using BovineLabs.Timeline.Data;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Properties;
using Hash128 = Unity.Entities.Hash128;

namespace BovineLabs.Timeline.Animation
{
    [InternalBufferCapacity(0)]
    public struct BlendTree1DMotionData : IBufferElementData
    {
        public Hash128 AnimationHash;

        // Scalar threshold for this motion. Buffer is baked sorted ascending so the index in this
        // buffer matches the index Rukhanka's ComputeBlendTree1D returns.
        public float Threshold;
        public int MotionIndex;
    }

    public struct BlendTree1DParameterClipData : IAnimatedComponent<float>
    {
        public BlendDirectionReadKind ReadKind;
        public ushort ReadLinkKey;
        [CreateProperty] public float Value { get; set; }

        public float MaxSpeed;

        public float3 PositionOffset;
        public quaternion RotationOffset;
        public bool RemoveStartOffset;
        public bool ApplyFootIK;
    }

    public struct BlendAnimationTree1DTrackData : IComponentData
    {
        public int LayerIndex;

        public float3 TrackPositionOffset;
        public quaternion TrackRotationOffset;
        public bool ApplyAvatarMask;
        public Hash128 AvatarMaskHash;
    }

    [InternalBufferCapacity(4)]
    public struct BlendTree1DPlaybackStateElement : IBufferElementData
    {
        public Entity Track;
        public float AccumulatedTime;
        public float PreviousAbsoluteTime;
        public bool IsInitialized;
    }
}
