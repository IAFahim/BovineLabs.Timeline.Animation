using BovineLabs.Core.EntityCommands;

namespace BovineLabs.Timeline.Animation.Data.Builders
{
    public struct LayerWeightClipBuilder
    {
        public float MaxMultiplier;

        public void ApplyTo<T>(ref T builder)
            where T : struct, IEntityCommands
        {
            builder.AddComponent(new LayerWeightClipData { MaxMultiplier = MaxMultiplier });
        }
    }
}
