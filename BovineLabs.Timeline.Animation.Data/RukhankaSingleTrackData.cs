using Rukhanka;
using Unity.Entities;
using Unity.Mathematics;
using Hash128 = Unity.Entities.Hash128;

namespace BovineLabs.Timeline.Animation
{
    public struct RukhankaSingleTrackData : IComponentData
    {
        public int LayerIndex;

        public float3 TrackPositionOffset;
        public quaternion TrackRotationOffset;
        public bool ApplyAvatarMask;
        public Hash128 AvatarMaskHash;
        public AnimationBlendingMode BlendMode;
    }
}