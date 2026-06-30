using BovineLabs.Core.Authoring.EntityCommands;
using BovineLabs.Timeline.Animation.Data.Builders;
using Rukhanka;
using Rukhanka.Hybrid;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using Hash128 = Unity.Entities.Hash128;

namespace BovineLabs.Timeline.Animation.Authoring
{
    public class TimelineAnimationStateAuthoring : MonoBehaviour
    {
        [Tooltip("The animation to play when no timeline clips are active.")]
        public AnimationClip fallbackAnimationClip;

        [Tooltip("How the fallback animation wraps: Loop restarts, Clamp stops at end, Hold stays on last frame.")]
        public FallbackPlaybackMode fallbackPlaybackMode = FallbackPlaybackMode.Loop;

        [Tooltip("Global crossfade time in seconds. Smooths every transition INCLUDING switches between separate timelines (trackA ending -> trackB starting), where per-clip ease alone cannot crossfade. 0 = snap instantly (hard cut / per-clip ease governs).")] [Min(0f)]
        public float blendInDuration = 0.2f;

        [Tooltip("Global crossfade time in seconds. Smooths every transition INCLUDING switches between separate timelines (trackA ending -> trackB starting), where per-clip ease alone cannot crossfade. 0 = snap instantly (hard cut / per-clip ease governs).")] [Min(0f)]
        public float blendOutDuration = 0.2f;

        [Header("Fallback Transform Offsets")] public Vector3 positionOffset = Vector3.zero;

        public Vector3 eulerAnglesOffset = Vector3.zero;

        [Tooltip(
            "When enabled, strips the clip's baked root motion start pose so the animation begins at the track/fallback offset position. Only enable when position/rotation offsets are also set.")]
        public bool removeStartOffset;

        public bool applyFootIK = true;

        [Header("Inertialization")]
        [Tooltip(
            "Momentum-preserving transition window in seconds. 0 = OFF (exactly the current crossfade-only behavior). " +
            "When > 0, a dominant-clip change cuts to the new clip immediately and a per-bone offset decays to zero " +
            "over this window, carrying the previous motion's momentum across the cut (no mush, no foot slide). " +
            "Typical 0.1-0.3. With this on you can lower the global blend durations to 0 for the crispest result.")]
        [Min(0f)]
        public float inertializationDuration;

        public class Baker : Baker<TimelineAnimationStateAuthoring>
        {
            private readonly AnimationClip[] singleClipBuffer = new AnimationClip[1];

            public override void Bake(TimelineAnimationStateAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.None);
                var rigDef = GetComponent<RigDefinitionAuthoring>();
                var avatar = rigDef != null ? rigDef.GetAvatar() : null;

                var commands = new BakerCommands(this, entity);
                var builder = new TimelineAnimationStateBuilder()
                    .WithFallbackOffsets(authoring.positionOffset, Quaternion.Euler(authoring.eulerAnglesOffset),
                        authoring.removeStartOffset, authoring.applyFootIK)
                    .WithFallback(default, authoring.blendInDuration, authoring.blendOutDuration,
                        authoring.fallbackPlaybackMode);

                if (authoring.fallbackAnimationClip != null && avatar != null)
                {
                    var (fallbackHash, fallbackBlob) = BakeFallbackAnimation(authoring, avatar, entity);
                    builder = builder.WithFallback(fallbackHash, authoring.blendInDuration, authoring.blendOutDuration,
                            authoring.fallbackPlaybackMode)
                        .WithFallbackBlob(fallbackBlob, fallbackHash);
                }
                else if (authoring.fallbackAnimationClip != null)
                {
                    Debug.LogWarning(
                        $"{nameof(TimelineAnimationStateAuthoring)} on '{authoring.name}' has a fallbackAnimationClip assigned but no avatar " +
                        $"(missing {nameof(RigDefinitionAuthoring)} or its Avatar is unassigned). The fallback animation was dropped.",
                        authoring);
                }

                builder.ApplyTo(ref commands);

                var defaultFallback = builder.BuildFallbackBlend();
                var blobBuilder = new BlobBuilder(Allocator.Temp);
                blobBuilder.ConstructRoot<FallbackBlend>() = defaultFallback;
                var fallbackRef = blobBuilder.CreateBlobAssetReference<FallbackBlend>(Allocator.Persistent);
                blobBuilder.Dispose();
                commands.AddBlobAsset(ref fallbackRef, out _);
                commands.AddComponent(new DefaultBlendGroupFallback { Value = fallbackRef });

                // Opt-in inertialization: only provision the per-rig state + per-bone buffer when the designer set a
                // window. Default 0 = OFF = the rig never gets these components = exactly the current behavior.
                // The buffer is left empty here; InertializationSystem sizes it to the runtime rig bone count and
                // seeds the displayed-pose history on the first frame (the exact bone count is not reliably known at
                // bake because the rig blob is built with bone-stripping masks).
                if (authoring.inertializationDuration > 0f)
                {
                    AddComponent(entity, new InertializationState
                    {
                        duration = authoring.inertializationDuration,
                        elapsed = 0f,
                        active = 0,
                        lastDominant = 0,
                        initialized = 0,
                    });
                    AddBuffer<InertializationBoneState>(entity);
                }
            }

            private (Hash128 hash, BlobAssetReference<AnimationClipBlob> blob) BakeFallbackAnimation(
                TimelineAnimationStateAuthoring authoring, Avatar avatar, Entity entity)
            {
                var fallbackHash =
                    BakingUtils.ComputeAnimationHash(authoring.fallbackAnimationClip, avatar, authoring.applyFootIK);
                var animationBaker = new AnimationClipBaker();
                singleClipBuffer[0] = authoring.fallbackAnimationClip;
                var bakedAnimations =
                    animationBaker.BakeAnimations(this, singleClipBuffer, avatar, authoring.gameObject,
                        authoring.applyFootIK);
                singleClipBuffer[0] = null;

                BlobAssetReference<AnimationClipBlob> fallbackBlob = default;

                if (bakedAnimations is { IsCreated: true, Length: > 0 } &&
                    bakedAnimations[0] != BlobAssetReference<AnimationClipBlob>.Null)
                    fallbackBlob = bakedAnimations[0];

                if (bakedAnimations.IsCreated) bakedAnimations.Dispose();
                return (fallbackHash, fallbackBlob);
            }
        }
    }
}