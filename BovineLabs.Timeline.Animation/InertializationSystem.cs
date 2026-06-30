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
                        };
                    }

                    inert.active = 0;
                    inert.elapsed = 0f;
                    inert.initialized = 1;
                    inert.lastDominant = ComputeDominant(atps, out _);
                    return;
                }

                var dt = deltaTime;

                // 1. Dominant = motionId of the highest-weight entry this frame.
                var dominant = ComputeDominant(atps, out var hasDominant);

                // 2. Transition? Capture a fresh offset from last frame's displayed history + this frame's target.
                //    dt > 0 guard avoids a divide-by-zero velocity on the very first stepped frame / paused frames.
                if (hasDominant && dominant != inert.lastDominant && inert.duration > 0f && dt > 0f)
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
                        ToAngleAxis(qOffset, out var axis, out var angle);
                        bs.rotAxis = axis;
                        bs.rotAngle0 = angle;

                        // Offset-velocity = incoming displayed angular velocity projected onto the offset axis.
                        var qDelta = math.mul(bs.prevDisplayed.rot, math.inverse(bs.prevPrevDisplayed.rot));
                        ToAngleAxis(qDelta, out var dAxis, out var dAngle);
                        bs.rotVel0 = math.dot(dAxis * (dAngle * invDt), axis);

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
                            Quintic(bs.posOffset0.x, bs.posVel0.x, t, duration),
                            Quintic(bs.posOffset0.y, bs.posVel0.y, t, duration),
                            Quintic(bs.posOffset0.z, bs.posVel0.z, t, duration));

                        var rotOff = Quintic(bs.rotAngle0, bs.rotVel0, t, duration);

                        displayed.pos = target.pos + posOff;
                        displayed.rot = math.mul(quaternion.AxisAngle(bs.rotAxis, rotOff), target.rot);

                        animStream.SetLocalPose(i, displayed);
                    }

                    bs.prevPrevDisplayed = bs.prevDisplayed;
                    bs.prevDisplayed = displayed;
                    bones[i] = bs;
                }

                if (applying)
                {
                    inert.elapsed += dt;
                }

                // 5. Remember this frame's dominant for next-frame transition detection.
                if (hasDominant)
                {
                    inert.lastDominant = dominant;
                }
            }

            // motionId of the highest-weight AnimationToProcessComponent entry; hasDominant=false if none contribute.
            private static uint ComputeDominant(in DynamicBuffer<AnimationToProcessComponent> atps, out bool hasDominant)
            {
                hasDominant = false;
                uint dominant = 0;
                var best = 0f;
                for (var i = 0; i < atps.Length; i++)
                {
                    var w = atps[i].weight;
                    if (w > best)
                    {
                        best = w;
                        dominant = atps[i].motionId;
                        hasDominant = true;
                    }
                }

                return dominant;
            }

            // Verified quintic closed-form for one scalar channel (a0 = 0). x(0)=x0, x'(0)=v0, x(T)=x'(T)=x''(T)=0.
            // Per-channel overshoot guard shortens the effective window when the offset is already closing on zero.
            private static float Quintic(float x0, float v0, float t, float duration)
            {
                var teff = duration;
                if (math.abs(v0) > 1e-9f)
                {
                    var guard = -5f * x0 / v0;
                    if (guard > 0f && guard < teff)
                    {
                        teff = guard;
                    }
                }

                if (teff <= 1e-9f || t >= teff)
                {
                    return 0f;
                }

                var t2 = teff * teff;
                var t3 = t2 * teff;
                var t4 = t3 * teff;
                var t5 = t4 * teff;

                var a = -(6f * v0 * teff + 12f * x0) / (2f * t5);
                var b = (16f * v0 * teff + 30f * x0) / (2f * t4);
                var c = -(12f * v0 * teff + 20f * x0) / (2f * t3);

                var p2 = t * t;
                var p3 = p2 * t;
                var p4 = p3 * t;
                var p5 = p4 * t;

                return (a * p5) + (b * p4) + (c * p3) + (v0 * t) + x0;
            }

            // Quaternion -> shortest-arc angle (>= 0) about a unit axis. Mirrors Unity's ToAngleAxis semantics.
            private static void ToAngleAxis(quaternion q, out float3 axis, out float angle)
            {
                var v = math.normalize(q).value;
                if (v.w < 0f)
                {
                    v = -v; // shortest arc
                }

                var w = math.clamp(v.w, -1f, 1f);
                angle = 2f * math.acos(w);

                var s = math.sqrt(math.max(0f, 1f - (w * w)));
                axis = s < 1e-6f ? new float3(0f, 1f, 0f) : v.xyz / s;
            }
        }
    }
}
