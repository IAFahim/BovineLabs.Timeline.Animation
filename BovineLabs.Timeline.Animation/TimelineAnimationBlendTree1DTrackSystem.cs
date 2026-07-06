using BovineLabs.Core;
using BovineLabs.Core.Extensions;
using BovineLabs.Core.Iterators;
using BovineLabs.Timeline.Data;
using BovineLabs.Timeline.EntityLinks;
using BovineLabs.Timeline.EntityLinks.Data;
using BovineLabs.Timeline.PlayerInputs.Data;
using Rukhanka;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Physics;
using UnityEngine;
using Hash128 = Unity.Entities.Hash128;

namespace BovineLabs.Timeline.Animation
{
    [UpdateInGroup(typeof(TimelineComponentAnimationGroup))]
    [UpdateBefore(typeof(TimelineAnimationUnificationSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.LocalSimulation | WorldSystemFilterFlags.ClientSimulation |
                       WorldSystemFilterFlags.ServerSimulation)]
    public partial struct TimelineAnimationBlendTree1DTrackSystem : ISystem
    {
        private const float DirectionEpsilon = 0.0001f;

        private UnsafeComponentLookup<BlendAnimationTree1DTrackData> _trackData;
        private UnsafeBufferLookup<BlendTree1DMotionData> _motionBuffer;
        private BufferLookup<BlendGroupEntry> _blendGroup;
        private BufferLookup<BlendTree1DPlaybackStateElement> _playbackState;

        private NativeParallelMultiHashMap<Entity, BlendTreeGatherCore.ClipData<float>> _clipDataMap;
        private NativeList<Entity> _targetEntities;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            _trackData = state.GetUnsafeComponentLookup<BlendAnimationTree1DTrackData>(true);
            _motionBuffer = state.GetUnsafeBufferLookup<BlendTree1DMotionData>(true);
            _blendGroup = state.GetBufferLookup<BlendGroupEntry>();
            _playbackState = state.GetBufferLookup<BlendTree1DPlaybackStateElement>();
            _clipDataMap = new NativeParallelMultiHashMap<Entity, BlendTreeGatherCore.ClipData<float>>(64, Allocator.Persistent);
            _targetEntities = new NativeList<Entity>(64, Allocator.Persistent);
            state.RequireForUpdate<BlobDatabaseSingleton>();
            state.RequireForUpdate<BlendTree1DParameterClipData>();
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

            state.Dependency = new UpdateDynamicBlendParametersJob
            {
                PhysicsVelocityLookup = SystemAPI.GetComponentLookup<PhysicsVelocity>(true),
                PlayerMoveInputLookup = SystemAPI.GetComponentLookup<PlayerMoveInput>(true),
                EntityLinkSourceLookup = state.GetUnsafeComponentLookup<EntityLinkSource>(true),
                EntityLinkEntryLookup = state.GetUnsafeBufferLookup<EntityLinkEntry>(true),
                TrackBindingLookup = SystemAPI.GetComponentLookup<TrackBinding>(true)
            }.ScheduleParallel(state.Dependency);

            var clipCount = SystemAPI.QueryBuilder()
                .WithAll<BlendTree1DParameterClipData, ClipActive, TrackBinding, Clip, LocalTime>()
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
        private partial struct UpdateDynamicBlendParametersJob : IJobEntity
        {
            [ReadOnly] public ComponentLookup<PhysicsVelocity> PhysicsVelocityLookup;
            [ReadOnly] public ComponentLookup<PlayerMoveInput> PlayerMoveInputLookup;
            [ReadOnly] public UnsafeComponentLookup<EntityLinkSource> EntityLinkSourceLookup;
            [ReadOnly] public UnsafeBufferLookup<EntityLinkEntry> EntityLinkEntryLookup;
            [ReadOnly] public ComponentLookup<TrackBinding> TrackBindingLookup;

            private void Execute(Entity clipEntity, ref BlendTree1DParameterClipData clipData)
            {
                if (clipData.ReadKind == BlendDirectionReadKind.ClipValue)
                    return;

                if (clipData.ReadLinkKey == 0)
                {
                    clipData.Value = 0f;
                    return;
                }

                if (!TrackBindingLookup.TryGetComponent(clipEntity, out var binding) ||
                    !EntityLinkResolver.TryResolve(binding.Value, clipData.ReadLinkKey,
                        EntityLinkSourceLookup, EntityLinkEntryLookup, out var resolvedEntity))
                {
                    clipData.Value = 0f;
                    return;
                }

                if (clipData.ReadKind == BlendDirectionReadKind.PhysicsLinearVelocityNormalized)
                {
                    if (PhysicsVelocityLookup.TryGetComponent(resolvedEntity, out var pv))
                    {
                        var horizontalSpeed = math.length(new float3(pv.Linear.x, 0f, pv.Linear.z));
                        clipData.Value = math.saturate(horizontalSpeed / math.max(DirectionEpsilon, clipData.MaxSpeed));
                    }
                    else
                    {
                        clipData.Value = 0f;
                    }
                }
                else if (clipData.ReadKind == BlendDirectionReadKind.PlayerMoveInput)
                {
                    if (PlayerMoveInputLookup.TryGetComponent(resolvedEntity, out var moveInput))
                    {
                        clipData.Value = math.saturate(math.length(moveInput.Value));
                    }
                    else
                    {
                        clipData.Value = 0f;
                    }
                }

                if (math.isnan(clipData.Value) || math.isinf(clipData.Value))
                    clipData.Value = 0f;
            }
        }

        [BurstCompile]
        [WithAll(typeof(ClipActive))]
        private partial struct GatherClipDataJob : IJobEntity
        {
            public NativeParallelMultiHashMap<Entity, BlendTreeGatherCore.ClipData<float>>.ParallelWriter ClipDataMap;
            [ReadOnly] public ComponentLookup<Clip> ClipLookup;
            [ReadOnly] public ComponentLookup<ClipWeight> ClipWeightLookup;
            [ReadOnly] public ComponentLookup<CullAnimationsTag> CullLookup;

            private void Execute(Entity clipEntity, in BlendTree1DParameterClipData parameterData,
                in TrackBinding binding, in LocalTime localTime, in TimeTransform timeTransform)
            {
                // Off-screen rig: Rukhanka skips its pose computation, so gathering timeline clips for it is wasted.
                if (CullLookup.HasComponent(binding.Value) && CullLookup.IsComponentEnabled(binding.Value)) return;

                var weight = 1f;
                if (ClipWeightLookup.TryGetComponent(clipEntity, out var cw))
                    weight = cw.Value;

                if (weight <= 0f) return;

                var track = ClipLookup[clipEntity].Track;

                ClipDataMap.Add(binding.Value, new BlendTreeGatherCore.ClipData<float>
                {
                    Track = track,
                    AbsoluteTime = (float)(double)localTime.Value,
                    TimeScale = (float)timeTransform.Scale,
                    Parameter = parameterData.Value,
                    Weight = weight,
                    PositionOffset = parameterData.PositionOffset,
                    RotationOffset = parameterData.RotationOffset,
                    RemoveStartOffset = parameterData.RemoveStartOffset,
                    ApplyFootIK = parameterData.ApplyFootIK
                });
            }
        }

        [BurstCompile]
        private struct ExtractTargetEntitiesJob : IJob
        {
            [ReadOnly] public NativeParallelMultiHashMap<Entity, BlendTreeGatherCore.ClipData<float>>.ReadOnly ClipDataMap;
            public NativeList<Entity> TargetEntities;

            public void Execute()
            {
                ClipDataMap.GetUniqueKeyArray(TargetEntities);
            }
        }

        [BurstCompile]
        private struct DecomposeAndAppendBlendTreeJob : IJobParallelForDefer
        {
            [ReadOnly] public NativeParallelMultiHashMap<Entity, BlendTreeGatherCore.ClipData<float>>.ReadOnly ClipDataMap;
            [ReadOnly] public NativeList<Entity> TargetEntities;
            [ReadOnly] public NativeHashMap<Hash128, BlobAssetReference<AnimationClipBlob>> AnimDB;
            [ReadOnly] public UnsafeComponentLookup<BlendAnimationTree1DTrackData> TrackDataLookup;
            [ReadOnly] public UnsafeBufferLookup<BlendTree1DMotionData> MotionBufferLookup;

            [NativeDisableParallelForRestriction] public BufferLookup<BlendGroupEntry> BlendGroupLookup;

            [NativeDisableParallelForRestriction]
            public BufferLookup<BlendTree1DPlaybackStateElement> PlaybackStateLookup;

            public float GlobalDeltaTime;
            public bool IsScrubbing;

            public BLLogger Logger;

            public void Execute(int index)
            {
                BlendTreeGatherCore.Process<float, FloatBlendOps, BlendTree1DPlaybackStateElement, PlaybackStateAccess1D,
                    BlendTree1DSolver>(
                    index, TargetEntities, ClipDataMap, BlendGroupLookup, PlaybackStateLookup, AnimDB, GlobalDeltaTime,
                    IsScrubbing, Logger, default, default,
                    new BlendTree1DSolver { MotionBufferLookup = MotionBufferLookup, TrackDataLookup = TrackDataLookup });
            }
        }

        private struct FloatBlendOps : BlendTreeGatherCore.IBlendParamOps<float>
        {
            public float MulAdd(float accumulated, float raw, float weight)
            {
                return accumulated + (raw * weight);
            }

            public float Div(float accumulated, float weight)
            {
                return accumulated / weight;
            }
        }

        private struct PlaybackStateAccess1D : BlendTreeGatherCore.IPlaybackStateAccess<BlendTree1DPlaybackStateElement>
        {
            public Entity GetTrack(in BlendTree1DPlaybackStateElement element) => element.Track;

            public bool GetInitialized(in BlendTree1DPlaybackStateElement element) => element.IsInitialized;

            public float GetAccumulated(in BlendTree1DPlaybackStateElement element) => element.AccumulatedTime;

            public float GetPreviousAbsoluteTime(in BlendTree1DPlaybackStateElement element) => element.PreviousAbsoluteTime;

            public BlendTree1DPlaybackStateElement Create(Entity track, bool initialized, float accumulatedTime,
                float previousAbsoluteTime)
            {
                return new BlendTree1DPlaybackStateElement
                {
                    Track = track,
                    IsInitialized = initialized,
                    AccumulatedTime = accumulatedTime,
                    PreviousAbsoluteTime = previousAbsoluteTime
                };
            }
        }

        private struct BlendTree1DSolver : BlendTreeGatherCore.IBlendTreeSolver<float>
        {
            [ReadOnly] public UnsafeBufferLookup<BlendTree1DMotionData> MotionBufferLookup;
            [ReadOnly] public UnsafeComponentLookup<BlendAnimationTree1DTrackData> TrackDataLookup;

            public bool TryPrepare(Entity trackEntity, float blendedParameter,
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
                var thresholds = new NativeArray<float>(motionCount, Allocator.Temp);
                for (var i = 0; i < motionCount; i++)
                {
                    var motionData = motions[i];
                    var found = animDB.TryGetValue(motionData.AnimationHash, out var cb);
                    if (!found)
                        logger.LogWarning512(
                            "[BlendTree1D] Animation hash not found in BlobDatabaseSingleton. Motion entry will be skipped.");
                    clips[i] = found ? cb : BlobAssetReference<AnimationClipBlob>.Null;
                    thresholds[i] = motionData.Threshold;
                }

                if (motionCount < 2)
                {
                    // ComputeBlendTree1D needs at least 2 thresholds; a lone motion plays at full weight.
                    weights = new NativeList<ScriptedAnimator.MotionIndexAndWeight>(1, Allocator.Temp);
                    weights.Add(new ScriptedAnimator.MotionIndexAndWeight { motionIndex = 0, weight = 1f });
                }
                else
                {
                    weights = ScriptedAnimator.ComputeBlendTree1D(thresholds, blendedParameter);
                    if (!weights.IsCreated)
                    {
                        logger.LogWarning512("[BlendTree1D] ComputeBlendTree1D returned no weights; track skipped.");
                        thresholds.Dispose();
                        clips.Dispose();
                        clips = default;
                        return false;
                    }
                }

                thresholds.Dispose();
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
