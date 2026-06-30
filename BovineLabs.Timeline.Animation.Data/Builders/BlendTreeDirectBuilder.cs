using BovineLabs.Core.EntityCommands;
using Unity.Mathematics;

namespace BovineLabs.Timeline.Animation.Data.Builders
{
    public struct BlendTreeDirectBuilder
    {
        public float3 PositionOffset;
        public quaternion RotationOffset;
        public bool RemoveStartOffset;
        public bool ApplyFootIK;

        public void ApplyTo<T>(ref T builder)
            where T : struct, IEntityCommands
        {
            builder.AddComponent(new BlendTreeDirectClipData
            {
                PositionOffset = PositionOffset,
                RotationOffset = RotationOffset,
                RemoveStartOffset = RemoveStartOffset,
                ApplyFootIK = ApplyFootIK
            });
        }
    }
}
