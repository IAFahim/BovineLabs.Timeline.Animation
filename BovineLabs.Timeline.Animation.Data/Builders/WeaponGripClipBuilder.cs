using BovineLabs.Core.EntityCommands;
using Unity.Entities;
using Unity.Mathematics;

namespace BovineLabs.Timeline.Animation.Data.Builders
{
    public struct WeaponGripClipBuilder
    {
        /// <summary> Grip key (name hash). 0 = use the weapon's default grip. </summary>
        public uint Grip;

        public void ApplyTo<T>(ref T builder)
            where T : struct, IEntityCommands
        {
            builder.AddComponent(new WeaponGripClipData { Grip = Grip });

            // The legacy anchor pipeline consumes WeaponAnchorData; WeaponGripSampleSystem writes the resolved
            // bone/offsets into this component each frame. Bone == Entity.Null means "no contribution".
            builder.AddComponent(new WeaponAnchorData
            {
                Bone = Entity.Null,
                LocalPosition = float3.zero,
                LocalRotation = quaternion.identity
            });
        }
    }
}
