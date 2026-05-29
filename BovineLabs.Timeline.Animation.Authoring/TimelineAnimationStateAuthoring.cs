using BovineLabs.Core.Authoring.EntityCommands;
using BovineLabs.Timeline.Animation.Data.Builders;
using Rukhanka;
using Rukhanka.Hybrid;
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

        [Tooltip("Time in seconds to smoothly transition into a new timeline clip.")] [Min(0.001f)]
        public float blendInDuration = 0.25f;

        [Tooltip("Time in seconds to smoothly transition out of a timeline clip.")] [Min(0.001f)]
        public float blendOutDuration = 0.25f;

        [Header("Fallback Transform Offsets")] public Vector3 positionOffset = Vector3.zero;

        public Vector3 eulerAnglesOffset = Vector3.zero;

        [Tooltip(
            "When enabled, strips the clip's baked root motion start pose so the animation begins at the track/fallback offset position. Only enable when position/rotation offsets are also set.")]
        public bool removeStartOffset;

        public bool applyFootIK = true;

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

                if (authoring.fallbackAnimationClip != null)
                {
                    var (fallbackHash, fallbackBlob) = BakeFallbackAnimation(authoring, avatar, entity);
                    builder = builder.WithFallback(fallbackHash, authoring.blendInDuration, authoring.blendOutDuration,
                            authoring.fallbackPlaybackMode)
                        .WithFallbackBlob(fallbackBlob, fallbackHash);
                }

                builder.ApplyTo(ref commands);
            }

            private (Hash128 hash, BlobAssetReference<AnimationClipBlob> blob) BakeFallbackAnimation(
                TimelineAnimationStateAuthoring authoring, Avatar avatar, Entity entity)
            {
                var fallbackHash = BakingUtils.ComputeAnimationHash(authoring.fallbackAnimationClip, avatar);
                var animationBaker = new AnimationClipBaker();
                singleClipBuffer[0] = authoring.fallbackAnimationClip;
                var bakedAnimations =
                    animationBaker.BakeAnimations(this, singleClipBuffer, avatar, authoring.gameObject);
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