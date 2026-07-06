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
    public sealed class AfterImageClip : DOTSClip, ITimelineClipAsset
    {
        // #35: default 0.5s (vs 1s for WeaponAnchor/Ragdoll clips) — an after-image is a brief motion-blur ghost, so a
        // short default authors closer to the intended look; designers still resize the clip freely.
        public override double duration => 0.5;
        public ClipCaps clipCaps => ClipCaps.None;

#if UNITY_EDITOR
        public override Playable CreatePlayable(PlayableGraph graph, GameObject owner)
        {
            if (!Application.isPlaying)
                return AnimationMixerPlayable.Create(graph);

            return base.CreatePlayable(graph, owner);
        }
#endif

        public override void Bake(Entity clipEntity, BakingContext context)
        {
            var builder = new AfterImageBuilder();
            var commands = new BakerCommands(context.Baker, clipEntity);
            builder.ApplyTo(ref commands);

            base.Bake(clipEntity, context);
        }
    }
}