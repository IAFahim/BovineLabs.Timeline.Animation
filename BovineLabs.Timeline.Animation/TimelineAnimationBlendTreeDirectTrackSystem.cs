using BovineLabs.Core;
using BovineLabs.Core.Extensions;
using BovineLabs.Core.Iterators;
using BovineLabs.Timeline.Data;
using Rukhanka;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Hash128 = Unity.Entities.Hash128;

namespace BovineLabs.Timeline.Animation
{
    [UpdateInGroup(typeof(TimelineComponentAnimationGroup))]
    [UpdateBefore(typeof(TimelineAnimationUnificationSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.LocalSimulation | WorldSystemFilterFlags.ClientSimulation |
                       WorldSystemFilterFlags.ServerSimulation)]
    public partial struct TimelineAnimationBlendTreeDirectTrackSystem : ISystem
    {
        private UnsafeComponentLookup<BlendAnimationTreeDirectTrackData> _trackData;
        private UnsafeBufferLookup<BlendTreeDirectMotionData> _motionBuffer;
        private BufferLookup<BlendGroupEntry> _blendGroup;
        private BufferLookup<BlendTreeDirectPlaybackStateElement> _playbackState;

        private NativeParallelMultiHashMap<Entity, BlendTreeGatherCore.ClipData<NoParam>> _clipDataMap;
        private NativeList<Entity> _targetEntities;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            _trackData = state.GetUnsafeComponentLookup<BlendAnimationTreeDirectTrackData>(true);
            _motionBuffer = state.GetUnsafeBufferLookup<BlendTreeDirectMotionData>(true);
            _blendGroup = state.GetBufferLookup<BlendGroupEntry>();
            _playbackState = state.GetBufferLookup<BlendTreeDirectPlaybackStateElement>();
            _clipDataMap = new NativeParallelMultiHashMap<Entity, BlendTreeGatherCore.ClipData<NoParam>>(64, Allocator.Persistent);
            _targetEntities = new NativeList<Entity>(64, Allocator.Persistent);
            state.RequireForUpdate<BlobDatabaseSingleton>();
            state.RequireForUpdate<BlendTreeDirectClipData>();
        }

        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
            if (_clipDataMap.IsCreated) _clipDataMap.Dispose();
            if (_targetEntities.IsCreated) _targetEntities.Dispose();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var blobDB = SystemAPI.GetSingleton<BlobDatabaseSingleton>();

            _trackData.Update(ref state);
            _motionBuffer.Update(ref state);
            _blendGroup.Update(ref state);
            _playbackState.Update(ref state);

            var clipCount = SystemAPI.QueryBuilder()
                .WithAll<BlendTreeDirectClipData, ClipActive, TrackBinding, Clip, LocalTime>()
                .Build().CalculateEntityCount();
            if (_clipDataMap.Capacity < clipCount)
                _clipDataMap.Capacity = math.max(_clipDataMap.Capacity * 2, clipCount);
            _clipDataMap.Clear();
            _targetEntities.Clear();

            state.Dependency = new GatherClipDataJob
            {
                ClipDataMap = _clipDataMap.AsParallelWriter(),
                ClipLookup = SystemAPI.GetComponentLookup<Clip>(true),
                ClipWeightLookup = SystemAPI.GetComponentLookup<ClipWeight>(true),
                CullLookup = SystemAPI.GetComponentLookup<CullAnimationsTag>(true)
            }.ScheduleParallel(state.Dependency);

            state.Dependency = new ExtractTargetEntitiesJob
            {
                ClipDataMap = _clipDataMap.AsReadOnly(),
                TargetEntities = _targetEntities
            }.Schedule(state.Dependency);

            var isScrubbing = false;
#if UNITY_EDITOR
            isScrubbing = !Application.isPlaying;
#endif

            state.Dependency = new DecomposeAndAppendBlendTreeJob
            {
                TargetEntities = _targetEntities,
                ClipDataMap = _clipDataMap.AsReadOnly(),
                AnimDB = blobDB.animations,
                TrackDataLookup = _trackData,
                MotionBufferLookup = _motionBuffer,
                BlendGroupLookup = _blendGroup,
                PlaybackStateLookup = _playbackState,
                GlobalDeltaTime = SystemAPI.Time.DeltaTime,
                IsScrubbing = isScrubbing,
                Logger = SystemAPI.GetSingleton<BLLogger>()
            }.Schedule(_targetEntities, 64, state.Dependency);
        }

