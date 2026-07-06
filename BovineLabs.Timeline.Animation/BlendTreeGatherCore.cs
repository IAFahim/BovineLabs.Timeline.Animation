using System;
using BovineLabs.Core;
using BovineLabs.Core.Collections;
using BovineLabs.Core.Iterators;
using Rukhanka;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;
using Hash128 = Unity.Entities.Hash128;

namespace BovineLabs.Timeline.Animation
{
    /// <summary>
    /// Shared machinery for the 1D / 2D / Direct blend-tree track systems. The three systems are ~90% identical:
    /// they gather active clips per target, accumulate a weighted blend per source track, run a per-actor phase
    /// clock, ask a solver for per-motion weights, and emit one <see cref="BlendGroupEntry"/> per motion. Only the
    /// blend parameter (float / float2 / none), the weight solver, and the playback-state buffer type differ — those
    /// are supplied by each system as small structs implementing <see cref="IBlendParamOps{TParam}"/>,
    /// <see cref="IBlendTreeSolver{TParam}"/> and <see cref="IPlaybackStateAccess{TState}"/>. Everything else lives
    /// here, once.
    /// </summary>
    internal static class BlendTreeGatherCore
    {
        private const float WeightEpsilon = 0.0001f;
        private const float MinDuration = 0.001f;
        private const float MinWeight = 0.0001f;

        /// <summary>Weighted-average blend parameter arithmetic (float for 1D, float2 for 2D, none for Direct).</summary>
        public interface IBlendParamOps<TParam>
            where TParam : unmanaged
        {
            TParam MulAdd(TParam accumulated, TParam raw, float weight);

            TParam Div(TParam accumulated, float weight);
        }

        /// <summary>Reads/writes the per-actor playback-state buffer element without merging the (baked) buffer types.</summary>
        public interface IPlaybackStateAccess<TState>
            where TState : unmanaged, IBufferElementData
        {
            Entity GetTrack(in TState element);

            bool GetInitialized(in TState element);

            float GetAccumulated(in TState element);

            float GetPreviousAbsoluteTime(in TState element);

            TState Create(Entity track, bool initialized, float accumulatedTime, float previousAbsoluteTime);
        }

        /// <summary>Per-system motion prep + weight solve (owns the typed motion buffer and track-data lookups).</summary>
        public interface IBlendTreeSolver<TParam>
            where TParam : unmanaged
        {
            bool TryPrepare(
                Entity trackEntity,
                TParam blendedParameter,
                NativeHashMap<Hash128, BlobAssetReference<AnimationClipBlob>> animDB,
                BLLogger logger,
                out BlendTreeTrackConfig config,
                out NativeArray<BlobAssetReference<AnimationClipBlob>> clips,
                out NativeList<ScriptedAnimator.MotionIndexAndWeight> weights);
        }

        /// <summary>The track-level fields every blend-tree track carries; extracted by the solver from its track data.</summary>
        public struct BlendTreeTrackConfig
        {
            public int LayerIndex;
            public float3 TrackPositionOffset;
            public quaternion TrackRotationOffset;
            public bool ApplyAvatarMask;
            public Hash128 AvatarMaskHash;
        }

        /// <summary>Value stored per active clip in the per-target multi-hash map.</summary>
        public struct ClipData<TParam>
            where TParam : unmanaged
        {
            public Entity Track;
            public float AbsoluteTime;
            public TParam Parameter;
            public float Weight;
            public float TimeScale;
            public float3 PositionOffset;
            public quaternion RotationOffset;
            public bool RemoveStartOffset;
            public bool ApplyFootIK;
        }

        /// <summary>Per-source-track accumulation of all clips targeting one actor.</summary>
        public struct PerTrackBlend<TParam> : IComparable<PerTrackBlend<TParam>>
            where TParam : unmanaged
        {
            public Entity TrackEntity;
            public TParam Parameter;
            public float TotalWeight;
            public float BestWeight;
            public float AbsoluteTime;
            public float TimeScale;
            public float3 PositionOffset;
            public quaternion RotationOffset;
            public bool RemoveStartOffset;
            public bool ApplyFootIK;

            public int CompareTo(PerTrackBlend<TParam> other)
            {
                var cmp = this.TrackEntity.Index.CompareTo(other.TrackEntity.Index);
                if (cmp != 0)
                {
                    return cmp;
                }

                return this.TrackEntity.Version.CompareTo(other.TrackEntity.Version);
            }
        }

