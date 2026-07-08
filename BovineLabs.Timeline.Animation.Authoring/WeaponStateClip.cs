#if !BL_DISABLE_OBJECT_DEFINITION
using BovineLabs.Core.Authoring.EntityCommands;
using BovineLabs.Nerve.Authoring.ObjectManagement;
using BovineLabs.Timeline.Animation.Data.Builders;
using BovineLabs.Timeline.Authoring;
using Rukhanka.Toolbox;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Timeline;

namespace BovineLabs.Timeline.Animation.Authoring
{
    /// <summary>
    /// Lifecycle edge for the bound weapon, fired once when the clip activates: Equip (spawn via ObjectDefinition +
    /// attach), ReAttach (retarget grip; pose rides the sample crossfade), Drop (physics handoff with the blended
    /// pose velocity) or Pickup (attach the bound ground weapon, easing from its world pose into the grip).
    /// </summary>
    public class WeaponStateClip : DOTSClip, ITimelineClipAsset
    {
        [Tooltip("Which lifecycle edge fires when this clip activates.")]
        public WeaponStateMode mode;

        [Tooltip("Equip only: the weapon to spawn. Its id keys both the ObjectDefinition and grip registries.")]
        public ObjectDefinition weapon;

        [Tooltip("Grip name from the weapon's grip preset (Equip/ReAttach/Pickup). Empty uses the default grip.")]
        public string grip;

        public override double duration => 1;

        public ClipCaps clipCaps => ClipCaps.None;

        public override void Bake(Entity clipEntity, BakingContext context)
        {
            var builder = new WeaponStateClipBuilder
            {
                Mode = this.mode,
                ObjectId = this.weapon != null ? this.weapon.ID : 0,
                Grip = string.IsNullOrWhiteSpace(this.grip) ? 0u : this.grip.CalculateHash32()
            };

            var commands = new BakerCommands(context.Baker, clipEntity);
            builder.ApplyTo(ref commands);

            base.Bake(clipEntity, context);
        }
    }
}
#endif
