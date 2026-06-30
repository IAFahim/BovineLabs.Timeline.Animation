using BovineLabs.Core.Authoring.EntityCommands;
using BovineLabs.Timeline.Animation.Data.Builders;
using BovineLabs.Timeline.Authoring;
using BovineLabs.Timeline.EntityLinks.Authoring;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace BovineLabs.Timeline.Animation.Authoring
{
    public sealed class BlendTree1DClip : DOTSClip, ITimelineClipAsset
    {
        public float BlendParameter;
        public BlendDirectionReadKind ReadKind;
        public EntityLinkSchema ReadFrom;

        [Tooltip(
            "Speed (m/s) mapped to blend parameter 1 when ReadKind reads physics velocity. " +
            "The horizontal speed is divided by this, so 0 = idle and 1 = this speed.")]
        [Min(0.001f)]
        public float maxSpeed = 5f;

        [Header("Clip Transform Offsets")] public Vector3 positionOffset = Vector3.zero;

        public Vector3 eulerAnglesOffset = Vector3.zero;

        [Space] [Tooltip("Removes the starting offset of the animation so it begins exactly at the track's offset.")]
        public bool removeStartOffset = true;

        public bool applyFootIK = true;

        public ClipCaps clipCaps => ClipCaps.All;

#if UNITY_EDITOR

        public override Playable CreatePlayable(PlayableGraph graph, GameObject owner)
        {
            // Documented gap: edit-mode scrub shows the bind pose, not a blended pose. The motions
            // (AnimationClips + their thresholds) live on BlendTree1DTrack, not on this clip, and
            // Timeline gives a clip's PlayableAsset no clean back-reference to its parent track here.
            if (!Application.isPlaying)
                return AnimationMixerPlayable.Create(graph);

            return base.CreatePlayable(graph, owner);
        }
#endif

        public override void Bake(Entity clipEntity, BakingContext context)
        {
            ushort readLinkKey = 0;
            if (ReadKind != BlendDirectionReadKind.ClipValue)
            {
                if (ReadFrom == null)
                {
                    Debug.LogError($"{nameof(BlendTree1DClip)} '{name}' needs {nameof(ReadFrom)}.");
                    base.Bake(clipEntity, context);
                    return;
                }

                if (!EntityLinkAuthoringUtility.TryGetKey(ReadFrom, out var key))
                {
                    Debug.LogError(
                        $"{nameof(BlendTree1DClip)} '{name}' could not resolve key for '{ReadFrom.name}'.");
                    base.Bake(clipEntity, context);
                    return;
                }

                readLinkKey = key;
            }

            var builder = new BlendTree1DBuilder
            {
                BlendParameter = BlendParameter,
                ReadKind = ReadKind,
                ReadLinkKey = readLinkKey,
                MaxSpeed = maxSpeed,
                PositionOffset = positionOffset,
                RotationOffset = Quaternion.Euler(eulerAnglesOffset),
                RemoveStartOffset = removeStartOffset,
                ApplyFootIK = applyFootIK
            };
            var commands = new BakerCommands(context.Baker, clipEntity);
            builder.ApplyTo(ref commands);

            base.Bake(clipEntity, context);
        }
    }
}
