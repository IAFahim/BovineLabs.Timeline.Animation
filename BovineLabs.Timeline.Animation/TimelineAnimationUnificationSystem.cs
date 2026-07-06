using BovineLabs.Core;
using Rukhanka;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using Hash128 = Unity.Entities.Hash128;

namespace BovineLabs.Timeline.Animation
{
    [UpdateInGroup(typeof(TimelineComponentAnimationGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.LocalSimulation | WorldSystemFilterFlags.ClientSimulation |
                       WorldSystemFilterFlags.ServerSimulation)]
    public partial struct TimelineAnimationUnificationSystem : ISystem
    {
        private const float WeightEpsilon = 0.0001f;

        private const float MinDuration = 0.001f;

        private EntityQuery _gpuGuardQuery;
        private NativeReference<bool> _gpuWarned;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<BlobDatabaseSingleton>();

            // GPU parity guard (#3): actors carrying timeline-animation state (BlendGroupTimer) whose rig is on the
            // GPU animation path (enabled GPUAnimationEngineTag). The GPU AnimationToProcess struct never gained the
            // offset/removeStartOffset parity fields, so those features silently no-op there.
            _gpuGuardQuery = SystemAPI.QueryBuilder().WithAll<BlendGroupTimer, GPUAnimationEngineTag>().Build();
            _gpuWarned = new NativeReference<bool>(Allocator.Persistent);
        }

        public void OnDestroy(ref SystemState state)
        {
            _gpuWarned.Dispose();
        }

        // Advances a fading clip's normalized time. Looped clips wrap (frac); clamped clips pin at 1. Extracted so the
        // integration math can be unit-tested independently of the Application.isPlaying scrub gate.
        internal static float AdvanceNormalizedTime(float normalizedTime, float advance, bool looped)
        {
            return looped ? math.frac(normalizedTime + advance) : math.min(1f, normalizedTime + advance);
        }

        // Whether a reconciled entry should re-sync its NormalizedTime to the request's. Continuous-loop entries own
        // their free-running phase once seeded and are only re-synced while scrubbing; everyone else tracks the
        // request exactly. Extracted for unit testing.
        internal static bool ShouldResyncPhase(bool continuousLoop, bool isScrubbing, bool phaseSeeded)
        {
            return !continuousLoop || isScrubbing || !phaseSeeded;
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var blobDB = SystemAPI.GetSingleton<BlobDatabaseSingleton>();

            if (!_gpuWarned.Value && !_gpuGuardQuery.IsEmptyIgnoreFilter && !_gpuGuardQuery.IsEmpty)
            {
                _gpuWarned.Value = true;
                SystemAPI.GetSingleton<BLLogger>().LogError512(
                    "[TimelineAnimation] A rig on the GPU animation path (GPUAnimationEngineTag) has timeline-animation " +
                    "state. Timeline-animation features (position/rotation offsets, removeStartOffset, continuous loop, " +
                    "inertialization) are UNSUPPORTED on the GPU animation engine and will be silently ignored.");
            }

            var isScrubbing = false;
#if UNITY_EDITOR
            isScrubbing = !Application.isPlaying;
#endif

            var job = new UnifyAnimationsJob
            {
                AnimDB = blobDB.animations,
                MaskDB = blobDB.avatarMasks,
                DeltaTime = SystemAPI.Time.DeltaTime,
                IsScrubbing = isScrubbing,
                // Optional per-actor layer-weight overrides written by TimelineLayerWeightTrackSystem. Absent on
                // any actor with no LayerWeight track, in which case the multiplier defaults to 1 (no change).
                LayerOverrides = SystemAPI.GetBufferLookup<LayerWeightOverride>(true),
                CullLookup = SystemAPI.GetComponentLookup<CullAnimationsTag>(true),
            };

            state.Dependency = job.ScheduleParallel(state.Dependency);
        }

        private const int LayerSumCapacity = 64;

        [BurstCompile]
        private partial struct UnifyAnimationsJob : IJobEntity
        {
            [ReadOnly] public NativeHashMap<Hash128, BlobAssetReference<AnimationClipBlob>> AnimDB;
            [ReadOnly] public NativeHashMap<Hash128, BlobAssetReference<AvatarMaskBlob>> MaskDB;

            // Optional, per-actor: authored layer-weight overrides keyed by LayerIndex. Absent => multiplier 1.
            [ReadOnly] public BufferLookup<LayerWeightOverride> LayerOverrides;

            // Rukhanka culls pose computation for off-screen rigs via CullAnimationsTag. Absent on most actors.
            [ReadOnly] public ComponentLookup<CullAnimationsTag> CullLookup;

            public float DeltaTime;
            public bool IsScrubbing;

            public void Execute(
                Entity actor,
                ref BlendGroupTimer timer,
                in FallbackBlend fallbackData,
                ref DynamicBuffer<BlendGroupEntry> blendEntries,
                ref DynamicBuffer<SmoothBlendGroupEntry> smoothEntries,
                ref DynamicBuffer<AnimationToProcessComponent> atps)
            {
                atps.Clear();

                // Off-screen rig (#15): Rukhanka skips pose computation for it, so integrating weights and emitting
                // clips is wasted. Snap CurrentWeight to TargetWeight so an un-cull resumes without a half-blended
                // pop, discard this frame's gathered requests, and emit nothing.
                if (CullLookup.HasComponent(actor) && CullLookup.IsComponentEnabled(actor))
                {
                    for (var i = 0; i < smoothEntries.Length; i++)
                    {
                        var s = smoothEntries[i];
                        s.CurrentWeight = s.TargetWeight;
                        smoothEntries[i] = s;
                    }

                    blendEntries.Clear();
                    return;
                }

                var timeScale = ResolveTimeScale(in blendEntries, ref timer);

                ReconcileRequests(ref blendEntries, ref smoothEntries, IsScrubbing);
                IntegrateWeights(ref smoothEntries, fallbackData.BlendInSpeed, fallbackData.BlendOutSpeed, timeScale);

                var baseLayer = fallbackData.LayerIndex;
                var baseControl = BaseLayerControl(in smoothEntries, baseLayer);

                EmitFallback(ref timer, in fallbackData, baseControl, timeScale, ref atps);

                var layerSums = AccumulateOverrideSums(in smoothEntries);
                EmitClips(actor, in smoothEntries, baseLayer, baseControl, in layerSums, ref atps);

                SortByLayer(ref atps);

                blendEntries.Clear();
            }

            // Resolves the per-timeline playback speed that drives the fallback clock and crossfade ramps: the
            // dominant (best-weight) active clip's TimeScale, or 1 when no clips are active (pure fallback idle) so a
            // stale scale from a finished clip does not linger. Blend-tree requests leave TimeScale 0 until they
            // thread scale; a resolved value of <=0 is treated as 1 (unscaled).
            private static float ResolveTimeScale(
                in DynamicBuffer<BlendGroupEntry> blendEntries,
                ref BlendGroupTimer timer)
            {
                if (blendEntries.Length == 0)
                {
                    timer.TimeScale = 1f;
                    return 1f;
                }

                var bestWeight = -1f;
                var bestScale = 1f;
                for (var i = 0; i < blendEntries.Length; i++)
                {
                    var e = blendEntries[i];
                    if (e.Weight > bestWeight)
                    {
                        bestWeight = e.Weight;
                        bestScale = e.TimeScale;
                    }
                }

                var scale = bestScale > 0f ? bestScale : 1f;
                timer.TimeScale = scale;
                return scale;
            }

            private static void ReconcileRequests(
                ref DynamicBuffer<BlendGroupEntry> blendEntries,
                ref DynamicBuffer<SmoothBlendGroupEntry> smoothEntries,
                bool isScrubbing)
            {
                for (var i = 0; i < smoothEntries.Length; i++)
                {
                    var s = smoothEntries[i];
                    s.TargetWeight = 0f;
                    smoothEntries[i] = s;
                }

                for (var i = 0; i < blendEntries.Length; i++)
                {
                    var request = blendEntries[i];
                    var smoothIndex = -1;

                    for (var j = 0; j < smoothEntries.Length; j++)
                        if (smoothEntries[j].MotionId == request.MotionId)
                        {
                            smoothIndex = j;
                            break;
                        }

                    if (smoothIndex != -1)
                    {
                        var s = smoothEntries[smoothIndex];
                        s.TargetWeight = request.Weight;
                        s.ContinuousLoop = request.ContinuousLoop;
                        s.PhaseVelocity = request.PhaseVelocity;

                        // Continuous-loop entries free-run their own phase (advanced in IntegrateWeights) and must
                        // NOT be re-synced to the wrapping timeline localTime, otherwise the seam snaps back. We
                        // still take the request's NormalizedTime while scrubbing (scrub must land exactly), or the
                        // very first time to seed the phase. Non-continuous entries track localTime exactly, as before.
                        if (ShouldResyncPhase(s.ContinuousLoop, isScrubbing, s.PhaseSeeded))
                        {
                            s.NormalizedTime = request.NormalizedTime;
                            if (s.ContinuousLoop && !isScrubbing)
                                s.PhaseSeeded = true;
                        }

                        s.LayerIndex = request.LayerIndex;
                        s.BlendMode = request.BlendMode;
                        s.AvatarMaskHash = request.AvatarMaskHash;
                        s.MotionId = request.MotionId;

                        s.PositionOffset = request.PositionOffset;
                        s.RotationOffset = request.RotationOffset;
                        s.RemoveStartOffset = request.RemoveStartOffset;
                        s.ApplyFootIK = request.ApplyFootIK;

                        smoothEntries[smoothIndex] = s;
                    }
                    else
                    {
                        smoothEntries.Add(new SmoothBlendGroupEntry
                        {
                            LayerIndex = request.LayerIndex,
                            ClipHash = request.ClipHash,
                            NormalizedTime = request.NormalizedTime,
                            CurrentWeight = 0f,
                            TargetWeight = request.Weight,
                            BlendMode = request.BlendMode,
                            AvatarMaskHash = request.AvatarMaskHash,
                            MotionId = request.MotionId,

                            PositionOffset = request.PositionOffset,
                            RotationOffset = request.RotationOffset,
                            RemoveStartOffset = request.RemoveStartOffset,
                            ApplyFootIK = request.ApplyFootIK,

                            // Seed the free-run phase from the request on first appearance; a continuous entry is
                            // considered seeded immediately so subsequent frames own NormalizedTime themselves.
                            ContinuousLoop = request.ContinuousLoop,
                            PhaseVelocity = request.PhaseVelocity,
                            PhaseSeeded = request.ContinuousLoop
                        });
                    }
                }
            }

            private void IntegrateWeights(
                ref DynamicBuffer<SmoothBlendGroupEntry> smoothEntries,
                float blendInSpeed,
                float blendOutSpeed,
                float timeScale)
            {
                var blendInDur = blendInSpeed <= WeightEpsilon ? 0f : 1f / blendInSpeed;
                var blendOutDur = blendOutSpeed <= WeightEpsilon ? 0f : 1f / blendOutSpeed;

                for (var i = smoothEntries.Length - 1; i >= 0; i--)
                {
                    var s = smoothEntries[i];

                    var hasClip = AnimDB.TryGetValue(s.ClipHash, out var clipBlob) && clipBlob.IsCreated;
                    var clipLen = hasClip ? math.max(MinDuration, clipBlob.Value.length) : MinDuration;

                    if (IsScrubbing)
                    {
                        s.CurrentWeight = s.TargetWeight;
                    }
                    else
                    {
                        var rising = s.TargetWeight >= s.CurrentWeight;
                        var floorDur = math.min(rising ? blendInDur : blendOutDur, clipLen * 0.5f);
                        var maxStep = floorDur <= WeightEpsilon ? 1f : DeltaTime * timeScale / floorDur;
                        s.CurrentWeight += math.clamp(s.TargetWeight - s.CurrentWeight, -maxStep, maxStep);
                    }

                    if (s.CurrentWeight <= WeightEpsilon && s.TargetWeight <= WeightEpsilon)
                    {
                        smoothEntries.RemoveAtSwapBack(i);
                        continue;
                    }

                    if (s.ContinuousLoop)
                    {
                        // Continuous-phase loop: advance this entry's OWN phase by PhaseVelocity (cycles/sec) every
                        // frame and never read the wrapping timeline localTime, so the loop seam is invisible
                        // regardless of timeline duration. Runs whether rising, held, or fading out.
                        var adv = (IsScrubbing ? 0f : DeltaTime) * s.PhaseVelocity;
                        s.NormalizedTime = math.frac(s.NormalizedTime + adv);
                    }
                    else if (s.TargetWeight <= WeightEpsilon && hasClip)
                    {
                        var advance = (IsScrubbing ? 0f : DeltaTime) / clipLen;
                        s.NormalizedTime = AdvanceNormalizedTime(s.NormalizedTime, advance, clipBlob.Value.looped);
                    }

                    smoothEntries[i] = s;
                }
            }

            private static float BaseLayerControl(
                in DynamicBuffer<SmoothBlendGroupEntry> smoothEntries,
                int baseLayer)
            {
                var baseSum = 0f;
                for (var i = 0; i < smoothEntries.Length; i++)
                {
                    var e = smoothEntries[i];
                    if (e.BlendMode != AnimationBlendingMode.Override || e.LayerIndex != baseLayer)
                        continue;

                    baseSum += e.CurrentWeight;
                }

                return math.saturate(baseSum);
            }

            private void EmitFallback(
                ref BlendGroupTimer timer,
                in FallbackBlend fallbackData,
                float baseControl,
                float timeScale,
                ref DynamicBuffer<AnimationToProcessComponent> atps)
            {
                var fallbackWeight = BlendLayerMath.FallbackWeight(baseControl);

                if (fallbackWeight <= WeightEpsilon || fallbackData.ClipHash == default)
                    return;

                if (!AnimDB.TryGetValue(fallbackData.ClipHash, out var fallbackClip) || !fallbackClip.IsCreated)
                    return;

                if (timer.PreviousFallbackClipHash != fallbackData.ClipHash)
                {
                    timer.FallbackAccumulatedTime = 0f;
                    timer.PreviousFallbackClipHash = fallbackData.ClipHash;
                }

                var duration = math.max(MinDuration, fallbackClip.Value.length);

                var fallbackAdvance = (IsScrubbing ? 0f : DeltaTime) * timeScale / duration;

                if (fallbackData.PlaybackMode == FallbackPlaybackMode.Hold)
                {
                    if (timer.FallbackAccumulatedTime < 1f)
                        timer.FallbackAccumulatedTime += fallbackAdvance;
                }
                else
                {
                    timer.FallbackAccumulatedTime += fallbackAdvance;
                }

                var fallbackTime = fallbackData.PlaybackMode == FallbackPlaybackMode.Loop
                    ? math.frac(timer.FallbackAccumulatedTime)
                    : math.min(timer.FallbackAccumulatedTime, 1f);

                atps.Add(new AnimationToProcessComponent
                {
                    animation = fallbackClip,
                    avatarMask = ResolveMask(fallbackData.AvatarMaskHash),
                    time = fallbackTime,
                    weight = fallbackWeight,
                    blendMode = fallbackData.BlendMode,
                    layerIndex = fallbackData.LayerIndex,
                    layerWeight = 1.0f,
                    motionId = MotionId.Fallback,

                    positionOffset = fallbackData.PositionOffset,
                    rotationOffset = fallbackData.RotationOffset,
                    removeStartOffset = fallbackData.RemoveStartOffset,
                    applyFootIK = fallbackData.ApplyFootIK
                });
            }

            private static FixedList512Bytes<float> AccumulateOverrideSums(
                in DynamicBuffer<SmoothBlendGroupEntry> entries)
            {
                var sums = default(FixedList512Bytes<float>);
                sums.Length = LayerSumCapacity;
                for (var i = 0; i < LayerSumCapacity; i++)
                    sums[i] = 0f;

                for (var i = 0; i < entries.Length; i++)
                {
                    var e = entries[i];
                    if (e.BlendMode == AnimationBlendingMode.Override && (uint)e.LayerIndex < LayerSumCapacity)
                        sums[e.LayerIndex] += e.CurrentWeight;
                }

                return sums;
            }

            private void EmitClips(
                Entity actor,
                in DynamicBuffer<SmoothBlendGroupEntry> smoothEntries,
                int baseLayer,
                float baseControl,
                in FixedList512Bytes<float> layerSums,
                ref DynamicBuffer<AnimationToProcessComponent> atps)
            {
                for (var i = 0; i < smoothEntries.Length; i++)
                {
                    var s = smoothEntries[i];
                    if (!AnimDB.TryGetValue(s.ClipHash, out var clipBlob) || !clipBlob.IsCreated)
                        continue;

                    float weight;
                    float layerWeight;

                    if (s.BlendMode == AnimationBlendingMode.Override)
                    {
                        var layerSum = (uint)s.LayerIndex < LayerSumCapacity
                            ? layerSums[s.LayerIndex]
                            : OverrideSumForLayer(in smoothEntries, s.LayerIndex);

                        if (s.LayerIndex == baseLayer)
                        {
                            weight = BlendLayerMath.NormalizeBaseLayerWeight(s.CurrentWeight, baseControl, layerSum);
                            layerWeight = 1.0f;
                        }
                        else
                        {
                            weight = BlendLayerMath.NormalizeAdditionalLayerWeight(s.CurrentWeight, layerSum);
                            layerWeight = BlendLayerMath.AdditionalLayerWeight(layerSum);
                        }
                    }
                    else
                    {
                        weight = s.CurrentWeight;
                        layerWeight = 1.0f;
                    }

                    // LAYER-WEIGHT OVERRIDE (optional): a LayerWeight timeline track can fade a whole animation
                    // layer in/out. The authored multiplier (1 when absent => unchanged) scales this layer's
                    // overall weight. Applied to layerWeight — the per-layer stacking amount Rukhanka consumes in
                    // BlendLayerPose/ApplyLayerValue (lerp for Override, add for Additive) — AFTER the
                    // BlendLayerMath normalization above, so the within-layer clip distribution (weight) is left
                    // intact and the override composes with the normalization rather than fighting it.
                    layerWeight *= LayerOverrideMultiplier(actor, s.LayerIndex);

                    atps.Add(new AnimationToProcessComponent
                    {
                        animation = clipBlob,
                        avatarMask = ResolveMask(s.AvatarMaskHash),
                        time = s.NormalizedTime,
                        weight = weight,
                        blendMode = s.BlendMode,
                        layerIndex = s.LayerIndex,
                        layerWeight = layerWeight,
                        motionId = s.MotionId,

                        positionOffset = s.PositionOffset,
                        rotationOffset = s.RotationOffset,
                        removeStartOffset = s.RemoveStartOffset,
                        applyFootIK = s.ApplyFootIK
                    });
                }
            }

            // Looks up the authored layer-weight multiplier for a given actor + layer. Returns 1 (no change) when
            // the actor has no override buffer (no LayerWeight track) or no entry for this layer.
            private float LayerOverrideMultiplier(Entity actor, int layerIndex)
            {
                if (!LayerOverrides.TryGetBuffer(actor, out var overrides))
                    return 1f;

                for (var i = 0; i < overrides.Length; i++)
                    if (overrides[i].LayerIndex == layerIndex)
                        return overrides[i].Multiplier;

                return 1f;
            }

            private static float OverrideSumForLayer(in DynamicBuffer<SmoothBlendGroupEntry> entries, int layer)
            {
                var sum = 0f;
                for (var i = 0; i < entries.Length; i++)
                {
                    var e = entries[i];
                    if (e.BlendMode == AnimationBlendingMode.Override && e.LayerIndex == layer)
                        sum += e.CurrentWeight;
                }

                return sum;
            }

            private readonly BlobAssetReference<AvatarMaskBlob> ResolveMask(Hash128 maskHash)
            {
                if (maskHash != default && MaskDB.TryGetValue(maskHash, out var mask) && mask.IsCreated)
                    return mask;

                return default;
            }

            private static void SortByLayer(ref DynamicBuffer<AnimationToProcessComponent> atps)
            {
                for (var i = 1; i < atps.Length; i++)
                {
                    var key = atps[i];
                    var j = i - 1;
                    while (j >= 0 && atps[j].layerIndex > key.layerIndex)
                    {
                        atps[j + 1] = atps[j];
                        j--;
                    }

                    atps[j + 1] = key;
                }
            }
        }
    }
}