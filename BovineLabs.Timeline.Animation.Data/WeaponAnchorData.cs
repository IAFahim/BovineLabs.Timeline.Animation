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

    /// <summary>
    /// Snapshot of a weapon's pre-attach <see cref="Unity.Transforms.LocalTransform"/>, captured on the anchor
    /// activation edge. While <see cref="Captured"/> is true and the anchor has deactivated, the weapon relaxes
    /// back toward this rest pose instead of freezing at its last anchored pose.
    /// </summary>
    public struct WeaponAnchorRest : IComponentData
    {
        public float3 Position;
        public quaternion Rotation;
        public bool Captured;
    }

    [InternalBufferCapacity(4)]
    public struct WeaponAnchorSample : IBufferElementData
    {
        public float3 WorldPosition;
        public quaternion WorldRotation;
        public float Weight;
    }
}