        /// <summary>Pure phase-clock state; extracted so the free-running cycle clock is unit-testable.</summary>
        public struct PhaseClockState
        {
            public bool Initialized;
            public float AccumulatedTime;
            public float PreviousAbsoluteTime;
        }

        /// <summary>
        /// Advances (or seeds) the free-running blend-tree phase clock and returns the wrapped normalized time.
        /// On first sight the phase is seeded from absolute time; thereafter it accrues the plausible per-frame
        /// step (<see cref="BlendTreePhaseMath.PlayingDelta"/>) unless scrubbing, when it follows absolute time
        /// directly. <see cref="math.frac"/> keeps the result in [0,1) for both forward and reverse accumulation.
        /// </summary>
        public static float AdvancePhase(
            ref PhaseClockState state,
            float absoluteTime,
            float weightedDuration,
            float timeScale,
            float globalDeltaTime,
            bool isScrubbing)
        {
            if (!state.Initialized)
            {
                var initialTime = absoluteTime / weightedDuration;
                state.Initialized = true;
                state.AccumulatedTime = initialTime;
                state.PreviousAbsoluteTime = absoluteTime;
                return math.frac(initialTime);
            }

            var delta = absoluteTime - state.PreviousAbsoluteTime;
            if (!isScrubbing)
            {
                delta = BlendTreePhaseMath.PlayingDelta(delta, globalDeltaTime * timeScale);
            }

            state.AccumulatedTime += delta / weightedDuration;
            state.PreviousAbsoluteTime = absoluteTime;
            return math.frac(state.AccumulatedTime);
        }

