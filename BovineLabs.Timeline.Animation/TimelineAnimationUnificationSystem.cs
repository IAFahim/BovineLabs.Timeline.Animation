using Rukhanka;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using Hash128 = Unity.Entities.Hash128;

namespace BovineLabs.Timeline.Animation
{
    [UpdateInGroup(typeof(TimelineComponentAnimationGroup))]
    [UpdateBefore(typeof(AnimationProcessSystem))]
    [Unity.Entities.WorldSystemFilter(Unity.Entities.WorldSystemFilterFlags.LocalSimulation | Unity.Entities.WorldSystemFilterFlags.ClientSimulation | Unity.Entities.WorldSystemFilterFlags.ServerSimulation)]
    public partial struct TimelineAnimationUnificationSystem : ISystem
    {
        // Weight threshold below which a clip/entry is considered fully faded out.
        private const float WeightEpsilon = 0.0001f;

        // Minimum clip duration guard to avoid division by zero.
        private const float MinDuration = 0.001f;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<BlobDatabaseSingleton>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var blobDB = SystemAPI.GetSingleton<BlobDatabaseSingleton>();

            var isScrubbing = false;
#if UNITY_EDITOR
            isScrubbing = !Application.isPlaying;
#endif

            var job = new UnifyAnimationsJob
            {
                AnimDB = blobDB.animations,
                DeltaTime = SystemAPI.Time.DeltaTime,
                IsScrubbing = isScrubbing
            };

            state.Dependency = job.ScheduleParallel(state.Dependency);
        }

        [BurstCompile]
        private partial struct UnifyAnimationsJob : IJobEntity
        {
            [ReadOnly] public NativeHashMap<Hash128, BlobAssetReference<AnimationClipBlob>> AnimDB;
            public float DeltaTime;
            public bool IsScrubbing; // ADDED

