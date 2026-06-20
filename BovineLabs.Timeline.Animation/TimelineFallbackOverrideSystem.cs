using BovineLabs.Core.Extensions;
using BovineLabs.Core.Iterators;
using BovineLabs.Timeline.Data;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;

namespace BovineLabs.Timeline.Animation
{
    [UpdateInGroup(typeof(TimelineComponentAnimationGroup))]
    [UpdateBefore(typeof(TimelineAnimationUnificationSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.LocalSimulation | WorldSystemFilterFlags.ClientSimulation |
                       WorldSystemFilterFlags.ServerSimulation)]
    public partial struct TimelineFallbackOverrideSystem : ISystem
    {
        private NativeParallelMultiHashMap<Entity, TrackFallbackOverride> _candidates;
        private NativeList<Entity> _targets;
        private EntityQuery _activeClips;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            _candidates = new NativeParallelMultiHashMap<Entity, TrackFallbackOverride>(64, Allocator.Persistent);
            _targets = new NativeList<Entity>(64, Allocator.Persistent);
            _activeClips = SystemAPI.QueryBuilder()
                .WithAll<ClipActive, TimelineActive, Clip, TrackBinding>().Build();
        }

        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
            if (_candidates.IsCreated) _candidates.Dispose();
            if (_targets.IsCreated) _targets.Dispose();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var count = _activeClips.CalculateEntityCountWithoutFiltering();
            if (_candidates.Capacity < count) _candidates.Capacity = count;
            _candidates.Clear();

            state.Dependency = new GatherOverridesJob
            {
                TrackOverrides = state.GetUnsafeComponentLookup<TrackFallbackOverride>(true),
                Candidates = _candidates.AsParallelWriter()
            }.ScheduleParallel(state.Dependency);

            state.Dependency = new ExtractKeysJob
            {
                Candidates = _candidates,
                Targets = _targets
            }.Schedule(state.Dependency);

            state.Dependency = new LatchFallbackJob
            {
                Targets = _targets.AsDeferredJobArray(),
                Candidates = _candidates,
                Fallbacks = state.GetComponentLookup<FallbackBlend>()
            }.Schedule(_targets, 32, state.Dependency);

            state.Dependency = new RestoreFallbackJob
            {
                Candidates = _candidates
            }.ScheduleParallel(state.Dependency);
        }

        [BurstCompile]
        [WithAll(typeof(ClipActive), typeof(TimelineActive))]
        private partial struct GatherOverridesJob : IJobEntity
        {
            [ReadOnly] public UnsafeComponentLookup<TrackFallbackOverride> TrackOverrides;
            public NativeParallelMultiHashMap<Entity, TrackFallbackOverride>.ParallelWriter Candidates;

            private void Execute(in Clip clip, in TrackBinding binding)
            {
                if (TrackOverrides.TryGetComponent(clip.Track, out var fallbackOverride))
                    Candidates.Add(binding.Value, fallbackOverride);
            }
        }

        [BurstCompile]
        private struct ExtractKeysJob : IJob
        {
            [ReadOnly] public NativeParallelMultiHashMap<Entity, TrackFallbackOverride> Candidates;
            public NativeList<Entity> Targets;

            public void Execute()
            {
                var (keys, length) = Candidates.GetUniqueKeyArray(Allocator.Temp);
                Targets.Clear();
                Targets.AddRange(keys.GetSubArray(0, length));
                keys.Dispose();
            }
        }

        [BurstCompile]
        private struct LatchFallbackJob : IJobParallelForDefer
        {
            [ReadOnly] public NativeArray<Entity> Targets;
            [ReadOnly] public NativeParallelMultiHashMap<Entity, TrackFallbackOverride> Candidates;
            [NativeDisableParallelForRestriction] public ComponentLookup<FallbackBlend> Fallbacks;

            public void Execute(int index)
            {
                var entity = Targets[index];
                if (!Fallbacks.HasComponent(entity)) return;

                var hasBest = false;
                var best = default(TrackFallbackOverride);

                foreach (var candidate in Candidates.GetValuesForKey(entity))
                    if (!hasBest || IsDominant(candidate, best))
                    {
                        best = candidate;
                        hasBest = true;
                    }

                if (!hasBest) return;

                var latched = Fallbacks[entity];

                if (FallbackEquality.Matches(in latched, in best)) return;

                Fallbacks[entity] = new FallbackBlend
                {
                    ClipHash = best.FallbackClipHash,
                    BlendInSpeed = best.BlendInSpeed,
                    BlendOutSpeed = best.BlendOutSpeed,
                    PlaybackMode = best.PlaybackMode,
                    LayerIndex = best.LayerIndex,
                    BlendMode = best.BlendMode,
                    AvatarMaskHash = best.AvatarMaskHash,
                    PositionOffset = best.PositionOffset,
                    RotationOffset = best.RotationOffset,
                    RemoveStartOffset = best.RemoveStartOffset,
                    ApplyFootIK = best.ApplyFootIK
                };
            }

            private static bool IsDominant(in TrackFallbackOverride candidate, in TrackFallbackOverride current)
            {
                if (candidate.LayerIndex != current.LayerIndex)
                    return candidate.LayerIndex > current.LayerIndex;

                return candidate.FallbackClipHash.CompareTo(current.FallbackClipHash) > 0;
            }
        }

        [BurstCompile]
        private partial struct RestoreFallbackJob : IJobEntity
        {
            [ReadOnly] public NativeParallelMultiHashMap<Entity, TrackFallbackOverride> Candidates;

            private void Execute(Entity entity, ref FallbackBlend fallback,
                in DefaultBlendGroupFallback defaultFallback)
            {
                if (Candidates.ContainsKey(entity)) return;

                ref readonly var reset = ref defaultFallback.Value.Value;

                if (FallbackEquality.Matches(in fallback, in reset)) return;

                fallback = reset;
            }
        }

        private static class FallbackEquality
        {
            public static bool Matches(in FallbackBlend f, in TrackFallbackOverride o)
            {
                return f.ClipHash == o.FallbackClipHash
                    && f.BlendInSpeed == o.BlendInSpeed
                    && f.BlendOutSpeed == o.BlendOutSpeed
                    && f.PlaybackMode == o.PlaybackMode
                    && f.LayerIndex == o.LayerIndex
                    && f.BlendMode == o.BlendMode
                    && f.AvatarMaskHash == o.AvatarMaskHash
                    && f.PositionOffset.Equals(o.PositionOffset)
                    && f.RotationOffset.Equals(o.RotationOffset)
                    && f.RemoveStartOffset == o.RemoveStartOffset
                    && f.ApplyFootIK == o.ApplyFootIK;
            }

            public static bool Matches(in FallbackBlend f, in FallbackBlend d)
            {
                return f.ClipHash == d.ClipHash
                    && f.BlendInSpeed == d.BlendInSpeed
                    && f.BlendOutSpeed == d.BlendOutSpeed
                    && f.PlaybackMode == d.PlaybackMode
                    && f.LayerIndex == d.LayerIndex
                    && f.BlendMode == d.BlendMode
                    && f.AvatarMaskHash == d.AvatarMaskHash
                    && f.PositionOffset.Equals(d.PositionOffset)
                    && f.RotationOffset.Equals(d.RotationOffset)
                    && f.RemoveStartOffset == d.RemoveStartOffset
                    && f.ApplyFootIK == d.ApplyFootIK;
            }
        }
    }
}