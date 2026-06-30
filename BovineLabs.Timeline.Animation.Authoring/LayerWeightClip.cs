using BovineLabs.Core.Authoring.EntityCommands;
using BovineLabs.Timeline.Animation.Data.Builders;
using BovineLabs.Timeline.Authoring;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Timeline;

namespace BovineLabs.Timeline.Animation.Authoring
{
    public sealed class LayerWeightClip : DOTSClip, ITimelineClipAsset
    {
        [Tooltip(
            "Upper bound on the layer-weight multiplier. The per-frame multiplier is this clip's timeline ease (its blend in/out) times this value: 1 = ease drives the full 0..1 range, 0.5 = the layer never rises above half weight while this clip is active.")]
        [Range(0f, 1f)]
        public float maxMultiplier = 1f;

        // Blending exposes the ease in/out handles that ARE the layer-weight curve.
        public ClipCaps clipCaps => ClipCaps.Blending | ClipCaps.Extrapolation;

        public override void Bake(Entity clipEntity, BakingContext context)
        {
            var builder = new LayerWeightClipBuilder { MaxMultiplier = maxMultiplier };
            var commands = new BakerCommands(context.Baker, clipEntity);
            builder.ApplyTo(ref commands);

            base.Bake(clipEntity, context);
        }
    }
}
