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
    [WorldSystemFilter(WorldSystemFilterFlags.LocalSimulation | WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    public partial struct TimelineAnimationUnificationSystem : ISystem
    {
        private const float WeightEpsilon = 0.0001f;

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
                MaskDB = blobDB.avatarMasks,
                DeltaTime = SystemAPI.Time.DeltaTime,
                IsScrubbing = isScrubbing
            };

            state.Dependency = job.ScheduleParallel(state.Dependency);
        }

        [BurstCompile]
        private partial struct UnifyAnimationsJob : IJobEntity
        {
            [ReadOnly] public NativeHashMap<Hash128, BlobAssetReference<AnimationClipBlob>> AnimDB;
            [ReadOnly] public NativeHashMap<Hash128, BlobAssetReference<AvatarMaskBlob>> MaskDB;
            public float DeltaTime;
            public bool IsScrubbing;

            public void Execute(
                ref BlendGroupTimer timer,
                in FallbackBlend fallbackData,
                ref DynamicBuffer<BlendGroupEntry> blendEntries,
                ref DynamicBuffer<SmoothBlendGroupEntry> smoothEntries,
                ref DynamicBuffer<AnimationToProcessComponent> atps)
            {
                atps.Clear();

                ReconcileRequests(ref blendEntries, ref smoothEntries);
                IntegrateWeights(ref smoothEntries, fallbackData.BlendInSpeed, fallbackData.BlendOutSpeed);

                var baseLayer = fallbackData.LayerIndex;

                EmitFallback(ref timer, in fallbackData, in smoothEntries, baseLayer, ref atps);
                EmitClips(in smoothEntries, baseLayer, ref atps);

                SortByLayer(ref atps);

                blendEntries.Clear();
            }

            private static void ReconcileRequests(
                ref DynamicBuffer<BlendGroupEntry> blendEntries,
                ref DynamicBuffer<SmoothBlendGroupEntry> smoothEntries)
            {
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
            }

            private void IntegrateWeights(
                ref DynamicBuffer<SmoothBlendGroupEntry> smoothEntries,
                float blendInSpeed,
                float blendOutSpeed)
            {
                for (var i = smoothEntries.Length - 1; i >= 0; i--)
                {
                    var s = smoothEntries[i];
                    var speed = s.CurrentWeight < s.TargetWeight ? blendInSpeed : blendOutSpeed;

                    if (IsScrubbing)
                    {
                        s.CurrentWeight = s.TargetWeight;
                    }
                    else
                    {
                        var lerpT = speed <= WeightEpsilon ? 1f : 1f - math.exp(-speed * DeltaTime);
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
                        s.NormalizedTime += (IsScrubbing ? 0f : DeltaTime) / duration;
                        s.NormalizedTime = math.frac(s.NormalizedTime);
                    }

                    smoothEntries[i] = s;
                }
            }

            private void EmitFallback(
                ref BlendGroupTimer timer,
                in FallbackBlend fallbackData,
                in DynamicBuffer<SmoothBlendGroupEntry> smoothEntries,
                int baseLayer,
                ref DynamicBuffer<AnimationToProcessComponent> atps)
            {
                var baseOverride = math.min(1f, OverrideSumForLayer(in smoothEntries, baseLayer));
                var fallbackWeight = 1f - baseOverride;

                if (fallbackWeight <= WeightEpsilon || fallbackData.ClipHash == default)
                    return;

                if (!AnimDB.TryGetValue(fallbackData.ClipHash, out var fallbackClip) || !fallbackClip.IsCreated)
                    return;

                if (timer.PreviousFallbackClipHash != fallbackData.ClipHash)
                {
                    timer.FallbackAccumulatedTime = 0f;
                    timer.PreviousFallbackClipHash = fallbackData.ClipHash;
                }

                var duration = math.max(MinDuration, fallbackClip.Value.length);

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
                    avatarMask = ResolveMask(fallbackData.AvatarMaskHash),
                    time = fallbackTime,
                    weight = fallbackWeight,
                    blendMode = fallbackData.BlendMode,
                    layerIndex = baseLayer,
                    layerWeight = 1.0f,
                    motionId = MotionId.Fallback,

                    positionOffset = fallbackData.PositionOffset,
                    rotationOffset = fallbackData.RotationOffset,
                    removeStartOffset = fallbackData.RemoveStartOffset,
                    applyFootIK = fallbackData.ApplyFootIK
                });
            }

            private void EmitClips(
                in DynamicBuffer<SmoothBlendGroupEntry> smoothEntries,
                int baseLayer,
                ref DynamicBuffer<AnimationToProcessComponent> atps)
            {
                for (var i = 0; i < smoothEntries.Length; i++)
                {
                    var s = smoothEntries[i];
                    if (!AnimDB.TryGetValue(s.ClipHash, out var clipBlob) || !clipBlob.IsCreated)
                        continue;

                    float weight;
                    float layerWeight;

                    if (s.BlendMode == AnimationBlendingMode.Override)
                    {
                        var layerSum = OverrideSumForLayer(in smoothEntries, s.LayerIndex);

                        if (s.LayerIndex == baseLayer)
                        {
                            var normalizeFactor = layerSum > 1f ? 1f / layerSum : 1f;
                            weight = s.CurrentWeight * normalizeFactor;
                            layerWeight = 1.0f;
                        }
                        else
                        {
                            weight = s.CurrentWeight / math.max(WeightEpsilon, layerSum);
                            layerWeight = math.saturate(layerSum);
                        }
                    }
                    else
                    {
                        weight = s.CurrentWeight;
                        layerWeight = 1.0f;
                    }

                    atps.Add(new AnimationToProcessComponent
                    {
                        animation = clipBlob,
                        avatarMask = ResolveMask(s.AvatarMaskHash),
                        time = s.NormalizedTime,
                        weight = weight,
                        blendMode = s.BlendMode,
                        layerIndex = s.LayerIndex,
                        layerWeight = layerWeight,
                        motionId = s.MotionId,

                        positionOffset = s.PositionOffset,
                        rotationOffset = s.RotationOffset,
                        removeStartOffset = s.RemoveStartOffset,
                        applyFootIK = s.ApplyFootIK
                    });
                }
            }

            private static float OverrideSumForLayer(in DynamicBuffer<SmoothBlendGroupEntry> entries, int layer)
            {
                var sum = 0f;
                for (var i = 0; i < entries.Length; i++)
                {
                    var e = entries[i];
                    if (e.BlendMode == AnimationBlendingMode.Override && e.LayerIndex == layer)
                        sum += e.CurrentWeight;
                }

                return sum;
            }

            private readonly BlobAssetReference<AvatarMaskBlob> ResolveMask(Hash128 maskHash)
            {
                if (maskHash != default && MaskDB.TryGetValue(maskHash, out var mask) && mask.IsCreated)
                    return mask;

                return default;
            }

            private static void SortByLayer(ref DynamicBuffer<AnimationToProcessComponent> atps)
            {
                for (var i = 1; i < atps.Length; i++)
                {
                    var key = atps[i];
                    var j = i - 1;
                    while (j >= 0 && atps[j].layerIndex > key.layerIndex)
                    {
                        atps[j + 1] = atps[j];
                        j--;
                    }

                    atps[j + 1] = key;
                }
            }
        }
    }
}