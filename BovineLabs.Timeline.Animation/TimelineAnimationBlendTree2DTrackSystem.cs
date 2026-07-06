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
using Unity.Transforms;
using UnityEngine;
using Hash128 = Unity.Entities.Hash128;

namespace BovineLabs.Timeline.Animation
{
    [UpdateInGroup(typeof(TimelineComponentAnimationGroup))]
    [UpdateBefore(typeof(TimelineAnimationUnificationSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.LocalSimulation | WorldSystemFilterFlags.ClientSimulation |
                       WorldSystemFilterFlags.ServerSimulation)]
    public partial struct TimelineAnimationBlendTree2DTrackSystem : ISystem
    {
        // Below this planar speed (m^2/s^2, ~0.1 m/s) the body counts as stopped: the blend returns the r0 idle
        // centre instead of normalising a near-zero velocity into a jittery direction. Tune per game feel.
        private const float MoveDeadzoneSq = 0.01f;

        private UnsafeComponentLookup<BlendAnimationTree2DTrackData> _trackData;
        private UnsafeBufferLookup<BlendTree2DMotionData> _motionBuffer;
        private BufferLookup<BlendGroupEntry> _blendGroup;
        private BufferLookup<BlendTreePlaybackStateElement> _playbackState;

        private NativeParallelMultiHashMap<Entity, BlendTreeGatherCore.ClipData<float2>> _clipDataMap;
        private NativeList<Entity> _targetEntities;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            _trackData = state.GetUnsafeComponentLookup<BlendAnimationTree2DTrackData>(true);
            _motionBuffer = state.GetUnsafeBufferLookup<BlendTree2DMotionData>(true);
            _blendGroup = state.GetBufferLookup<BlendGroupEntry>();
            _playbackState = state.GetBufferLookup<BlendTreePlaybackStateElement>();
            _clipDataMap = new NativeParallelMultiHashMap<Entity, BlendTreeGatherCore.ClipData<float2>>(64, Allocator.Persistent);
            _targetEntities = new NativeList<Entity>(64, Allocator.Persistent);
            state.RequireForUpdate<BlobDatabaseSingleton>();
            state.RequireForUpdate<BlendTree2DDirectionClipData>();
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
                LocalToWorldLookup = SystemAPI.GetComponentLookup<LocalToWorld>(true),
                PlayerMoveInputLookup = SystemAPI.GetComponentLookup<PlayerMoveInput>(true),
                EntityLinkSourceLookup = state.GetUnsafeComponentLookup<EntityLinkSource>(true),
                EntityLinkEntryLookup = state.GetUnsafeBufferLookup<EntityLinkEntry>(true),
                TrackBindingLookup = SystemAPI.GetComponentLookup<TrackBinding>(true)
            }.ScheduleParallel(state.Dependency);

            var clipCount = SystemAPI.QueryBuilder()
                .WithAll<BlendTree2DDirectionClipData, ClipActive, TrackBinding, Clip, LocalTime>()
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
            [ReadOnly] public ComponentLookup<LocalToWorld> LocalToWorldLookup;
            [ReadOnly] public ComponentLookup<PlayerMoveInput> PlayerMoveInputLookup;
            [ReadOnly] public UnsafeComponentLookup<EntityLinkSource> EntityLinkSourceLookup;
            [ReadOnly] public UnsafeBufferLookup<EntityLinkEntry> EntityLinkEntryLookup;
            [ReadOnly] public ComponentLookup<TrackBinding> TrackBindingLookup;

            private void Execute(Entity clipEntity, ref BlendTree2DDirectionClipData clipData)
            {
                if (clipData.ReadKind == BlendDirectionReadKind.ClipValue)
                    return;

                if (clipData.ReadLinkKey == 0)
                {
                    clipData.Value = float2.zero;
                    return;
                }

                if (!TrackBindingLookup.TryGetComponent(clipEntity, out var binding) ||
                    !EntityLinkResolver.TryResolve(binding.Value, clipData.ReadLinkKey,
                        EntityLinkSourceLookup, EntityLinkEntryLookup, out var resolvedEntity))
                {
                    clipData.Value = float2.zero;
                    return;
                }

                // Camera basis is published once per frame by CameraGroundBasisSystem; every camera-relative clip on
                // every character reads this one shared value. Gated on Valid so a missing camera falls back cleanly.
                var basis = CameraGroundBasis.Data;
                var cameraRelative = clipData.CameraRelative && basis.Valid;

                if (clipData.ReadKind == BlendDirectionReadKind.PhysicsLinearVelocityNormalized)
                {
                    if (PhysicsVelocityLookup.TryGetComponent(resolvedEntity, out var pv))
                    {
                        var worldVelocity = new float3(pv.Linear.x, 0f, pv.Linear.z);

                        // Camera-relative: measure velocity in the camera's ground frame (screen-relative locomotion).
                        // Otherwise measure it in the character's own facing (the default, body-relative locomotion).
                        var facing = cameraRelative
                            ? quaternion.LookRotationSafe(basis.Forward, math.up())
                            : LocalToWorldLookup.TryGetComponent(resolvedEntity, out var ltw)
                                ? quaternion.LookRotationSafe(new float3(ltw.Forward.x, 0f, ltw.Forward.z), math.up())
                                : quaternion.identity;
                        var localVelocity = math.rotate(math.inverse(facing), worldVelocity);

                        // Direction only, scale-free: any movement above the deadzone snaps to the outer ring (full
                        // directional motion); at/near rest the value is the r0 centre (idle). No maxSpeed reference,
                        // so unbounded roguelike speed stats never saturate or shift a half-blend band - moving is
                        // always "1" in the facing direction, stopped is "0". Holding the last direction is avoided:
                        // zero selects the idle centre motion directly.
                        var dir = new float2(localVelocity.x, localVelocity.z);
                        clipData.Value = math.lengthsq(dir) > MoveDeadzoneSq ? math.normalize(dir) : float2.zero;
                    }
                    else
                    {
                        clipData.Value = float2.zero;
                    }
                }
                else if (clipData.ReadKind == BlendDirectionReadKind.PlayerMoveInput)
                {
                    if (PlayerMoveInputLookup.TryGetComponent(resolvedEntity, out var moveInput))
                    {
                        var vel2D = moveInput.Value;

                        if (cameraRelative)
                        {
                            // Lift the camera-relative stick into a world ground direction, then express it in the
                            // character's facing so the correct directional anim plays while the body is steered
                            // camera-relative. No camera facing -> identity (raw stick), matching the non-camera path.
                            var world = (basis.Right * vel2D.x) + (basis.Forward * vel2D.y);
                            var facing = LocalToWorldLookup.TryGetComponent(resolvedEntity, out var ltw)
                                ? quaternion.LookRotationSafe(new float3(ltw.Forward.x, 0f, ltw.Forward.z), math.up())
                                : quaternion.identity;
                            var local = math.rotate(math.inverse(facing), world);
                            vel2D = new float2(local.x, local.z);
                        }

                        var lengthSq = math.lengthsq(vel2D);
                        clipData.Value = lengthSq > 1f
                            ? vel2D / math.sqrt(lengthSq)
                            : vel2D;
                    }
                    else
                    {
                        clipData.Value = float2.zero;
                    }
                }

                clipData.Value = math.select(clipData.Value, float2.zero,
                    math.isnan(clipData.Value) | math.isinf(clipData.Value));
            }
        }