        [BurstCompile]
        [WithAll(typeof(ClipActive))]
        private partial struct GatherClipDataJob : IJobEntity
        {
            public NativeParallelMultiHashMap<Entity, BlendTreeGatherCore.ClipData<NoParam>>.ParallelWriter ClipDataMap;
            [ReadOnly] public ComponentLookup<Clip> ClipLookup;
            [ReadOnly] public ComponentLookup<ClipWeight> ClipWeightLookup;
            [ReadOnly] public ComponentLookup<CullAnimationsTag> CullLookup;

            private void Execute(Entity clipEntity, in BlendTreeDirectClipData clipData,
                in TrackBinding binding, in LocalTime localTime, in TimeTransform timeTransform)
            {
                // Off-screen rig: Rukhanka skips its pose computation, so gathering timeline clips for it is wasted.
                if (CullLookup.HasComponent(binding.Value) && CullLookup.IsComponentEnabled(binding.Value)) return;

                var weight = 1f;
                if (ClipWeightLookup.TryGetComponent(clipEntity, out var cw))
                    weight = cw.Value;

                if (weight <= 0f) return;

                var track = ClipLookup[clipEntity].Track;

                ClipDataMap.Add(binding.Value, new BlendTreeGatherCore.ClipData<NoParam>
                {
                    Track = track,
                    AbsoluteTime = (float)(double)localTime.Value,
                    TimeScale = (float)timeTransform.Scale,
                    Weight = weight,
                    PositionOffset = clipData.PositionOffset,
                    RotationOffset = clipData.RotationOffset,
                    RemoveStartOffset = clipData.RemoveStartOffset,
                    ApplyFootIK = clipData.ApplyFootIK
                });
            }
        }

        [BurstCompile]
        private struct ExtractTargetEntitiesJob : IJob
        {
            [ReadOnly] public NativeParallelMultiHashMap<Entity, BlendTreeGatherCore.ClipData<NoParam>>.ReadOnly ClipDataMap;
            public NativeList<Entity> TargetEntities;

            public void Execute()
            {
                ClipDataMap.GetUniqueKeyArray(TargetEntities);
            }
        }

        [BurstCompile]
        private struct DecomposeAndAppendBlendTreeJob : IJobParallelForDefer
        {
            [ReadOnly] public NativeParallelMultiHashMap<Entity, BlendTreeGatherCore.ClipData<NoParam>>.ReadOnly ClipDataMap;
            [ReadOnly] public NativeList<Entity> TargetEntities;
            [ReadOnly] public NativeHashMap<Hash128, BlobAssetReference<AnimationClipBlob>> AnimDB;
            [ReadOnly] public UnsafeComponentLookup<BlendAnimationTreeDirectTrackData> TrackDataLookup;
            [ReadOnly] public UnsafeBufferLookup<BlendTreeDirectMotionData> MotionBufferLookup;

            [NativeDisableParallelForRestriction] public BufferLookup<BlendGroupEntry> BlendGroupLookup;

            [NativeDisableParallelForRestriction]
            public BufferLookup<BlendTreeDirectPlaybackStateElement> PlaybackStateLookup;

            public float GlobalDeltaTime;
            public bool IsScrubbing;

            public BLLogger Logger;

            public void Execute(int index)
            {
                BlendTreeGatherCore.Process<NoParam, NoParamBlendOps, BlendTreeDirectPlaybackStateElement,
                    PlaybackStateAccessDirect, BlendTreeDirectSolver>(
                    index, TargetEntities, ClipDataMap, BlendGroupLookup, PlaybackStateLookup, AnimDB, GlobalDeltaTime,
                    IsScrubbing, Logger, default, default,
                    new BlendTreeDirectSolver { MotionBufferLookup = MotionBufferLookup, TrackDataLookup = TrackDataLookup });
            }
        }

