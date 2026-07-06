using Rukhanka;
using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;

namespace BovineLabs.Timeline.Animation
{
    /// <summary>
    /// Inertialization (momentum-preserving transitions; David Bollo, GDC 2018). On a dominant-clip change the
    /// pose is cut to the target clip immediately and a per-bone offset is captured that starts where the previous
    /// pose was (matching position AND velocity) then decays to zero over a short window via the verified quintic.
    /// Zero cost except during the decay window. Opt-in per rig via
    /// <see cref="Authoring.TimelineAnimationStateAuthoring.inertializationDuration"/> (0 = off = current behavior).
    ///
    /// Runs after the Rukhanka IK injection group (post pose + IK) and before skinning, reading/writing bones via
    /// <see cref="AnimationStream"/> exactly as <c>DynamicBoneChainSystem</c> does (local get/set; the stream's
    /// Dispose flushes the local writes into the world-space buffer that <c>AnimationApplicationSystem</c> reads).
    /// </summary>
    [UpdateInGroup(typeof(RukhankaAnimationSystemGroup))]
    [UpdateAfter(typeof(RukhankaAnimationInjectionSystemGroup))]
    [UpdateBefore(typeof(AnimationApplicationSystem))]
    public partial struct InertializationSystem : ISystem
    {
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<RuntimeAnimationData>();

            var query = SystemAPI.QueryBuilder()
                .WithAll<RigDefinitionComponent, AnimationToProcessComponent>()
                .WithAllRW<InertializationState>()
                .WithAll<InertializationBoneState>()
                .Build();

            state.RequireForUpdate(query);
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            if (!SystemAPI.TryGetSingletonRW<RuntimeAnimationData>(out var runtimeData))
            {
                return;
            }

            var job = new InertializationJob
            {
                runtimeData = runtimeData.ValueRW,
                cullLookup = SystemAPI.GetComponentLookup<CullAnimationsTag>(true),
                deltaTime = SystemAPI.Time.DeltaTime,
            };

            job.ScheduleParallel();
        }

        [BurstCompile]
        private partial struct InertializationJob : IJobEntity
        {
            // Each rig writes only into its own bonePoseOffset range, so parallel scheduling never overlaps. The
            // safety restriction is disabled exactly as DynamicBoneChainSystem does for the shared runtime buffers.
            [NativeDisableContainerSafetyRestriction]
            public RuntimeAnimationData runtimeData;

            [Unity.Collections.ReadOnly]
            public ComponentLookup<CullAnimationsTag> cullLookup;

            public float deltaTime;

