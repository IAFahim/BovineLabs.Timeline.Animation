using Unity.Entities;
using Unity.Mathematics;

namespace BovineLabs.Timeline.Animation
{
    public struct WeaponAnchorData : IComponentData
    {
        public Entity Bone;
        public float3 LocalPosition;
        public quaternion LocalRotation;
    }

    [InternalBufferCapacity(4)]
    public struct WeaponAnchorSample : IBufferElementData
    {
        public float3 WorldPosition;
        public quaternion WorldRotation;
        public float Weight;
    }
}