        // Direct blend trees have no blend parameter; the weights live on the track's motion buffer.
        private struct NoParam
        {
        }

        private struct NoParamBlendOps : BlendTreeGatherCore.IBlendParamOps<NoParam>
        {
            public NoParam MulAdd(NoParam accumulated, NoParam raw, float weight) => default;

            public NoParam Div(NoParam accumulated, float weight) => default;
        }

        private struct PlaybackStateAccessDirect : BlendTreeGatherCore.IPlaybackStateAccess<BlendTreeDirectPlaybackStateElement>
        {
            public Entity GetTrack(in BlendTreeDirectPlaybackStateElement element) => element.Track;

            public bool GetInitialized(in BlendTreeDirectPlaybackStateElement element) => element.IsInitialized;

            public float GetAccumulated(in BlendTreeDirectPlaybackStateElement element) => element.AccumulatedTime;

            public float GetPreviousAbsoluteTime(in BlendTreeDirectPlaybackStateElement element) => element.PreviousAbsoluteTime;

            public BlendTreeDirectPlaybackStateElement Create(Entity track, bool initialized, float accumulatedTime,
                float previousAbsoluteTime)
            {
                return new BlendTreeDirectPlaybackStateElement
                {
                    Track = track,
                    IsInitialized = initialized,
                    AccumulatedTime = accumulatedTime,
                    PreviousAbsoluteTime = previousAbsoluteTime
                };
            }
        }

        private struct BlendTreeDirectSolver : BlendTreeGatherCore.IBlendTreeSolver<NoParam>
        {
            [ReadOnly] public UnsafeBufferLookup<BlendTreeDirectMotionData> MotionBufferLookup;
            [ReadOnly] public UnsafeComponentLookup<BlendAnimationTreeDirectTrackData> TrackDataLookup;

            public bool TryPrepare(Entity trackEntity, NoParam blendedParameter,
                NativeHashMap<Hash128, BlobAssetReference<AnimationClipBlob>> animDB, BLLogger logger,
                out BlendTreeGatherCore.BlendTreeTrackConfig config,
                out NativeArray<BlobAssetReference<AnimationClipBlob>> clips,
                out NativeList<ScriptedAnimator.MotionIndexAndWeight> weights)
            {
                config = default;
                clips = default;
                weights = default;

                if (!MotionBufferLookup.TryGetBuffer(trackEntity, out var motions) ||
                    !TrackDataLookup.TryGetComponent(trackEntity, out var trackData))
                    return false;

                var motionCount = motions.Length;
                if (motionCount <= 0) return false;

                clips = new NativeArray<BlobAssetReference<AnimationClipBlob>>(motionCount, Allocator.Temp);
                var blendWeights = new NativeArray<float>(motionCount, Allocator.Temp);
                for (var i = 0; i < motionCount; i++)
                {
                    var motionData = motions[i];
                    var found = animDB.TryGetValue(motionData.AnimationHash, out var cb);
                    if (!found)
                        logger.LogWarning512(
                            "[BlendTreeDirect] Animation hash not found in BlobDatabaseSingleton. Motion entry will be skipped.");
                    clips[i] = found ? cb : BlobAssetReference<AnimationClipBlob>.Null;
                    blendWeights[i] = motionData.Weight;
                }

                weights = ScriptedAnimator.ComputeBlendTreeDirect(blendWeights, trackData.NormalizeBlendValues);
                blendWeights.Dispose();

                if (!weights.IsCreated)
                {
                    logger.LogWarning512("[BlendTreeDirect] ComputeBlendTreeDirect returned no weights; track skipped.");
                    clips.Dispose();
                    clips = default;
                    return false;
                }

                config = new BlendTreeGatherCore.BlendTreeTrackConfig
                {
                    LayerIndex = trackData.LayerIndex,
                    TrackPositionOffset = trackData.TrackPositionOffset,
                    TrackRotationOffset = trackData.TrackRotationOffset,
                    ApplyAvatarMask = trackData.ApplyAvatarMask,
                    AvatarMaskHash = trackData.AvatarMaskHash
                };
                return true;
            }
        }
    }
}
