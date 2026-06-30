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
        private const ClipCaps SupportedClipCaps = ClipCaps.Looping | ClipCaps.Extrapolation | ClipCaps.ClipIn |
                                                   ClipCaps.SpeedMultiplier | ClipCaps.Blending;

        [Tooltip("The animation clip to play when this timeline clip is active.")]
        public AnimationClip animationClipHolder;

        [Header("Clip Transform Offsets")] public Vector3 positionOffset = Vector3.zero;

        public Vector3 eulerAnglesOffset = Vector3.zero;

        [Space] [Tooltip("Removes the starting offset of the animation so it begins exactly at the track's offset.")]
        public bool removeStartOffset = true;

        public bool applyFootIK = true;

        [Header("Additive Reference Pose")]
        [Tooltip(
            "Base pose subtracted from this clip when the TRACK's Blend Mode is Additive (recoil/breathing/lean on top of lower layers). Leave null to keep current behavior: the clip's own import-settings reference pose, or its first frame if it has none. Ignored when the track's Blend Mode is Override.")]
        public AnimationClip additiveReferencePoseClip;

        [Tooltip(
            "Time (in seconds) into Additive Reference Pose Clip to sample the base pose from. Only used when Additive Reference Pose Clip is set and the track's Blend Mode is Additive.")]
        public float additiveReferencePoseTime = 0f;

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

                if (rigDef != null) avatar = rigDef.GetAvatar();

                context.Baker.DependsOn(animationClipHolder);
                context.Baker.DependsOn(avatar);

                var builder = new RukhankaAnimationBuilder
                {
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