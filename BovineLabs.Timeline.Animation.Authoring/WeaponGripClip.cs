using BovineLabs.Core.Authoring.EntityCommands;
using BovineLabs.Timeline.Animation.Data.Builders;
using BovineLabs.Timeline.Authoring;
using Rukhanka.Toolbox;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Timeline;

namespace BovineLabs.Timeline.Animation.Authoring
{
    /// <summary>
    /// Anchors the bound weapon to a designer-authored grip (see <see cref="WeaponGripPresetObject" />) while
    /// active. Unlike <see cref="WeaponAnchorClip" /> no scene bone reference is needed — the grip resolves
    /// against whichever rig holds the weapon at runtime.
    /// </summary>
    public class WeaponGripClip : DOTSClip, ITimelineClipAsset
    {
        [Tooltip("Grip name authored in the weapon's grip preset. Empty uses the weapon's default grip.")]
        public string grip;

        public override double duration => 1;
        public ClipCaps clipCaps => ClipCaps.Blending | ClipCaps.Looping;

        public override void Bake(Entity clipEntity, BakingContext context)
        {
            var builder = new WeaponGripClipBuilder
            {
                Grip = string.IsNullOrWhiteSpace(this.grip) ? 0u : this.grip.CalculateHash32()
            };

            var commands = new BakerCommands(context.Baker, clipEntity);
            builder.ApplyTo(ref commands);

            base.Bake(clipEntity, context);
        }
    }
}
