using BovineLabs.Core.Authoring.EntityCommands;
using BovineLabs.Timeline.Animation.Data.Builders;
using BovineLabs.Timeline.Authoring;
using Rukhanka.Hybrid;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace BovineLabs.Timeline.Animation.Authoring
{
    public class RukhankaAnimationClip : DOTSClip, ITimelineClipAsset
    {
        private const ClipCaps SupportedClipCaps = ClipCaps.Looping | ClipCaps.Extrapolation | ClipCaps.ClipIn | ClipCaps.SpeedMultiplier | ClipCaps.Blending;

        [Tooltip("The animation clip to play when this timeline clip is active.")]
        public AnimationClip animationClipHolder;

        [Header("Clip Transform Offsets")]
        public Vector3 positionOffset = Vector3.zero;

        public Vector3 eulerAnglesOffset = Vector3.zero;

        [Space]
        [Tooltip("Removes the starting offset of the animation so it begins exactly at the track's offset.")]
        public bool removeStartOffset = true;

        public bool applyFootIK = true;

        public override double duration => animationClipHolder != null ? animationClipHolder.length : base.duration;

        public ClipCaps clipCaps => SupportedClipCaps;

#if UNITY_EDITOR
        public override Playable CreatePlayable(PlayableGraph graph, GameObject owner)
        {
            if (!Application.isPlaying && animationClipHolder != null)
            {
                var asset = CreateInstance<AnimationPlayableAsset>();
                asset.clip = animationClipHolder;
                asset.applyFootIK = applyFootIK;
                asset.removeStartOffset = removeStartOffset;
                return asset.CreatePlayable(graph, owner);
            }

            return base.CreatePlayable(graph, owner);
        }
#endif

        public override void Bake(Entity clipEntity, BakingContext context)
        {
            if (animationClipHolder != null)
            {
                Avatar avatar = null;
                var rigDef = context.Director.ResolveRigDefinition(context.Track);

                if (rigDef != null)
                {
                    avatar = rigDef.GetAvatar();
                }

                // Register the baked source assets so editing the clip or avatar re-triggers baking.
                context.Baker.DependsOn(animationClipHolder);
                context.Baker.DependsOn(avatar);

                var builder = new RukhankaAnimationBuilder
                {
                    // Must match the variant baked by RukhankaAnimationTrack (foot-IK on/off → different blob hash).
                    ClipHash = BakingUtils.ComputeAnimationHash(animationClipHolder, avatar, applyFootIK),
                    PreExtrapolation = context.Clip.preExtrapolationMode,
                    PostExtrapolation = context.Clip.postExtrapolationMode,
                    PositionOffset = positionOffset,
                    RotationOffset = Quaternion.Euler(eulerAnglesOffset),
                    RemoveStartOffset = removeStartOffset,
                    ApplyFootIK = applyFootIK
                };

                var commands = new BakerCommands(context.Baker, clipEntity);
                builder.ApplyTo(ref commands);
            }

            base.Bake(clipEntity, context);
        }
    }
}
