using BovineLabs.Core.EntityCommands;
using Unity.Mathematics;

namespace BovineLabs.Timeline.Animation.Data.Builders
{
    public struct BlendTree2DBuilder
    {
        public float2 BlendParameter;
        public BlendDirectionReadKind ReadKind;
        public ushort ReadLinkKey;
        public float ClipIn;
        public float TimeScale;
        public float MaxSpeed;
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
                ClipIn = ClipIn,
                TimeScale = TimeScale,
                MaxSpeed = MaxSpeed,
                PositionOffset = PositionOffset,
                RotationOffset = RotationOffset,
                RemoveStartOffset = RemoveStartOffset,
                ApplyFootIK = ApplyFootIK
            });
        }
    }
}