using BovineLabs.Core.Extensions;
using BovineLabs.Core.Iterators;
using BovineLabs.Timeline.Data;
using Rukhanka;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Hash128 = Unity.Entities.Hash128;

namespace BovineLabs.Timeline.Animation
{
    [UpdateInGroup(typeof(TimelineComponentAnimationGroup))]
    [UpdateBefore(typeof(TimelineAnimationUnificationSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.LocalSimulation | WorldSystemFilterFlags.ClientSimulation |
                       WorldSystemFilterFlags.ServerSimulation)]
    public partial struct TimelineSingleAnimationTrackSystem : ISystem
    {
        private const float MinDuration = 0.001f;

        private NativeParallelMultiHashMap<Entity, BlendGroupEntry> _activeAnimationsMap;
        private NativeList<Entity> _uniqueKeys;
        private EntityQuery _query;

        private UnsafeComponentLookup<ClipWeight> _clipWeights;
        private UnsafeComponentLookup<RukhankaSingleTrackData> _trackData;
        private BufferLookup<BlendGroupEntry> _animationBuffers;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            _activeAnimationsMap = new NativeParallelMultiHashMap<Entity, BlendGroupEntry>(64, Allocator.Persistent);
            _uniqueKeys = new NativeList<Entity>(64, Allocator.Persistent);
            _query = SystemAPI.QueryBuilder().WithAll<ClipActive, TimelineActive, RukhankaSingleClipData>().Build();
            _clipWeights = state.GetUnsafeComponentLookup<ClipWeight>(true);
            _trackData = state.GetUnsafeComponentLookup<RukhankaSingleTrackData>(true);
            _animationBuffers = state.GetBufferLookup<BlendGroupEntry>();
            state.RequireForUpdate<BlobDatabaseSingleton>();
        }

        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
            if (_activeAnimationsMap.IsCreated) _activeAnimationsMap.Dispose();
            if (_uniqueKeys.IsCreated) _uniqueKeys.Dispose();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var count = _query.CalculateEntityCountWithoutFiltering();
            if (_activeAnimationsMap.Capacity < count)
                _activeAnimationsMap.Capacity = math.max(_activeAnimationsMap.Capacity * 2, count);
            _activeAnimationsMap.Clear();
            var blobDB = SystemAPI.GetSingleton<BlobDatabaseSingleton>();

            _clipWeights.Update(ref state);
            _trackData.Update(ref state);
            _animationBuffers.Update(ref state);

            var gatherJob = new GatherActiveClipsJob
            {
                AnimDB = blobDB.animations,
                ClipWeights = _clipWeights,
                TrackDataLookup = _trackData,
                ActiveAnimations = _activeAnimationsMap.AsParallelWriter()
            };

            state.Dependency = gatherJob.ScheduleParallel(state.Dependency);

            state.Dependency = new ExtractKeysJob
            {
                ActiveAnimations = _activeAnimationsMap,
                UniqueKeys = _uniqueKeys
            }.Schedule(state.Dependency);

            state.Dependency = new ApplyAnimationsJob
            {
                UniqueKeys = _uniqueKeys.AsDeferredJobArray(),
                ActiveAnimations = _activeAnimationsMap,
                AnimationBuffers = _animationBuffers
            }.Schedule(_uniqueKeys, 64, state.Dependency);
        }

        [BurstCompile]
        [WithAll(typeof(ClipActive), typeof(TimelineActive))]
        public partial struct GatherActiveClipsJob : IJobEntity
        {
            [ReadOnly] public NativeHashMap<Hash128, BlobAssetReference<AnimationClipBlob>> AnimDB;
            [ReadOnly] public UnsafeComponentLookup<ClipWeight> ClipWeights;
            [ReadOnly] public UnsafeComponentLookup<RukhankaSingleTrackData> TrackDataLookup;

            public NativeParallelMultiHashMap<Entity, BlendGroupEntry>.ParallelWriter ActiveAnimations;

            private void Execute(Entity clipEntity, in RukhankaSingleClipData clipData, in TrackBinding binding,
                in Clip clip, in LocalTime localTime, in TimeTransform timeTransform)
            {
                if (!TrackDataLookup.TryGetComponent(clip.Track, out var trackData)) return;

                var weight = 1f;
                if (ClipWeights.TryGetComponent(clipEntity, out var clipWeight))
                    weight = clipWeight.Value;

                if (weight <= 0f) return;
                if (!AnimDB.TryGetValue(clipData.ClipHash, out var clipBlob) || !clipBlob.IsCreated) return;

                var timeInSeconds = (float)(double)localTime.Value;
                var duration = math.max(MinDuration, clipBlob.Value.length);

                var extrapolation = timeInSeconds < 0f ? clipData.PreExtrapolation : clipData.PostExtrapolation;

                var normalizedTime = ClipSampling.NormalizedClipTime(timeInSeconds, duration, extrapolation, clipBlob.Value.looped);

                ClipSampling.ComposeTrackClipOffset(trackData.TrackPositionOffset, trackData.TrackRotationOffset,
                    clipData.PositionOffset, clipData.RotationOffset, out var finalPosOffset, out var finalRotOffset);

                ActiveAnimations.Add(binding.Value, new BlendGroupEntry
                {
                    LayerIndex = trackData.LayerIndex,
                    ClipHash = clipData.ClipHash,
                    NormalizedTime = normalizedTime,
                    Weight = weight,
                    AvatarMaskHash = trackData.ApplyAvatarMask ? trackData.AvatarMaskHash : default,
                    BlendMode = trackData.BlendMode,
                    MotionId = MotionId.Compute(clip.Track, trackData.LayerIndex, clipData.ClipHash, clipEntity),

                    PositionOffset = finalPosOffset,
                    RotationOffset = finalRotOffset,
                    RemoveStartOffset = clipData.RemoveStartOffset,
                    ApplyFootIK = clipData.ApplyFootIK,

                    // Continuous-phase loop mode. PhaseVelocity is cycles/sec = the clip's timeline speed
                    // multiplier (TimeTransform.Scale) divided by the clip length in seconds (duration). The
                    // unification system free-runs NormalizedTime by this each frame instead of reading the
                    // wrapping localTime, so the loop seam is invisible regardless of timeline duration.
                    ContinuousLoop = clipData.ContinuousLoop,
                    PhaseVelocity = (float)(timeTransform.Scale / duration)
                });
            }
        }

        [BurstCompile]
        private struct ExtractKeysJob : IJob
        {
            [ReadOnly] public NativeParallelMultiHashMap<Entity, BlendGroupEntry> ActiveAnimations;
            public NativeList<Entity> UniqueKeys;

            public void Execute()
            {
                var (keys, count) = ActiveAnimations.GetUniqueKeyArray(Allocator.Temp);
                UniqueKeys.Clear();
                UniqueKeys.AddRange(keys.GetSubArray(0, count));
                keys.Dispose();
            }
        }

        [BurstCompile]
        private struct ApplyAnimationsJob : IJobParallelForDefer
        {
            [ReadOnly] public NativeArray<Entity> UniqueKeys;
            [ReadOnly] public NativeParallelMultiHashMap<Entity, BlendGroupEntry> ActiveAnimations;
            [NativeDisableParallelForRestriction] public BufferLookup<BlendGroupEntry> AnimationBuffers;

            public void Execute(int index)
            {
                var entity = UniqueKeys[index];
                if (!AnimationBuffers.TryGetBuffer(entity, out var buffer)) return;

                foreach (var entry in ActiveAnimations.GetValuesForKey(entity))
                    buffer.Add(entry);
            }
        }
    }
}