        [BurstCompile]
        [WithAll(typeof(ClipActive))]
        private partial struct GatherClipDataJob : IJobEntity
        {
            public NativeParallelMultiHashMap<Entity, BlendTreeGatherCore.ClipData<float2>>.ParallelWriter ClipDataMap;
            [ReadOnly] public ComponentLookup<Clip> ClipLookup;
            [ReadOnly] public ComponentLookup<ClipWeight> ClipWeightLookup;
            [ReadOnly] public ComponentLookup<CullAnimationsTag> CullLookup;

            private void Execute(Entity clipEntity, in BlendTree2DDirectionClipData directionData,
                in TrackBinding binding, in LocalTime localTime, in TimeTransform timeTransform)
            {
                // Off-screen rig: Rukhanka skips its pose computation, so gathering timeline clips for it is wasted.
                if (CullLookup.HasComponent(binding.Value) && CullLookup.IsComponentEnabled(binding.Value)) return;

                var weight = 1f;
                if (ClipWeightLookup.TryGetComponent(clipEntity, out var cw))
                    weight = cw.Value;

                if (weight <= 0f) return;

                var track = ClipLookup[clipEntity].Track;

                ClipDataMap.Add(binding.Value, new BlendTreeGatherCore.ClipData<float2>
                {
                    Track = track,
                    AbsoluteTime = (float)(double)localTime.Value,
                    TimeScale = (float)timeTransform.Scale,
                    Parameter = directionData.Value,
                    Weight = weight,
                    PositionOffset = directionData.PositionOffset,
                    RotationOffset = directionData.RotationOffset,
                    RemoveStartOffset = directionData.RemoveStartOffset,
                    ApplyFootIK = directionData.ApplyFootIK
                });
            }
        }

        [BurstCompile]
        private struct ExtractTargetEntitiesJob : IJob
        {
            [ReadOnly] public NativeParallelMultiHashMap<Entity, BlendTreeGatherCore.ClipData<float2>>.ReadOnly ClipDataMap;
            public NativeList<Entity> TargetEntities;

            public void Execute()
            {
                ClipDataMap.GetUniqueKeyArray(TargetEntities);
            }
        }

