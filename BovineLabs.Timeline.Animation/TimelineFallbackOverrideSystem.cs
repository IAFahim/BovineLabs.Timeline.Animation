using BovineLabs.Core.Extensions;
using BovineLabs.Core.Iterators;
using BovineLabs.Timeline.Data;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
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
        private NativeParallelMultiHashMap<Entity, TrackFallbackOverride> candidates;
        private NativeList<Entity> targets;
        private EntityQuery activeClips;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            candidates = new NativeParallelMultiHashMap<Entity, TrackFallbackOverride>(64, Allocator.Persistent);
            targets = new NativeList<Entity>(64, Allocator.Persistent);
            activeClips = SystemAPI.QueryBuilder()
                .WithAll<ClipActive, TimelineActive, Clip, TrackBinding>().Build();
        }

        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
            if (candidates.IsCreated) candidates.Dispose();
            if (targets.IsCreated) targets.Dispose();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var count = activeClips.CalculateEntityCountWithoutFiltering();
            if (candidates.Capacity < count) candidates.Capacity = count;
            candidates.Clear();

            state.Dependency = new GatherOverridesJob
            {
                TrackOverrides = state.GetUnsafeComponentLookup<TrackFallbackOverride>(true),
                Candidates = candidates.AsParallelWriter()
            }.ScheduleParallel(state.Dependency);

            state.Dependency = new ExtractKeysJob
            {
                Candidates = candidates,
                Targets = targets
            }.Schedule(state.Dependency);

            state.Dependency = new LatchFallbackJob
            {
                Targets = targets.AsDeferredJobArray(),
                Candidates = candidates,
                Fallbacks = state.GetComponentLookup<FallbackBlend>()
            }.Schedule(targets, 32, state.Dependency);
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
                if (latched.ClipHash == best.FallbackClipHash) return;

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
    }
}