            private void Execute(
                Entity e,
                in RigDefinitionComponent rigDef,
                in DynamicBuffer<AnimationToProcessComponent> atps,
                ref InertializationState inert,
                ref DynamicBuffer<InertializationBoneState> bones)
            {
                // Culled rigs are frozen: no capture, no decay. Clear active so they resume cleanly when unculled.
                if (cullLookup.HasComponent(e) && cullLookup.IsComponentEnabled(e))
                {
                    inert.active = 0;
                    return;
                }

                using var animStream = AnimationStream.Create(runtimeData, rigDef);
                var boneCount = animStream.rigFrameData.rigBoneCount;
                if (boneCount <= 0)
                {
                    return;
                }

                // Lazy init / resize. The exact rig bone count is only reliably known at runtime (the rig blob is
                // built with bone-stripping masks at bake), so we size + seed the history here on the first frame.
                if (inert.initialized == 0 || bones.Length != boneCount)
                {
                    bones.ResizeUninitialized(boneCount);
                    for (var i = 0; i < boneCount; i++)
                    {
                        var pose = animStream.GetLocalPose(i);
                        bones[i] = new InertializationBoneState
                        {
                            prevDisplayed = pose,
                            prevPrevDisplayed = pose,
                            prevPrevPrevDisplayed = pose,
                        };
                    }

                    inert.active = 0;
                    inert.elapsed = 0f;
                    inert.initialized = 1;
                    inert.lastDominant = ComputeDominant(atps, out _, out var seedTime, out _, out _);
                    inert.lastDominantTime = seedTime;
                    inert.prevDominantTime = seedTime;
                    return;
                }

                var dt = deltaTime;

                // 1. Candidate dominant = highest-weight entry this frame (with its normalized time + clip length).
                var candidate = ComputeDominant(atps, out var hasDominant, out var dominantTime, out var dominantLen, out var candidateWeight);

                // 1b. Dominance hysteresis: two clips crossing at ~equal weight would otherwise flip the dominant back
                //     and forth across frames and re-capture on every flip. Only let a NEW candidate take over when it
                //     beats the currently-latched dominant by DominanceWeightMargin, or the old dominant no longer
                //     contributes at all. Otherwise keep tracking the old dominant (using ITS phase this frame).
                if (hasDominant && candidate != inert.lastDominant &&
                    TryFindMotion(atps, inert.lastDominant, out var oldWeight, out var oldTime, out var oldLen) &&
                    candidateWeight <= oldWeight + InertializationMath.DominanceWeightMargin)
                {
                    candidate = inert.lastDominant;
                    dominantTime = oldTime;
                    dominantLen = oldLen;
                }

                var dominant = candidate;

                // 2. Transition? Two triggers: (a) the dominant clip changed, or (b) the SAME clip's phase jumped
                //    discontinuously (a raw loop seam for a clip not using continuous-loop mode). The phase-jump test
                //    (see InertializationMath.IsPhaseJump) measures the deviation in SECONDS against a step-scaled
                //    tolerance, so a clean full-cycle wrap does NOT fire and frame-time jitter on short clips does not
                //    false-positive.
                //    dt > 0 guard avoids a divide-by-zero velocity on the very first stepped frame / paused frames.
                bool clipChanged = hasDominant && dominant != inert.lastDominant;
                bool phaseJump = false;
                if (hasDominant && dominant == inert.lastDominant && dt > 0f)
                {
                    phaseJump = InertializationMath.IsPhaseJump(
                        dominantTime, inert.lastDominantTime, inert.prevDominantTime, dominantLen);
                }

                if ((clipChanged || phaseJump) && inert.duration > 0f && dt > 0f)
                {
                    var invDt = 1f / dt;
                    for (var i = 0; i < boneCount; i++)
                    {
                        var bs = bones[i];
                        var target = animStream.GetLocalPose(i);

                        // Position channel (per component): x0 = prevDisplayed - target, v0 from displayed history.
                        bs.posOffset0 = bs.prevDisplayed.pos - target.pos;
                        bs.posVel0 = (bs.prevDisplayed.pos - bs.prevPrevDisplayed.pos) * invDt;

                        // Rotation channel: offset rotation reduced to a scalar angle about a fixed axis.
                        var qOffset = math.mul(bs.prevDisplayed.rot, math.inverse(target.rot));
                        InertializationMath.ToAngleAxis(qOffset, out var axis, out var angle);
                        bs.rotAxis = axis;
                        bs.rotAngle0 = angle;

                        // Offset-velocity = incoming displayed angular velocity projected onto the offset axis.
                        var qDelta = math.mul(bs.prevDisplayed.rot, math.inverse(bs.prevPrevDisplayed.rot));
                        InertializationMath.ToAngleAxis(qDelta, out var dAxis, out var dAngle);
                        bs.rotVel0 = math.dot(dAxis * (dAngle * invDt), axis);

                        // Acceleration (a0) from a second difference of the displayed history. Position is the direct
                        // second difference; rotation is the change of angular velocity (projected on the offset axis).
                        var invDt2 = invDt * invDt;
                        bs.posAcc0 = (bs.prevDisplayed.pos - 2f * bs.prevPrevDisplayed.pos + bs.prevPrevPrevDisplayed.pos) * invDt2;
                        var qd1 = math.mul(bs.prevDisplayed.rot, math.inverse(bs.prevPrevDisplayed.rot));
                        var qd0 = math.mul(bs.prevPrevDisplayed.rot, math.inverse(bs.prevPrevPrevDisplayed.rot));
                        InertializationMath.ToAngleAxis(qd1, out var a1ax, out var a1);
                        InertializationMath.ToAngleAxis(qd0, out var a0ax, out var a0a);
                        var w1 = math.dot(a1ax * a1, axis) * invDt;
                        var w0 = math.dot(a0ax * a0a, axis) * invDt;
                        bs.rotAcc0 = (w1 - w0) * invDt;

                        // Second differences amplify per-frame noise, so clamp a0 to a conservative bound before it can
                        // spike the quintic's A/B/C coefficients and overshoot. The natural acceleration scale over the
                        // decay window is ~|v0| / duration; cap each channel to a few times that, with a fixed floor so a
                        // near-zero v0 still tolerates a modest genuine a0. This is a loose blow-up guard, not a tight fit.
                        const float accVelScale = 4f;
                        const float accFloor = 50f;
                        var invDur = 1f / inert.duration; // duration > 0 guaranteed by the capture condition
                        var posAccCap = math.max(math.abs(bs.posVel0) * (accVelScale * invDur), accFloor);
                        bs.posAcc0 = math.clamp(bs.posAcc0, -posAccCap, posAccCap);
                        var rotAccCap = math.max(math.abs(bs.rotVel0) * (accVelScale * invDur), accFloor);
                        bs.rotAcc0 = math.clamp(bs.rotAcc0, -rotAccCap, rotAccCap);

                        bones[i] = bs;
                    }

                    inert.elapsed = 0f;
                    inert.active = 1;
                }

                // 3. Decay window expiry.
                var applying = inert.active == 1 && inert.duration > 0f && inert.elapsed < inert.duration;
                if (inert.active == 1 && inert.elapsed >= inert.duration)
                {
                    inert.active = 0;
                }

                // 3b + 4. Apply the offset (when active) and ALWAYS shift the displayed history.
                var t = inert.elapsed;
                var duration = inert.duration;
                for (var i = 0; i < boneCount; i++)
                {
                    var bs = bones[i];
                    var target = animStream.GetLocalPose(i);
                    var displayed = target;

                    if (applying)
                    {
                        var posOff = new float3(
                            InertializationMath.Quintic(bs.posOffset0.x, bs.posVel0.x, bs.posAcc0.x, t, duration),
                            InertializationMath.Quintic(bs.posOffset0.y, bs.posVel0.y, bs.posAcc0.y, t, duration),
                            InertializationMath.Quintic(bs.posOffset0.z, bs.posVel0.z, bs.posAcc0.z, t, duration));

                        var rotOff = InertializationMath.Quintic(bs.rotAngle0, bs.rotVel0, bs.rotAcc0, t, duration);

                        displayed.pos = target.pos + posOff;
                        displayed.rot = math.mul(quaternion.AxisAngle(bs.rotAxis, rotOff), target.rot);

                        animStream.SetLocalPose(i, displayed);
                    }

                    bs.prevPrevPrevDisplayed = bs.prevPrevDisplayed;
                    bs.prevPrevDisplayed = bs.prevDisplayed;
                    bs.prevDisplayed = displayed;
                    bones[i] = bs;
                }

                if (applying)
                {
                    inert.elapsed += dt;
                }

                // 5. Remember this frame's dominant (id + phase) for next-frame transition/phase-jump detection.
                if (hasDominant)
                {
                    inert.prevDominantTime = inert.lastDominantTime;
                    inert.lastDominantTime = dominantTime;
                    inert.lastDominant = dominant;
                }
            }

