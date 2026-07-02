using BovineLabs.Core.EntityCommands;
using Unity.Mathematics;

namespace BovineLabs.Timeline.Animation.Data.Builders
{
    public struct BlendTree2DBuilder
    {
        public float2 BlendParameter;
        public BlendDirectionReadKind ReadKind;
        public ushort ReadLinkKey;
        public float MaxSpeed;
        public BovineLabs.Essence.Data.StatKey MaxSpeedStat;
        public ushort MaxSpeedStatLinkKey;
        public float3 PositionOffset;
        public quaternion RotationOffset;
        public bool RemoveStartOffset;
        public bool ApplyFootIK;

        public void ApplyTo<T>(ref T builder)
            where T : struct, IEntityCommands
        {
            builder.AddComponent(new BlendTree2DDirectionClipData
            {
                Value = BlendParameter,
                ReadKind = ReadKind,
                ReadLinkKey = ReadLinkKey,
                MaxSpeed = MaxSpeed,
                MaxSpeedStat = MaxSpeedStat,
                MaxSpeedStatLinkKey = MaxSpeedStatLinkKey,
                PositionOffset = PositionOffset,
                RotationOffset = RotationOffset,
                RemoveStartOffset = RemoveStartOffset,
                ApplyFootIK = ApplyFootIK
            });
        }
    }
}