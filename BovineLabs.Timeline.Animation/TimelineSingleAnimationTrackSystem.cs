using BovineLabs.Core.Extensions;
using BovineLabs.Core.Iterators;
using BovineLabs.Timeline.Data;
using Rukhanka;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine.Timeline;
using Hash128 = Unity.Entities.Hash128;

namespace BovineLabs.Timeline.Animation
{
    [UpdateInGroup(typeof(TimelineComponentAnimationGroup))]
    [UpdateBefore(typeof(TimelineAnimationUnificationSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.LocalSimulation | WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    public partial struct TimelineSingleAnimationTrackSystem : ISystem
    {
        private const float MinDuration = 0.001f;

        private NativeParallelMultiHashMap<Entity, BlendGroupEntry> activeAnimationsMap;
        private NativeList<Entity> uniqueKeys;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            activeAnimationsMap = new NativeParallelMultiHashMap<Entity, BlendGroupEntry>(64, Allocator.Persistent);
            uniqueKeys = new NativeList<Entity>(64, Allocator.Persistent);
            state.RequireForUpdate<BlobDatabaseSingleton>();
        }

        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
            if (activeAnimationsMap.IsCreated) activeAnimationsMap.Dispose();
            if (uniqueKeys.IsCreated) uniqueKeys.Dispose();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            activeAnimationsMap.Clear();
            var blobDB = SystemAPI.GetSingleton<BlobDatabaseSingleton>();

            var gatherJob = new GatherActiveClipsJob
            {
                AnimDB = blobDB.animations,
                ClipWeights = state.GetUnsafeComponentLookup<ClipWeight>(true),
                TrackDataLookup = state.GetUnsafeComponentLookup<RukhankaSingleTrackData>(true),
                ActiveAnimations = activeAnimationsMap.AsParallelWriter()
            };

            state.Dependency = gatherJob.ScheduleParallel(state.Dependency);

            state.Dependency = new ExtractKeysJob
            {
                ActiveAnimations = activeAnimationsMap,
                UniqueKeys = uniqueKeys
            }.Schedule(state.Dependency);

            state.Dependency = new ApplyAnimationsJob
            {
                UniqueKeys = uniqueKeys.AsDeferredJobArray(),
                ActiveAnimations = activeAnimationsMap,
                AnimationBuffers = state.GetBufferLookup<BlendGroupEntry>(false)
            }.Schedule(uniqueKeys, 64, state.Dependency);
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
                in Clip clip, in LocalTime localTime)
            {
                if (!TrackDataLookup.TryGetComponent(clip.Track, out var trackData)) return;

                var weight = 1f;
                if (ClipWeights.TryGetComponent(clipEntity, out var clipWeight))
                    weight = clipWeight.Value;

                if (weight <= 0f) return;
                if (!AnimDB.TryGetValue(clipData.ClipHash, out var clipBlob) || !clipBlob.IsCreated) return;

                var timeInSeconds = (float)((double)localTime.Value * clipData.TimeScale + clipData.ClipIn);
                var duration = math.max(MinDuration, clipBlob.Value.length);

                float normalizedTime;
                if (clipData.PostExtrapolation == TimelineClip.ClipExtrapolation.PingPong)
                {
                    var t = math.fmod(timeInSeconds, duration * 2f);
                    normalizedTime = (duration - math.abs(t - duration)) / duration;
                }
                else if (clipData.PostExtrapolation == TimelineClip.ClipExtrapolation.Loop || clipBlob.Value.looped)
                {
                    normalizedTime = math.frac(timeInSeconds / duration);
                }
                else
                {
                    normalizedTime = math.saturate(timeInSeconds / duration);
                }

                var finalPosOffset = trackData.TrackPositionOffset +
                                     math.rotate(trackData.TrackRotationOffset, clipData.PositionOffset);
                var finalRotOffset = math.mul(trackData.TrackRotationOffset, clipData.RotationOffset);

                ActiveAnimations.Add(binding.Value, new BlendGroupEntry
                {
                    LayerIndex = trackData.LayerIndex,
                    ClipHash = clipData.ClipHash,
                    NormalizedTime = normalizedTime,
                    Weight = weight,
                    AvatarMaskHash = trackData.ApplyAvatarMask ? trackData.AvatarMaskHash : default,
                    BlendMode = AnimationBlendingMode.Override,
                    MotionId = MotionId.Compute(clip.Track, trackData.LayerIndex, clipData.ClipHash),

                    PositionOffset = finalPosOffset,
                    RotationOffset = finalRotOffset,
                    RemoveStartOffset = clipData.RemoveStartOffset,
                    ApplyFootIK = clipData.ApplyFootIK
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