        /// <summary>Whether <paramref name="target"/> appears in the active-track set (drives orphan cleanup).</summary>
        public static bool ContainsEntity(ReadOnlySpan<Entity> entities, Entity target)
        {
            for (var i = 0; i < entities.Length; i++)
            {
                if (entities[i] == target)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Gathers, accumulates and emits blend-tree motions for one target actor. Called from each system's
        /// <c>DecomposeAndAppendBlendTree</c> job (one invocation per unique target entity).
        /// </summary>
        public static unsafe void Process<TParam, TOps, TState, TStateAccess, TSolver>(
            int index,
            NativeList<Entity> targetEntities,
            NativeParallelMultiHashMap<Entity, ClipData<TParam>>.ReadOnly clipDataMap,
            BufferLookup<BlendGroupEntry> blendGroupLookup,
            BufferLookup<TState> playbackStateLookup,
            NativeHashMap<Hash128, BlobAssetReference<AnimationClipBlob>> animDB,
            float globalDeltaTime,
            bool isScrubbing,
            BLLogger logger,
            TOps ops,
            TStateAccess stateAccess,
            TSolver solver)
            where TParam : unmanaged
            where TOps : unmanaged, IBlendParamOps<TParam>
            where TState : unmanaged, IBufferElementData
            where TStateAccess : unmanaged, IPlaybackStateAccess<TState>
            where TSolver : unmanaged, IBlendTreeSolver<TParam>
        {
            var targetEntity = targetEntities[index];

            if (!blendGroupLookup.HasBuffer(targetEntity))
            {
                return;
            }

            const int stackTrackCapacity = 128;
            var processedTracks = stackalloc PerTrackBlend<TParam>[stackTrackCapacity];
            var processedTrackCount = 0;
            var fallbackToMap = false;

            if (clipDataMap.TryGetFirstValue(targetEntity, out var clipData, out var it))
            {
                do
                {
                    var blendIndex = -1;
                    for (var i = 0; i < processedTrackCount; i++)
                    {
                        if (processedTracks[i].TrackEntity == clipData.Track)
                        {
                            blendIndex = i;
                            break;
                        }
                    }

                    if (blendIndex == -1)
                    {
                        if (processedTrackCount >= stackTrackCapacity)
                        {
                            fallbackToMap = true;
                            break;
                        }

                        blendIndex = processedTrackCount++;
                        processedTracks[blendIndex] = new PerTrackBlend<TParam> { TrackEntity = clipData.Track };
                    }

                    Accumulate(ref processedTracks[blendIndex], clipData, ops);
                }
                while (clipDataMap.TryGetNextValue(out clipData, ref it));
            }

            if (fallbackToMap)
            {
                ProcessTracksWithList(targetEntity, clipDataMap, blendGroupLookup, playbackStateLookup, animDB,
                    globalDeltaTime, isScrubbing, logger, ops, stateAccess, solver);
                return;
            }

            for (var i = 0; i < processedTrackCount; i++)
            {
                ProcessTrackBlend(targetEntity, processedTracks[i], blendGroupLookup, playbackStateLookup, animDB,
                    globalDeltaTime, isScrubbing, logger, ops, stateAccess, solver);
            }

            var activeTracks = stackalloc Entity[math.max(1, processedTrackCount)];
            for (var i = 0; i < processedTrackCount; i++)
            {
                activeTracks[i] = processedTracks[i].TrackEntity;
            }

            CleanupOrphanPlaybackStates(targetEntity, playbackStateLookup, stateAccess,
                new ReadOnlySpan<Entity>(activeTracks, processedTrackCount));
        }

        private static void Accumulate<TParam, TOps>(ref PerTrackBlend<TParam> blend, in ClipData<TParam> clip, TOps ops)
            where TParam : unmanaged
            where TOps : unmanaged, IBlendParamOps<TParam>
        {
            blend.Parameter = ops.MulAdd(blend.Parameter, clip.Parameter, clip.Weight);
            blend.TotalWeight += clip.Weight;

            if (clip.Weight > blend.BestWeight)
            {
                blend.BestWeight = clip.Weight;
                blend.AbsoluteTime = clip.AbsoluteTime;
                blend.TimeScale = clip.TimeScale;
                blend.PositionOffset = clip.PositionOffset;
                blend.RotationOffset = clip.RotationOffset;
                blend.RemoveStartOffset = clip.RemoveStartOffset;
                blend.ApplyFootIK = clip.ApplyFootIK;
            }
        }

        private static unsafe void ProcessTracksWithList<TParam, TOps, TState, TStateAccess, TSolver>(
            Entity targetEntity,
            NativeParallelMultiHashMap<Entity, ClipData<TParam>>.ReadOnly clipDataMap,
            BufferLookup<BlendGroupEntry> blendGroupLookup,
            BufferLookup<TState> playbackStateLookup,
            NativeHashMap<Hash128, BlobAssetReference<AnimationClipBlob>> animDB,
            float globalDeltaTime,
            bool isScrubbing,
            BLLogger logger,
            TOps ops,
            TStateAccess stateAccess,
            TSolver solver)
            where TParam : unmanaged
            where TOps : unmanaged, IBlendParamOps<TParam>
            where TState : unmanaged, IBufferElementData
            where TStateAccess : unmanaged, IPlaybackStateAccess<TState>
            where TSolver : unmanaged, IBlendTreeSolver<TParam>
        {
            var processedTracks = new UnsafeList<PerTrackBlend<TParam>>(16, Allocator.Temp);

            if (clipDataMap.TryGetFirstValue(targetEntity, out var clipData, out var it))
            {
                do
                {
                    var blendIndex = -1;
                    for (var i = 0; i < processedTracks.Length; i++)
                    {
                        if (processedTracks[i].TrackEntity == clipData.Track)
                        {
                            blendIndex = i;
                            break;
                        }
                    }

                    if (blendIndex == -1)
                    {
                        processedTracks.Add(new PerTrackBlend<TParam> { TrackEntity = clipData.Track });
                        blendIndex = processedTracks.Length - 1;
                    }

                    var blend = processedTracks[blendIndex];
                    Accumulate(ref blend, clipData, ops);
                    processedTracks[blendIndex] = blend;
                }
                while (clipDataMap.TryGetNextValue(out clipData, ref it));
            }

            processedTracks.Sort();

            for (var i = 0; i < processedTracks.Length; i++)
            {
                ProcessTrackBlend(targetEntity, processedTracks[i], blendGroupLookup, playbackStateLookup, animDB,
                    globalDeltaTime, isScrubbing, logger, ops, stateAccess, solver);
            }

            var activeTracks = new NativeArray<Entity>(math.max(1, processedTracks.Length), Allocator.Temp);
            for (var i = 0; i < processedTracks.Length; i++)
            {
                activeTracks[i] = processedTracks[i].TrackEntity;
            }

            CleanupOrphanPlaybackStates(targetEntity, playbackStateLookup, stateAccess,
                new ReadOnlySpan<Entity>(activeTracks.GetUnsafeReadOnlyPtr(), processedTracks.Length));

            activeTracks.Dispose();
            processedTracks.Dispose();
        }

        private static void ProcessTrackBlend<TParam, TOps, TState, TStateAccess, TSolver>(
            Entity targetEntity,
            in PerTrackBlend<TParam> blend,
            BufferLookup<BlendGroupEntry> blendGroupLookup,
            BufferLookup<TState> playbackStateLookup,
            NativeHashMap<Hash128, BlobAssetReference<AnimationClipBlob>> animDB,
            float globalDeltaTime,
            bool isScrubbing,
            BLLogger logger,
            TOps ops,
            TStateAccess stateAccess,
            TSolver solver)
            where TParam : unmanaged
            where TOps : unmanaged, IBlendParamOps<TParam>
            where TState : unmanaged, IBufferElementData
            where TStateAccess : unmanaged, IPlaybackStateAccess<TState>
            where TSolver : unmanaged, IBlendTreeSolver<TParam>
        {
            if (blend.TotalWeight <= 0f)
            {
                return;
            }

            var totalTimelineWeight = math.saturate(blend.TotalWeight);
            var blendedParameter = ops.Div(blend.Parameter, math.max(MinWeight, blend.TotalWeight));

            if (!solver.TryPrepare(blend.TrackEntity, blendedParameter, animDB, logger, out var config,
                    out var clips, out var weights))
            {
                return;
            }

            if (!blendGroupLookup.TryGetBuffer(targetEntity, out var blendGroupBuffer))
            {
                weights.Dispose();
                clips.Dispose();
                return;
            }

            var weightedDuration = 0f;
            var totalBlendWeight = 0f;

            for (var i = 0; i < weights.Length; i++)
            {
                var mw = weights[i];
                if (clips[mw.motionIndex].IsCreated)
                {
                    weightedDuration += clips[mw.motionIndex].Value.length * mw.weight;
                    totalBlendWeight += mw.weight;
                }
            }

            if (totalBlendWeight > 0f)
            {
                weightedDuration /= totalBlendWeight;
            }

            if (weightedDuration <= MinDuration)
            {
                weightedDuration = 1f;
            }

            var normalizedTime = ComputeNormalizedTime(playbackStateLookup, stateAccess, targetEntity,
                blend.TrackEntity, blend.AbsoluteTime, weightedDuration, blend.TimeScale, globalDeltaTime, isScrubbing);

            var avatarMaskHash = config.ApplyAvatarMask ? config.AvatarMaskHash : default;
            var finalPosOffset = config.TrackPositionOffset + math.rotate(config.TrackRotationOffset, blend.PositionOffset);
            var finalRotOffset = math.mul(config.TrackRotationOffset, blend.RotationOffset);
            var trackHasOffsets = math.lengthsq(config.TrackPositionOffset) > WeightEpsilon ||
                                  math.lengthsq(config.TrackRotationOffset.value.xyz) > WeightEpsilon;
            var removeStartOffset = blend.RemoveStartOffset || trackHasOffsets;

            for (var i = 0; i < weights.Length; i++)
            {
                var mw = weights[i];
                var clipBlob = clips[mw.motionIndex];
                if (!clipBlob.IsCreated || mw.weight <= 0f)
                {
                    continue;
                }

                var clipHash = clipBlob.Value.hash;
                blendGroupBuffer.Add(new BlendGroupEntry
                {
                    LayerIndex = config.LayerIndex,
                    ClipHash = clipHash,
                    NormalizedTime = normalizedTime,
                    Weight = mw.weight * totalTimelineWeight,
                    AvatarMaskHash = avatarMaskHash,
                    BlendMode = AnimationBlendingMode.Override,

                    // Each blend-tree motion slot is a distinct instance; key by its slot index so two motions
                    // referencing the same clip on this track+layer do not collapse into one.
                    MotionId = MotionId.ComputeForMotion(blend.TrackEntity, config.LayerIndex, clipHash, mw.motionIndex),
                    PositionOffset = finalPosOffset,
                    RotationOffset = finalRotOffset,
                    RemoveStartOffset = removeStartOffset,
                    ApplyFootIK = blend.ApplyFootIK,

                    // Per-timeline playback speed of the best-weight clip; the unification system slows the fallback
                    // clock and crossfade ramps by the dominant clip's TimeScale (<= 0 is treated as 1).
                    TimeScale = blend.TimeScale,
                });
            }

            weights.Dispose();
            clips.Dispose();
        }

        private static float ComputeNormalizedTime<TState, TStateAccess>(
            BufferLookup<TState> playbackStateLookup,
            TStateAccess stateAccess,
            Entity targetEntity,
            Entity trackEntity,
            float absoluteTime,
            float weightedDuration,
            float timeScale,
            float globalDeltaTime,
            bool isScrubbing)
            where TState : unmanaged, IBufferElementData
            where TStateAccess : unmanaged, IPlaybackStateAccess<TState>
        {
            if (!playbackStateLookup.TryGetBuffer(targetEntity, out var stateBuffer))
            {
                return 0f;
            }

            var stateIdx = -1;
            for (var i = 0; i < stateBuffer.Length; i++)
            {
                if (stateAccess.GetTrack(stateBuffer[i]) == trackEntity)
                {
                    stateIdx = i;
                    break;
                }
            }

            if (stateIdx == -1)
            {
                stateIdx = stateBuffer.Length;
                stateBuffer.Add(stateAccess.Create(trackEntity, false, 0f, 0f));
            }

            var element = stateBuffer[stateIdx];
            var clock = new PhaseClockState
            {
                Initialized = stateAccess.GetInitialized(element),
                AccumulatedTime = stateAccess.GetAccumulated(element),
                PreviousAbsoluteTime = stateAccess.GetPreviousAbsoluteTime(element),
            };

            var normalizedTime = AdvancePhase(ref clock, absoluteTime, weightedDuration, timeScale, globalDeltaTime,
                isScrubbing);

            stateBuffer[stateIdx] = stateAccess.Create(trackEntity, clock.Initialized, clock.AccumulatedTime,
                clock.PreviousAbsoluteTime);

            return normalizedTime;
        }

        private static void CleanupOrphanPlaybackStates<TState, TStateAccess>(
            Entity targetEntity,
            BufferLookup<TState> playbackStateLookup,
            TStateAccess stateAccess,
            ReadOnlySpan<Entity> activeTracks)
            where TState : unmanaged, IBufferElementData
            where TStateAccess : unmanaged, IPlaybackStateAccess<TState>
        {
            if (!playbackStateLookup.TryGetBuffer(targetEntity, out var stateBuffer))
            {
                return;
            }

            for (var i = stateBuffer.Length - 1; i >= 0; i--)
            {
                if (!ContainsEntity(activeTracks, stateAccess.GetTrack(stateBuffer[i])))
                {
                    stateBuffer.RemoveAtSwapBack(i);
                }
            }
        }
    }
}