            // motionId (plus normalized time, clip length and weight) of the highest-weight AnimationToProcessComponent
            // entry; hasDominant=false if none contribute.
            private static uint ComputeDominant(
                in DynamicBuffer<AnimationToProcessComponent> atps, out bool hasDominant, out float dominantTime,
                out float dominantLen, out float dominantWeight)
            {
                hasDominant = false;
                uint dominant = 0;
                dominantTime = 0f;
                dominantLen = 0f;
                dominantWeight = 0f;
                var best = 0f;
                for (var i = 0; i < atps.Length; i++)
                {
                    var w = atps[i].weight;
                    if (w > best)
                    {
                        best = w;
                        dominant = atps[i].motionId;
                        dominantTime = atps[i].time;
                        dominantLen = atps[i].animation.IsCreated ? atps[i].animation.Value.length : 0f;
                        dominantWeight = w;
                        hasDominant = true;
                    }
                }

                return dominant;
            }

            // Weight/time/clip-length this frame of a specific motionId (the previously-latched dominant), for the
            // dominance-hysteresis compare. false if that motion no longer contributes.
            private static bool TryFindMotion(
                in DynamicBuffer<AnimationToProcessComponent> atps, uint motionId, out float weight, out float time,
                out float len)
            {
                weight = 0f;
                time = 0f;
                len = 0f;
                if (motionId == 0)
                {
                    return false;
                }

                for (var i = 0; i < atps.Length; i++)
                {
                    if (atps[i].motionId == motionId)
                    {
                        weight = atps[i].weight;
                        time = atps[i].time;
                        len = atps[i].animation.IsCreated ? atps[i].animation.Value.length : 0f;
                        return true;
                    }
                }

                return false;
            }
        }
    }
}
