using BovineLabs.Core.Extensions;
using BovineLabs.Core.Iterators;
using BovineLabs.Timeline.Data;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace BovineLabs.Timeline.Animation
{
    /// <summary>
    /// Reads active LayerWeight clips and writes a per-actor, per-layer weight multiplier into the actor's
    /// <see cref="LayerWeightOverride"/> buffer each frame. The multiplier is the clip's timeline ease
    /// (ClipWeight) times the clip's MaxMultiplier, so a designer animates a whole layer's overall weight with
    /// the clip's own blend in/out handles. Overlapping clips on the same layer take the MAX multiplier so a
    /// crossfade between two LayerWeight clips holds the layer up instead of dipping. Every buffer is cleared
    /// first, so a layer with no active clip this frame reverts to no-override (the unification pass reads
    /// multiplier 1). Consumed by <see cref="TimelineAnimationUnificationSystem"/>.
    /// </summary>
    [UpdateInGroup(typeof(TimelineComponentAnimationGroup))]
    [UpdateBefore(typeof(TimelineAnimationUnificationSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.LocalSimulation | WorldSystemFilterFlags.ClientSimulation |
                       WorldSystemFilterFlags.ServerSimulation)]
    public partial struct TimelineLayerWeightTrackSystem : ISystem
    {
        private NativeParallelMultiHashMap<Entity, LayerWeightOverride> _overridesMap;
        private NativeList<Entity> _uniqueKeys;
        private EntityQuery _query;

        private UnsafeComponentLookup<ClipWeight> _clipWeights;
        private UnsafeComponentLookup<LayerWeightTrackData> _trackData;
        private BufferLookup<LayerWeightOverride> _overrideBuffers;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            _overridesMap = new NativeParallelMultiHashMap<Entity, LayerWeightOverride>(64, Allocator.Persistent);
            _uniqueKeys = new NativeList<Entity>(64, Allocator.Persistent);
            _query = SystemAPI.QueryBuilder().WithAll<ClipActive, TimelineActive, LayerWeightClipData>().Build();
            _clipWeights = state.GetUnsafeComponentLookup<ClipWeight>(true);
            _trackData = state.GetUnsafeComponentLookup<LayerWeightTrackData>(true);
            _overrideBuffers = state.GetBufferLookup<LayerWeightOverride>();
            state.RequireForUpdate<LayerWeightOverride>();
        }

        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
            if (_overridesMap.IsCreated) _overridesMap.Dispose();
            if (_uniqueKeys.IsCreated) _uniqueKeys.Dispose();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var count = _query.CalculateEntityCountWithoutFiltering();
            if (_overridesMap.Capacity < count)
                _overridesMap.Capacity = math.max(_overridesMap.Capacity * 2, count);
            _overridesMap.Clear();

            _clipWeights.Update(ref state);
            _trackData.Update(ref state);
            _overrideBuffers.Update(ref state);

            // Clear every override buffer first so a layer with no active clip this frame reverts to multiplier 1.
            state.Dependency = new ClearOverridesJob().ScheduleParallel(state.Dependency);

            state.Dependency = new GatherActiveClipsJob
            {
                ClipWeights = _clipWeights,
                TrackDataLookup = _trackData,
                Overrides = _overridesMap.AsParallelWriter(),
            }.ScheduleParallel(state.Dependency);

            state.Dependency = new ExtractKeysJob
            {
                Overrides = _overridesMap,
                UniqueKeys = _uniqueKeys,
            }.Schedule(state.Dependency);

            state.Dependency = new ApplyOverridesJob
            {
                UniqueKeys = _uniqueKeys.AsDeferredJobArray(),
                Overrides = _overridesMap,
                OverrideBuffers = _overrideBuffers,
            }.Schedule(_uniqueKeys, 64, state.Dependency);
        }

        [BurstCompile]
        private partial struct ClearOverridesJob : IJobEntity
        {
            private void Execute(ref DynamicBuffer<LayerWeightOverride> overrides)
            {
                overrides.Clear();
            }
        }

        [BurstCompile]
        [WithAll(typeof(ClipActive), typeof(TimelineActive))]
        private partial struct GatherActiveClipsJob : IJobEntity
        {
            [ReadOnly] public UnsafeComponentLookup<ClipWeight> ClipWeights;
            [ReadOnly] public UnsafeComponentLookup<LayerWeightTrackData> TrackDataLookup;
            public NativeParallelMultiHashMap<Entity, LayerWeightOverride>.ParallelWriter Overrides;

            private void Execute(Entity clipEntity, in LayerWeightClipData clipData, in TrackBinding binding,
                in Clip clip)
            {
                if (!TrackDataLookup.TryGetComponent(clip.Track, out var trackData)) return;

                var ease = 1f;
                if (ClipWeights.TryGetComponent(clipEntity, out var clipWeight))
                    ease = clipWeight.Value;

                // The clip's timeline ease IS the layer-weight multiplier, capped by the clip's MaxMultiplier.
                var multiplier = math.saturate(ease * clipData.MaxMultiplier);

                Overrides.Add(binding.Value, new LayerWeightOverride
                {
                    LayerIndex = trackData.LayerIndex,
                    Multiplier = multiplier,
                });
            }
        }

        [BurstCompile]
        private struct ExtractKeysJob : IJob
        {
            [ReadOnly] public NativeParallelMultiHashMap<Entity, LayerWeightOverride> Overrides;
            public NativeList<Entity> UniqueKeys;

            public void Execute()
            {
                var (keys, count) = Overrides.GetUniqueKeyArray(Allocator.Temp);
                UniqueKeys.Clear();
                UniqueKeys.AddRange(keys.GetSubArray(0, count));
                keys.Dispose();
            }
        }

        [BurstCompile]
        private struct ApplyOverridesJob : IJobParallelForDefer
        {
            [ReadOnly] public NativeArray<Entity> UniqueKeys;
            [ReadOnly] public NativeParallelMultiHashMap<Entity, LayerWeightOverride> Overrides;
            [NativeDisableParallelForRestriction] public BufferLookup<LayerWeightOverride> OverrideBuffers;

            public void Execute(int index)
            {
                var entity = UniqueKeys[index];
                if (!OverrideBuffers.TryGetBuffer(entity, out var buffer)) return;

                // The buffer was cleared this frame; fold overlapping clips on the same layer into one entry,
                // taking the MAX multiplier so a crossfade between two LayerWeight clips does not dip mid-blend.
                foreach (var entry in Overrides.GetValuesForKey(entity))
                {
                    var found = false;
                    for (var i = 0; i < buffer.Length; i++)
                    {
                        if (buffer[i].LayerIndex == entry.LayerIndex)
                        {
                            if (entry.Multiplier > buffer[i].Multiplier)
                                buffer[i] = entry;
                            found = true;
                            break;
                        }
                    }

                    if (!found)
                        buffer.Add(entry);
                }
            }
        }
    }
}