        [BurstCompile]
        private struct DecomposeAndAppendBlendTreeJob : IJobParallelForDefer
        {
            [ReadOnly] public NativeParallelMultiHashMap<Entity, BlendTreeGatherCore.ClipData<float2>>.ReadOnly ClipDataMap;
            [ReadOnly] public NativeList<Entity> TargetEntities;
            [ReadOnly] public NativeHashMap<Hash128, BlobAssetReference<AnimationClipBlob>> AnimDB;
            [ReadOnly] public UnsafeComponentLookup<BlendAnimationTree2DTrackData> TrackDataLookup;
            [ReadOnly] public UnsafeBufferLookup<BlendTree2DMotionData> MotionBufferLookup;

            [NativeDisableParallelForRestriction] public BufferLookup<BlendGroupEntry> BlendGroupLookup;

            [NativeDisableParallelForRestriction]
            public BufferLookup<BlendTreePlaybackStateElement> PlaybackStateLookup;

            public float GlobalDeltaTime;
            public bool IsScrubbing;

            public BLLogger Logger;

            public void Execute(int index)
            {
                BlendTreeGatherCore.Process<float2, Float2BlendOps, BlendTreePlaybackStateElement, PlaybackStateAccess2D,
                    BlendTree2DSolver>(
                    index, TargetEntities, ClipDataMap, BlendGroupLookup, PlaybackStateLookup, AnimDB, GlobalDeltaTime,
                    IsScrubbing, Logger, default, default,
                    new BlendTree2DSolver { MotionBufferLookup = MotionBufferLookup, TrackDataLookup = TrackDataLookup });
            }
        }

        private struct Float2BlendOps : BlendTreeGatherCore.IBlendParamOps<float2>
        {
            public float2 MulAdd(float2 accumulated, float2 raw, float weight)
            {
                return accumulated + (raw * weight);
            }

            public float2 Div(float2 accumulated, float weight)
            {
                return accumulated / weight;
            }
        }

        private struct PlaybackStateAccess2D : BlendTreeGatherCore.IPlaybackStateAccess<BlendTreePlaybackStateElement>
        {
            public Entity GetTrack(in BlendTreePlaybackStateElement element) => element.Track;

            public bool GetInitialized(in BlendTreePlaybackStateElement element) => element.IsInitialized;

            public float GetAccumulated(in BlendTreePlaybackStateElement element) => element.AccumulatedTime;

            public float GetPreviousAbsoluteTime(in BlendTreePlaybackStateElement element) => element.PreviousAbsoluteTime;

            public BlendTreePlaybackStateElement Create(Entity track, bool initialized, float accumulatedTime,
                float previousAbsoluteTime)
            {
                return new BlendTreePlaybackStateElement
                {
                    Track = track,
                    IsInitialized = initialized,
                    AccumulatedTime = accumulatedTime,
                    PreviousAbsoluteTime = previousAbsoluteTime
                };
            }
        }

        private struct BlendTree2DSolver : BlendTreeGatherCore.IBlendTreeSolver<float2>
        {
            [ReadOnly] public UnsafeBufferLookup<BlendTree2DMotionData> MotionBufferLookup;
            [ReadOnly] public UnsafeComponentLookup<BlendAnimationTree2DTrackData> TrackDataLookup;

            public bool TryPrepare(Entity trackEntity, float2 blendedParameter,
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
                var positions = new NativeArray<ScriptedAnimator.BlendTree2DMotionElement>(motionCount, Allocator.Temp);
                for (var i = 0; i < motionCount; i++)
                {
                    var motionData = motions[i];
                    var found = animDB.TryGetValue(motionData.AnimationHash, out var cb);
                    if (!found)
                        logger.LogWarning512(
                            "[BlendTree2D] Animation hash not found in BlobDatabaseSingleton. Motion entry will be skipped.");
                    clips[i] = found ? cb : BlobAssetReference<AnimationClipBlob>.Null;
                    positions[i] = motionData.BlendTree2DMotionElement;
                }

                weights = trackData.BlendTreeType switch
                {
                    MotionBlob.Type.BlendTree2DSimpleDirectional =>
                        ScriptedAnimator.ComputeBlendTree2DSimpleDirectional(positions, blendedParameter),
                    MotionBlob.Type.BlendTree2DFreeformCartesian =>
                        ScriptedAnimator.ComputeBlendTree2DFreeformCartesian(positions, blendedParameter),
                    MotionBlob.Type.BlendTree2DFreeformDirectional =>
                        ScriptedAnimator.ComputeBlendTree2DFreeformDirectional(positions, blendedParameter),
                    _ => default
                };

                positions.Dispose();

                if (!weights.IsCreated)
                {
                    logger.LogWarning512(
                        "[BlendTree2D] Unsupported BlendTreeType on track; only 2D blend types are handled. Track will be skipped.");
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
