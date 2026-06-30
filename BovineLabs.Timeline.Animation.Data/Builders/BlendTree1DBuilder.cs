using BovineLabs.Core.EntityCommands;
using Unity.Mathematics;

namespace BovineLabs.Timeline.Animation.Data.Builders
{
    public struct BlendTree1DBuilder
    {
        public float BlendParameter;
        public BlendDirectionReadKind ReadKind;
        public ushort ReadLinkKey;
        public float MaxSpeed;
        public float3 PositionOffset;
        public quaternion RotationOffset;
        public bool RemoveStartOffset;
        public bool ApplyFootIK;

        public void ApplyTo<T>(ref T builder)
            where T : struct, IEntityCommands
        {
            builder.AddComponent(new BlendTree1DParameterClipData
            {
                Value = BlendParameter,
                ReadKind = ReadKind,
                ReadLinkKey = ReadLinkKey,
                MaxSpeed = MaxSpeed,
                PositionOffset = PositionOffset,
                RotationOffset = RotationOffset,
                RemoveStartOffset = RemoveStartOffset,
                ApplyFootIK = ApplyFootIK
            });
        }
    }
}
