using BovineLabs.Core.EntityCommands;

namespace BovineLabs.Timeline.Animation.Data.Builders
{
    public struct WeaponStateClipBuilder
    {
        public WeaponStateMode Mode;

        /// <summary> ObjectDefinition id (Equip only). </summary>
        public int ObjectId;

        /// <summary> Grip key (name hash). 0 = use the weapon's default grip. </summary>
        public uint Grip;

        public void ApplyTo<T>(ref T builder)
            where T : struct, IEntityCommands
        {
            builder.AddComponent(new WeaponStateClipData
            {
                Mode = Mode,
                ObjectId = ObjectId,
                Grip = Grip
            });

            // Edge latch: disabled until the clip activates and WeaponLifecycleSystem fires the edge.
            builder.AddComponent<WeaponStateFired>();
            builder.SetComponentEnabled<WeaponStateFired>(false);
        }
    }
}