            public void Execute(
                Entity entity,
                ref BlendGroupTimer timer,
                ref FallbackBlend fallbackData,
                ref DynamicBuffer<BlendGroupEntry> blendEntries,
                ref DynamicBuffer<SmoothBlendGroupEntry> smoothEntries,
                ref DynamicBuffer<AnimationToProcessComponent> atps)
            {
                atps.Clear();

                for (var i = 0; i < smoothEntries.Length; i++)
                {
                    var s = smoothEntries[i];
                    s.TargetWeight = 0f;
                    smoothEntries[i] = s;
                }

                for (var i = 0; i < blendEntries.Length; i++)
                {
                    var request = blendEntries[i];
                    var smoothIndex = -1;

                    for (var j = 0; j < smoothEntries.Length; j++)
                        if (smoothEntries[j].MotionId == request.MotionId)
                        {
                            smoothIndex = j;
                            break;
                        }

                    if (smoothIndex != -1)
                    {
                        var s = smoothEntries[smoothIndex];
                        s.TargetWeight = request.Weight;
                        s.NormalizedTime = request.NormalizedTime;
                        s.LayerIndex = request.LayerIndex;
                        s.BlendMode = request.BlendMode;
                        s.AvatarMaskHash = request.AvatarMaskHash;
                        s.MotionId = request.MotionId;

                        s.PositionOffset = request.PositionOffset;
                        s.RotationOffset = request.RotationOffset;
                        s.RemoveStartOffset = request.RemoveStartOffset;
                        s.ApplyFootIK = request.ApplyFootIK;

                        smoothEntries[smoothIndex] = s;
                    }
                    else
                    {
                        smoothEntries.Add(new SmoothBlendGroupEntry
                        {
                            LayerIndex = request.LayerIndex,
                            ClipHash = request.ClipHash,
                            NormalizedTime = request.NormalizedTime,
                            CurrentWeight = 0f,
                            TargetWeight = request.Weight,
                            BlendMode = request.BlendMode,
                            AvatarMaskHash = request.AvatarMaskHash,
                            MotionId = request.MotionId,

                            PositionOffset = request.PositionOffset,
                            RotationOffset = request.RotationOffset,
                            RemoveStartOffset = request.RemoveStartOffset,
                            ApplyFootIK = request.ApplyFootIK
                        });
                    }
                }

                var totalOverrideWeight = 0f;

                for (var i = smoothEntries.Length - 1; i >= 0; i--)
                {
                    var s = smoothEntries[i];
                    var speed = s.CurrentWeight < s.TargetWeight
                        ? fallbackData.BlendInSpeed
                        : fallbackData.BlendOutSpeed;

                    if (IsScrubbing)
                    {
                        s.CurrentWeight = s.TargetWeight;
                    }
                    else
                    {
                        var lerpT = speed <= 0.0001f ? 1f : (1f - math.exp(-speed * DeltaTime));
                        s.CurrentWeight = math.lerp(s.CurrentWeight, s.TargetWeight, lerpT);
                    }

                    if (s.CurrentWeight <= WeightEpsilon && s.TargetWeight <= WeightEpsilon)
                    {
                        smoothEntries.RemoveAtSwapBack(i);
                        continue;
                    }

                    if (s.TargetWeight <= WeightEpsilon && AnimDB.TryGetValue(s.ClipHash, out var clipBlob) &&
                        clipBlob.IsCreated)
                    {
                        var duration = math.max(MinDuration, clipBlob.Value.length);
                        // Safe normalized time increment
                        s.NormalizedTime += (IsScrubbing ? 0 : DeltaTime) / duration;
                        s.NormalizedTime = math.frac(s.NormalizedTime);
                    }

                    if (s.BlendMode == AnimationBlendingMode.Override) totalOverrideWeight += s.CurrentWeight;

                    smoothEntries[i] = s;
                }

                var normalizeFactor = 1.0f;
                if (totalOverrideWeight > 1.0f)
                {
                    normalizeFactor = 1.0f / totalOverrideWeight;
                    totalOverrideWeight = 1.0f;
                }

                var fallbackWeight = 1.0f - totalOverrideWeight;
                if (fallbackWeight > WeightEpsilon && fallbackData.ClipHash != default)
                    if (AnimDB.TryGetValue(fallbackData.ClipHash, out var fallbackClip) && fallbackClip.IsCreated)
                    {
                        if (timer.PreviousFallbackClipHash != fallbackData.ClipHash)
                        {
                            timer.FallbackAccumulatedTime = 0f;
                            timer.PreviousFallbackClipHash = fallbackData.ClipHash;
                        }

                        var duration = math.max(MinDuration, fallbackClip.Value.length);

                        // Hold mode: stop accumulating once past clip end (don't wrap)
                        if (fallbackData.PlaybackMode == FallbackPlaybackMode.Hold)
                        {
                            if (timer.FallbackAccumulatedTime < 1f)
                                timer.FallbackAccumulatedTime += DeltaTime / duration;
                        }
                        else
                        {
                            timer.FallbackAccumulatedTime += DeltaTime / duration;
                        }

                        var fallbackTime = fallbackData.PlaybackMode switch
                        {
                            FallbackPlaybackMode.Clamp => math.min(timer.FallbackAccumulatedTime, 1f),
                            FallbackPlaybackMode.Hold => math.min(timer.FallbackAccumulatedTime, 1f),
                            _ => math.frac(timer.FallbackAccumulatedTime)
                        };

                        atps.Add(new AnimationToProcessComponent
                        {
                            animation = fallbackClip,
                            time = fallbackTime,
                            weight = fallbackWeight,
                            blendMode = fallbackData.BlendMode,
                            layerIndex = fallbackData.LayerIndex,
                            layerWeight = 1.0f,
                            motionId = 0xFFFFFFFF,

                            positionOffset = fallbackData.PositionOffset,
                            rotationOffset = fallbackData.RotationOffset,
                            removeStartOffset = fallbackData.RemoveStartOffset,
                            applyFootIK = fallbackData.ApplyFootIK
                        });
                    }

                for (var i = 0; i < smoothEntries.Length; i++)
                {
                    var s = smoothEntries[i];
                    if (AnimDB.TryGetValue(s.ClipHash, out var clipBlob) && clipBlob.IsCreated)
                    {
                        var appliedWeight = s.BlendMode == AnimationBlendingMode.Override
                            ? s.CurrentWeight * normalizeFactor
                            : s.CurrentWeight;

                        atps.Add(new AnimationToProcessComponent
                        {
                            animation = clipBlob,
                            time = s.NormalizedTime,
                            weight = appliedWeight,
                            blendMode = s.BlendMode,
                            layerIndex = s.LayerIndex,
                            layerWeight = 1.0f,
                            motionId = s.MotionId,

                            positionOffset = s.PositionOffset,
                            rotationOffset = s.RotationOffset,
                            removeStartOffset = s.RemoveStartOffset,
                            applyFootIK = s.ApplyFootIK
                        });
                    }
                }

                blendEntries.Clear();
            }
        }
    }
}