using BovineLabs.Core.Authoring.EntityCommands;
using BovineLabs.Timeline.Animation.Data.Builders;
using BovineLabs.Timeline.Authoring;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace BovineLabs.Timeline.Animation.Authoring
{
    public sealed class BlendTreeDirectClip : DOTSClip, ITimelineClipAsset
    {
        [Header("Clip Transform Offsets")] public Vector3 positionOffset = Vector3.zero;

        public Vector3 eulerAnglesOffset = Vector3.zero;

        [Space] [Tooltip("Removes the starting offset of the animation so it begins exactly at the track's offset.")]
        public bool removeStartOffset = true;

        public bool applyFootIK = true;

        public ClipCaps clipCaps => ClipCaps.All;

#if UNITY_EDITOR
        // Back-reference stamped by BlendTreeDirectTrack.CreateTrackMixer so the edit-mode preview can reach the track's
        // Motions (a clip's PlayableAsset otherwise has no clean handle on its owning track). Not serialized.
        [System.NonSerialized] internal BlendTreeDirectTrack EditorPreviewTrack;

        public override Playable CreatePlayable(PlayableGraph graph, GameObject owner)
        {
            if (!Application.isPlaying)
            {
                var motions = EditorPreviewTrack?.Motions;

                // No track back-ref or no motions: fall back to the empty mixer (bind pose), exactly as before.
                if (motions == null || motions.Count == 0)
                    return AnimationMixerPlayable.Create(graph);

                // Dominant-motion approximation, NOT the runtime weighted blend: Direct trees carry an explicit static
                // weight per motion, so preview the single highest-weight motion. Trust runtime for the true blend.
                AnimationClip dominant = null;
                var bestWeight = float.NegativeInfinity;

                foreach (var motion in motions)
                {
                    if (motion?.clip == null)
                        continue;

                    if (motion.weight > bestWeight)
                    {
                        bestWeight = motion.weight;
                        dominant = motion.clip;
                    }
                }

                if (dominant == null)
                    return AnimationMixerPlayable.Create(graph);

                var asset = CreateInstance<AnimationPlayableAsset>();
                asset.clip = dominant;
                asset.applyFootIK = applyFootIK;
                asset.removeStartOffset = removeStartOffset;
                asset.position = positionOffset;
                asset.rotation = Quaternion.Euler(eulerAnglesOffset);
                return asset.CreatePlayable(graph, owner);
            }

            return base.CreatePlayable(graph, owner);
        }
#endif

        public override void Bake(Entity clipEntity, BakingContext context)
        {
            var builder = new BlendTreeDirectBuilder
            {
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
