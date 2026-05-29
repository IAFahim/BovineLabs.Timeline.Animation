using BovineLabs.Core.Collections;
using BovineLabs.Core.Extensions;
using BovineLabs.Core.Iterators;
using BovineLabs.Timeline.Data;
using BovineLabs.Timeline.PlayerInputs.Data;
using Rukhanka;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
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
    [Unity.Entities.WorldSystemFilter(Unity.Entities.WorldSystemFilterFlags.LocalSimulation | Unity.Entities.WorldSystemFilterFlags.ClientSimulation | Unity.Entities.WorldSystemFilterFlags.ServerSimulation)]
    public partial struct TimelineAnimationBlendTree2DTrackSystem : ISystem
    {
        /// <summary>
        ///     Accumulated per-clip data for a single timeline clip on a blend tree track.
        ///     Multiple clips may target the same track; their directions and weights are
        ///     combined in <see cref="PerTrackBlend" /> before the actual blend tree evaluation.
        /// </summary>
        internal struct TrackClipData
        {
            public Entity Track;
            public float AbsoluteTime;

            /// <summary>Weighted direction contribution from this clip (pre-normalization).</summary>
            public float2 Direction;

            /// <summary>Clip weight from ClipWeight component or default 1.0.</summary>
            public float Weight;
        }

        private const float WeightEpsilon = 0.0001f;

        private const float MinDuration = 0.001f;

        private const float DirectionEpsilon = 0.0001f;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<BlobDatabaseSingleton>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var blobDB = SystemAPI.GetSingleton<BlobDatabaseSingleton>();

            state.Dependency = new UpdateDynamicBlendParametersJob
            {
                PhysicsVelocityLookup = SystemAPI.GetComponentLookup<PhysicsVelocity>(true),
                PlayerMoveInputLookup = SystemAPI.GetComponentLookup<PlayerMoveInput>(true)
            }.ScheduleParallel(state.Dependency);

            var clipCount = SystemAPI.QueryBuilder()
                .WithAll<BlendTree2DDirectionClipData, ClipActive, TrackBinding, Clip, LocalTime>()
                .Build().CalculateEntityCount();
            var clipDataMap = new NativeParallelMultiHashMap<Entity, TrackClipData>(
                math.max(1, clipCount), Allocator.TempJob);
            var targetEntities = new NativeList<Entity>(math.max(1, clipCount), Allocator.TempJob);

            state.Dependency = new GatherClipDataJob
            {
                ClipDataMap = clipDataMap.AsParallelWriter(),
                ClipLookup = SystemAPI.GetComponentLookup<Clip>(true),
                ClipWeightLookup = SystemAPI.GetComponentLookup<ClipWeight>(true)
            }.ScheduleParallel(state.Dependency);

            state.Dependency = new ExtractTargetEntitiesJob
            {
                ClipDataMap = clipDataMap.AsReadOnly(),
                TargetEntities = targetEntities
            }.Schedule(state.Dependency);

            var isScrubbing = false;
#if UNITY_EDITOR
            isScrubbing = !Application.isPlaying;
#endif

            state.Dependency = new DecomposeAndAppendBlendTreeJob
            {
                TargetEntities = targetEntities,
                ClipDataMap = clipDataMap.AsReadOnly(),
                AnimDB = blobDB.animations,
                TrackDataLookup = state.GetUnsafeComponentLookup<BlendAnimationTree2DTrackData>(true),
                MotionBufferLookup = state.GetUnsafeBufferLookup<BlendTree2DMotionData>(true),
                FallbackOverrideLookup = state.GetUnsafeComponentLookup<TrackFallbackOverride>(true),
                DefaultFallbackLookup = state.GetUnsafeComponentLookup<DefaultBlendGroupFallback>(true),
                BlendGroupLookup = state.GetBufferLookup<BlendGroupEntry>(false),
                PlaybackStateLookup = state.GetBufferLookup<BlendTreePlaybackStateElement>(false),
                FallbackLookup = state.GetComponentLookup<FallbackBlend>(false),
                GlobalDeltaTime = SystemAPI.Time.DeltaTime,
                IsScrubbing = isScrubbing
            }.Schedule(targetEntities, 64, state.Dependency);

            state.Dependency = new ResetStaleFallbackJob
            {
                TargetEntities = targetEntities.AsDeferredJobArray()
            }.Schedule(state.Dependency);

            targetEntities.Dispose(state.Dependency);
            clipDataMap.Dispose(state.Dependency);
        }

        [BurstCompile]
        [WithAll(typeof(ClipActive))]
        private partial struct UpdateDynamicBlendParametersJob : IJobEntity
        {
            [ReadOnly] public ComponentLookup<PhysicsVelocity> PhysicsVelocityLookup;
            [ReadOnly] public ComponentLookup<PlayerMoveInput> PlayerMoveInputLookup;

            private void Execute(ref BlendTree2DDirectionClipData clipData)
            {
                if (clipData.ReadKind == BlendDirectionReadKind.PhysicsLinearVelocityNormalized)
                {
                    if (PhysicsVelocityLookup.TryGetComponent(clipData.ReadEntity, out var pv))
                    {
                        var vel2d = new float2(pv.Linear.x, pv.Linear.z);
                        var lengthSq = math.lengthsq(vel2d);
                        clipData.Value = lengthSq > DirectionEpsilon
                            ? vel2d / math.sqrt(lengthSq)
                            : float2.zero;
                    }
                    else
                    {
                        clipData.Value = float2.zero;
                    }
                }
                else if (clipData.ReadKind == BlendDirectionReadKind.PlayerMoveInput)
                {
                    if (PlayerMoveInputLookup.TryGetComponent(clipData.ReadEntity, out var moveInput))
                    {
                        var vel2d = moveInput.Value;
                        var lengthSq = math.lengthsq(vel2d);
                        clipData.Value = lengthSq > 1f
                            ? vel2d / math.sqrt(lengthSq)
                            : vel2d;
                    }
                    else
                    {
                        clipData.Value = float2.zero;
                    }
                }
            }
        }

        [BurstCompile]
        [WithAll(typeof(ClipActive))]
        private partial struct GatherClipDataJob : IJobEntity
        {
            public NativeParallelMultiHashMap<Entity, TrackClipData>.ParallelWriter ClipDataMap;
            [ReadOnly] public ComponentLookup<Clip> ClipLookup;
            [ReadOnly] public ComponentLookup<ClipWeight> ClipWeightLookup;

            private void Execute(Entity clipEntity, in BlendTree2DDirectionClipData directionData,
                in TrackBinding binding, in LocalTime localTime)
            {
                var weight = 1f;
                if (ClipWeightLookup.TryGetComponent(clipEntity, out var cw))
                    weight = cw.Value;

                if (weight <= 0f) return;

                var track = ClipLookup[clipEntity].Track;

                ClipDataMap.Add(binding.Value, new TrackClipData
                {
                    Track = track,
                    AbsoluteTime = (float)localTime.Value,
                    Direction = directionData.Value,
                    Weight = weight
                });
            }
        }

        [BurstCompile]
        private struct ExtractTargetEntitiesJob : IJob
        {
            [ReadOnly] public NativeParallelMultiHashMap<Entity, TrackClipData>.ReadOnly ClipDataMap;
            public NativeList<Entity> TargetEntities;

            public void Execute()
            {
                ClipDataMap.GetUniqueKeyArray(TargetEntities);
            }
        }

        [BurstCompile]
        private struct DecomposeAndAppendBlendTreeJob : IJobParallelForDefer
        {
            [ReadOnly] public NativeParallelMultiHashMap<Entity, TrackClipData>.ReadOnly ClipDataMap;
            [ReadOnly] public NativeList<Entity> TargetEntities;
            [ReadOnly] public NativeHashMap<Hash128, BlobAssetReference<AnimationClipBlob>> AnimDB;
            [ReadOnly] public UnsafeComponentLookup<BlendAnimationTree2DTrackData> TrackDataLookup;
            [ReadOnly] public UnsafeBufferLookup<BlendTree2DMotionData> MotionBufferLookup;
            [ReadOnly] public UnsafeComponentLookup<TrackFallbackOverride> FallbackOverrideLookup;
            [ReadOnly] public UnsafeComponentLookup<DefaultBlendGroupFallback> DefaultFallbackLookup;

            [NativeDisableParallelForRestriction] public BufferLookup<BlendGroupEntry> BlendGroupLookup;

            [NativeDisableParallelForRestriction]
            public BufferLookup<BlendTreePlaybackStateElement> PlaybackStateLookup;

            [NativeDisableParallelForRestriction] public ComponentLookup<FallbackBlend> FallbackLookup;

            public float GlobalDeltaTime;
            public bool IsScrubbing;

            public unsafe void Execute(int index)
            {
                var targetEntity = TargetEntities[index];

                if (!BlendGroupLookup.TryGetBuffer(targetEntity, out var blendGroupBuffer)) return;

                var bestFallbackLayer = -1;
                TrackFallbackOverride bestFallback = default;
                var hasFallbackCandidate = false;

                const int stackTrackCapacity = 128;
                var processedTracks = stackalloc PerTrackBlend[stackTrackCapacity];
                var processedTrackCount = 0;
                var fallbackToMap = false;

                if (ClipDataMap.TryGetFirstValue(targetEntity, out var clipData, out var it))
                    do
                    {
                        var blendIndex = -1;
                        for (var i = 0; i < processedTrackCount; i++)
                            if (processedTracks[i].TrackEntity == clipData.Track)
                            {
                                blendIndex = i;
                                break;
                            }

                        if (blendIndex == -1)
                        {
                            if (processedTrackCount >= stackTrackCapacity)
                            {
                                fallbackToMap = true;
                                break;
                            }

                            blendIndex = processedTrackCount++;
                            processedTracks[blendIndex] = new PerTrackBlend { TrackEntity = clipData.Track };
                        }

                        var blend = processedTracks[blendIndex];

                        blend.DirectionX += clipData.Direction.x * clipData.Weight;
                        blend.DirectionY += clipData.Direction.y * clipData.Weight;
                        blend.TotalWeight += clipData.Weight;

                        if (clipData.Weight > blend.BestWeight)
                        {
                            blend.BestWeight = clipData.Weight;
                            blend.AbsoluteTime = clipData.AbsoluteTime;
                        }

                        processedTracks[blendIndex] = blend;
                    } while (ClipDataMap.TryGetNextValue(out clipData, ref it));

                if (fallbackToMap)
                {
                    ProcessTracksWithList(targetEntity, ref bestFallbackLayer, ref bestFallback,
                        ref hasFallbackCandidate);
                }
                else
                {
                    for (var i = 0; i < processedTrackCount; i++)
                        ProcessTrackBlend(targetEntity, processedTracks[i], ref bestFallbackLayer, ref bestFallback,
                            ref hasFallbackCandidate);

                    CleanupOrphanPlaybackStates(targetEntity, processedTracks, processedTrackCount);
                }

                if (hasFallbackCandidate)
                {
                    if (FallbackLookup.HasComponent(targetEntity))
                    {
                        var prev = FallbackLookup[targetEntity];
                        var next = new FallbackBlend
                        {
                            ClipHash = bestFallback.FallbackClipHash,
                            BlendInSpeed = bestFallback.BlendInSpeed,
                            BlendOutSpeed = bestFallback.BlendOutSpeed,
                            PlaybackMode = bestFallback.PlaybackMode,
                            LayerIndex = bestFallback.LayerIndex,
                            BlendMode = bestFallback.BlendMode,
                            AvatarMaskHash = bestFallback.AvatarMaskHash,
                            PositionOffset = bestFallback.PositionOffset,
                            RotationOffset = bestFallback.RotationOffset,
                            RemoveStartOffset = bestFallback.RemoveStartOffset,
                            ApplyFootIK = bestFallback.ApplyFootIK
                        };

                        if (prev.ClipHash != next.ClipHash)
                            FallbackLookup[targetEntity] = next;
                    }
                }
                else if (DefaultFallbackLookup.TryGetComponent(targetEntity, out var defaults))
                {
                    if (FallbackLookup.HasComponent(targetEntity))
                    {
                        var prev = FallbackLookup[targetEntity];
                        var next = new FallbackBlend
                        {
                            ClipHash = defaults.ClipHash,
                            BlendInSpeed = defaults.BlendInSpeed,
                            BlendOutSpeed = defaults.BlendOutSpeed,
                            PlaybackMode = defaults.PlaybackMode,
                            LayerIndex = defaults.LayerIndex,
                            BlendMode = defaults.BlendMode,
                            AvatarMaskHash = defaults.AvatarMaskHash,
                            PositionOffset = defaults.PositionOffset,
                            RotationOffset = defaults.RotationOffset,
                            RemoveStartOffset = defaults.RemoveStartOffset,
                            ApplyFootIK = defaults.ApplyFootIK
                        };

                        if (prev.ClipHash != next.ClipHash)
                            FallbackLookup[targetEntity] = next;
                    }
                }
            }

            private void ProcessTracksWithList(Entity targetEntity, ref int bestFallbackLayer,
                ref TrackFallbackOverride bestFallback, ref bool hasFallbackCandidate)
            {
                var processedTracks = new UnsafeList<PerTrackBlend>(16, Allocator.Temp);

                if (ClipDataMap.TryGetFirstValue(targetEntity, out var clipData, out var it))
                    do
                    {
                        var blendIndex = -1;
                        for (var i = 0; i < processedTracks.Length; i++)
                            if (processedTracks[i].TrackEntity == clipData.Track)
                            {
                                blendIndex = i;
                                break;
                            }

                        if (blendIndex == -1)
                        {
                            processedTracks.Add(new PerTrackBlend { TrackEntity = clipData.Track });
                            blendIndex = processedTracks.Length - 1;
                        }

                        var blend = processedTracks[blendIndex];

                        blend.DirectionX += clipData.Direction.x * clipData.Weight;
                        blend.DirectionY += clipData.Direction.y * clipData.Weight;
                        blend.TotalWeight += clipData.Weight;

                        if (clipData.Weight > blend.BestWeight)
                        {
                            blend.BestWeight = clipData.Weight;
                            blend.AbsoluteTime = clipData.AbsoluteTime;
                        }

                        processedTracks[blendIndex] = blend;
                    } while (ClipDataMap.TryGetNextValue(out clipData, ref it));

                processedTracks.Sort();

                for (var i = 0; i < processedTracks.Length; i++)
                    ProcessTrackBlend(targetEntity, processedTracks[i], ref bestFallbackLayer, ref bestFallback,
                        ref hasFallbackCandidate);

                CleanupOrphanPlaybackStatesHeap(targetEntity, ref processedTracks);
                processedTracks.Dispose();
            }

            private void ProcessTrackBlend(Entity targetEntity, in PerTrackBlend blend, ref int bestFallbackLayer,
                ref TrackFallbackOverride bestFallback, ref bool hasFallbackCandidate)
            {
                if (blend.TotalWeight <= 0f) return;

                var trackEntity = blend.TrackEntity;
                if (FallbackOverrideLookup.TryGetComponent(trackEntity, out var fo) &&
                    TrackDataLookup.TryGetComponent(trackEntity, out var td) &&
                    td.LayerIndex > bestFallbackLayer)
                {
                    bestFallbackLayer = td.LayerIndex;
                    bestFallback = fo;
                    hasFallbackCandidate = true;
                }

                var totalWeight = math.saturate(blend.TotalWeight);
                var blendedDirection = new float2(blend.DirectionX, blend.DirectionY) /
                                       math.max(DirectionEpsilon, blend.TotalWeight);

                ProcessTrack(targetEntity, trackEntity, blendedDirection, totalWeight, blend.AbsoluteTime);
            }

            private unsafe void ProcessTrack(
                Entity targetEntity,
                Entity trackEntity,
                float2 blendedDirection,
                float totalTimelineWeight,
                float absoluteTime)
            {
                if (!MotionBufferLookup.TryGetBuffer(trackEntity, out var motions) ||
                    !TrackDataLookup.TryGetComponent(trackEntity, out var trackData) ||
                    !BlendGroupLookup.TryGetBuffer(targetEntity, out var blendGroupBuffer)) return;

                var motionCount = motions.Length;
                if (motionCount <= 0) return;

                const int stackMotionCapacity = 64;

                if (motionCount <= stackMotionCapacity)
                {
                    var blendTreeClipsData = stackalloc BlobAssetReference<AnimationClipBlob>[stackMotionCapacity];
                    var blendTreePositionsData =
                        stackalloc ScriptedAnimator.BlendTree2DMotionElement[stackMotionCapacity];
                    var blendTreeClips = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<
                        BlobAssetReference<AnimationClipBlob>>(blendTreeClipsData, motionCount, Allocator.None);
                    var blendTreePositions = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<
                        ScriptedAnimator.BlendTree2DMotionElement>(blendTreePositionsData, motionCount, Allocator.None);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref blendTreeClips,
                        AtomicSafetyHandle.GetTempMemoryHandle());
                    NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref blendTreePositions,
                        AtomicSafetyHandle.GetTempMemoryHandle());
#endif

                    PopulateTrackData(motions, blendTreeClips, blendTreePositions);
                    ProcessTrackMotions(targetEntity, trackEntity, blendedDirection, totalTimelineWeight, absoluteTime,
                        trackData, blendGroupBuffer, blendTreeClips, blendTreePositions);
                    return;
                }

                var heapBlendTreeClips =
                    new NativeArray<BlobAssetReference<AnimationClipBlob>>(motionCount, Allocator.Temp);
                var heapBlendTreePositions =
                    new NativeArray<ScriptedAnimator.BlendTree2DMotionElement>(motionCount, Allocator.Temp);

                PopulateTrackData(motions, heapBlendTreeClips, heapBlendTreePositions);
                ProcessTrackMotions(targetEntity, trackEntity, blendedDirection, totalTimelineWeight, absoluteTime,
                    trackData, blendGroupBuffer, heapBlendTreeClips, heapBlendTreePositions);

                heapBlendTreeClips.Dispose();
                heapBlendTreePositions.Dispose();
            }

            private void PopulateTrackData(UnsafeDynamicBuffer<BlendTree2DMotionData> motions,
                NativeArray<BlobAssetReference<AnimationClipBlob>> blendTreeClips,
                NativeArray<ScriptedAnimator.BlendTree2DMotionElement> blendTreePositions)
            {
                for (var i = 0; i < motions.Length; i++)
                {
                    var motionData = motions[i];
                    var found = AnimDB.TryGetValue(motionData.AnimationHash, out var cb);
#if UNITY_EDITOR
                    if (!found)
                        Debug.LogWarning(
                            "[BlendTree2D] Animation hash not found in BlobDatabaseSingleton. Motion entry will be skipped.");
#endif
                    blendTreeClips[i] = found ? cb : BlobAssetReference<AnimationClipBlob>.Null;
                    blendTreePositions[i] = motionData.BlendTree2DMotionElement;
                }
            }

            private void ProcessTrackMotions(
                Entity targetEntity,
                Entity trackEntity,
                float2 blendedDirection,
                float totalTimelineWeight,
                float absoluteTime,
                in BlendAnimationTree2DTrackData trackData,
                DynamicBuffer<BlendGroupEntry> blendGroupBuffer,
                NativeArray<BlobAssetReference<AnimationClipBlob>> blendTreeClips,
                NativeArray<ScriptedAnimator.BlendTree2DMotionElement> blendTreePositions)
            {
                var internalWeights = trackData.BlendTreeType switch
                {
                    MotionBlob.Type.BlendTree2DSimpleDirectional =>
                        ScriptedAnimator.ComputeBlendTree2DSimpleDirectional(blendTreePositions, blendedDirection),
                    MotionBlob.Type.BlendTree2DFreeformCartesian =>
                        ScriptedAnimator.ComputeBlendTree2DFreeformCartesian(blendTreePositions, blendedDirection),
                    MotionBlob.Type.BlendTree2DFreeformDirectional =>
                        ScriptedAnimator.ComputeBlendTree2DFreeformDirectional(blendTreePositions, blendedDirection),
                    _ => default
                };

                var weightedDuration = 0f;
                var totalBlendWeight = 0f;

                for (var i = 0; i < internalWeights.Length; i++)
                {
                    var mw = internalWeights[i];
                    if (blendTreeClips[mw.motionIndex].IsCreated)
                    {
                        weightedDuration += blendTreeClips[mw.motionIndex].Value.length * mw.weight;
                        totalBlendWeight += mw.weight;
                    }
                }

                if (totalBlendWeight > 0f) weightedDuration /= totalBlendWeight;
                if (weightedDuration <= MinDuration) weightedDuration = 1f;

                var normalizedTime = 0f;

                if (PlaybackStateLookup.TryGetBuffer(targetEntity, out var stateBuffer))
                {
                    var stateIdx = -1;
                    for (var i = 0; i < stateBuffer.Length; i++)
                        if (stateBuffer[i].Track == trackEntity)
                        {
                            stateIdx = i;
                            break;
                        }

                    if (stateIdx == -1)
                    {
                        stateIdx = stateBuffer.Length;
                        stateBuffer.Add(
                            new BlendTreePlaybackStateElement { Track = trackEntity, IsInitialized = false });
                    }

                    var ps = stateBuffer[stateIdx];

                    if (!ps.IsInitialized)
                    {
                        var initialTime = absoluteTime / weightedDuration;
                        ps.AccumulatedTime = initialTime;
                        ps.PreviousAbsoluteTime = absoluteTime;
                        ps.IsInitialized = true;
                        normalizedTime = math.frac(initialTime);
                    }
                    else
                    {
                        var delta = absoluteTime - ps.PreviousAbsoluteTime;
                        if (!IsScrubbing && math.abs(delta) > 1.0f) delta = GlobalDeltaTime;
                        ps.AccumulatedTime += delta / weightedDuration;
                        ps.PreviousAbsoluteTime = absoluteTime;
                        normalizedTime = math.frac(ps.AccumulatedTime);
                    }

                    stateBuffer[stateIdx] = ps;
                }

                var avatarMaskHash = trackData.ApplyAvatarMask ? trackData.AvatarMaskHash : default;
                var trackPosOffset = trackData.TrackPositionOffset;
                var trackRotOffset = trackData.TrackRotationOffset;
                var hasOffsets = math.lengthsq(trackPosOffset) > WeightEpsilon ||
                                 math.lengthsq(trackRotOffset.value.xyz) > WeightEpsilon;

                for (var i = 0; i < internalWeights.Length; i++)
                {
                    var mw = internalWeights[i];
                    var clipBlob = blendTreeClips[mw.motionIndex];

                    if (clipBlob.IsCreated && mw.weight > 0f)
                    {
                        var clipHash = clipBlob.Value.hash;
                        blendGroupBuffer.Add(new BlendGroupEntry
                        {
                            LayerIndex = trackData.LayerIndex,
                            ClipHash = clipHash,
                            NormalizedTime = normalizedTime,
                            Weight = mw.weight * totalTimelineWeight,
                            AvatarMaskHash = avatarMaskHash,
                            BlendMode = AnimationBlendingMode.Override,
                            MotionId = ComputeMotionId(trackEntity, trackData.LayerIndex, clipHash),
                            PositionOffset = trackPosOffset,
                            RotationOffset = trackRotOffset,
                            RemoveStartOffset = hasOffsets,
                            ApplyFootIK = true
                        });
                    }
                }

                internalWeights.Dispose();
            }

            private uint ComputeMotionId(Entity track, int layerIndex, Hash128 clipHash)
            {
                var hash = (uint)track.Index;
                hash = (hash * 31) ^ (uint)track.Version;
                hash = (hash * 31) ^ (uint)layerIndex;
                hash = (hash * 31) ^ (uint)clipHash.GetHashCode();
                return hash;
            }

            /// <summary>
            ///     Removes BlendTreePlaybackStateElement entries for tracks that are no longer active.
            ///     Stack-based version used when track count is within stackalloc capacity.
            /// </summary>
            private unsafe void CleanupOrphanPlaybackStates(
                Entity targetEntity,
                PerTrackBlend* activeTracks,
                int activeTrackCount)
            {
                if (!PlaybackStateLookup.TryGetBuffer(targetEntity, out var stateBuffer)) return;

                for (var i = stateBuffer.Length - 1; i >= 0; i--)
                {
                    var track = stateBuffer[i].Track;
                    var found = false;
                    for (var j = 0; j < activeTrackCount; j++)
                        if (activeTracks[j].TrackEntity == track)
                        {
                            found = true;
                            break;
                        }

                    if (!found)
                        stateBuffer.RemoveAtSwapBack(i);
                }
            }

            /// <summary>
            ///     Removes BlendTreePlaybackStateElement entries for tracks that are no longer active.
            ///     Heap-based version used when track count exceeds stackalloc capacity.
            /// </summary>
            private void CleanupOrphanPlaybackStatesHeap(
                Entity targetEntity,
                ref UnsafeList<PerTrackBlend> activeTracks)
            {
                if (!PlaybackStateLookup.TryGetBuffer(targetEntity, out var stateBuffer)) return;

                for (var i = stateBuffer.Length - 1; i >= 0; i--)
                {
                    var track = stateBuffer[i].Track;
                    var found = false;
                    for (var j = 0; j < activeTracks.Length; j++)
                        if (activeTracks[j].TrackEntity == track)
                        {
                            found = true;
                            break;
                        }

                    if (!found)
                        stateBuffer.RemoveAtSwapBack(i);
                }
            }
        }

        /// <summary>
        ///     Resets FallbackBlend to DefaultBlendGroupFallback for entities not processed by
        ///     DecomposeAndAppendBlendTreeJob (i.e. entities with no active blend-tree clips).
        /// </summary>
        [BurstCompile]
        private partial struct ResetStaleFallbackJob : IJobEntity
        {
            [ReadOnly] public NativeArray<Entity> TargetEntities;

            public void Execute(Entity entity, ref FallbackBlend fallback,
                in DefaultBlendGroupFallback defaults)
            {
                for (var i = 0; i < TargetEntities.Length; i++)
                    if (TargetEntities[i] == entity)
                        return;

                fallback = new FallbackBlend
                {
                    ClipHash = defaults.ClipHash,
                    BlendInSpeed = defaults.BlendInSpeed,
                    BlendOutSpeed = defaults.BlendOutSpeed,
                    PlaybackMode = defaults.PlaybackMode,
                    LayerIndex = defaults.LayerIndex,
                    BlendMode = defaults.BlendMode,
                    AvatarMaskHash = defaults.AvatarMaskHash,
                    PositionOffset = defaults.PositionOffset,
                    RotationOffset = defaults.RotationOffset,
                    RemoveStartOffset = defaults.RemoveStartOffset,
                    ApplyFootIK = defaults.ApplyFootIK
                };
            }
        }

        private struct PerTrackBlend : System.IComparable<PerTrackBlend>
        {
            public Entity TrackEntity;
            public float DirectionX;
            public float DirectionY;
            public float TotalWeight;
            public float BestWeight;
            public float AbsoluteTime;

            public int CompareTo(PerTrackBlend other)
            {
                var cmp = TrackEntity.Index.CompareTo(other.TrackEntity.Index);
                if (cmp != 0) return cmp;
                return TrackEntity.Version.CompareTo(other.TrackEntity.Version);
            }
        }
    }
}