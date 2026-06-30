using Unity.Entities;
using Unity.Mathematics;
using Hash128 = Unity.Entities.Hash128;

namespace BovineLabs.Timeline.Animation
{
    [InternalBufferCapacity(0)]
    public struct BlendTreeDirectMotionData : IBufferElementData
    {
        public Hash128 AnimationHash;

        // Explicit (designer-set) weight for this motion. Index in this buffer matches the index
        // Rukhanka's ComputeBlendTreeDirect returns.
        public float Weight;
        public int MotionIndex;
    }

    // Direct blend trees have no blend parameter; the clip is purely an activation marker carrying
    // the per-clip transform offsets. Per-motion weights live on the track's motion buffer.
    public struct BlendTreeDirectClipData : IComponentData
    {
        public float3 PositionOffset;
        public quaternion RotationOffset;
        public bool RemoveStartOffset;
        public bool ApplyFootIK;
    }

    public struct BlendAnimationTreeDirectTrackData : IComponentData
    {
        public int LayerIndex;
        public bool NormalizeBlendValues;

        public float3 TrackPositionOffset;
        public quaternion TrackRotationOffset;
        public bool ApplyAvatarMask;
        public Hash128 AvatarMaskHash;
    }

    [InternalBufferCapacity(4)]
    public struct BlendTreeDirectPlaybackStateElement : IBufferElementData
    {
        public Entity Track;
        public float AccumulatedTime;
        public float PreviousAbsoluteTime;
        public bool IsInitialized;
    }
}
