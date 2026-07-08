using BovineLabs.Core.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace BovineLabs.Timeline.Animation
{
    /// <summary> A single designer-authored hold pose: bone (Rukhanka name hash) + local offset. </summary>
    public struct Grip
    {
        /// <summary> Hash of the grip name (Rukhanka CalculateHash32), the key clips reference. </summary>
        public uint Key;

        /// <summary> Rukhanka bone name hash the weapon anchors to. </summary>
        public uint BoneHash;

        public float3 Position;
        public quaternion Rotation;
    }

    /// <summary> All grips for one weapon. </summary>
    public struct WeaponGrips
    {
        public BlobArray<Grip> Grips;

        /// <summary> Index into <see cref="Grips" /> used when a clip references a missing key. </summary>
        public int DefaultGrip;
    }

#if !BL_DISABLE_OBJECT_DEFINITION
    /// <summary> Registry of every weapon's grips, keyed by its ObjectDefinition id. </summary>
    public struct WeaponGripRegistryBlob
    {
        public BlobHashMap<BovineLabs.Nerve.ObjectManagement.ObjectId, WeaponGrips> Weapons;
    }

    /// <summary> Singleton wrapping <see cref="WeaponGripRegistryBlob" />, baked by WeaponGripSettings. </summary>
    public struct WeaponGripRegistry : IComponentData
    {
        public BlobAssetReference<WeaponGripRegistryBlob> Value;
    }
#endif

    /// <summary> Baked onto a WeaponGripClip entity. Resolved into <see cref="WeaponAnchorData" /> every frame by WeaponGripSampleSystem. </summary>
    public struct WeaponGripClipData : IComponentData
    {
        /// <summary> Grip key (name hash). 0 or unknown falls back to the weapon's default grip. </summary>
        public uint Grip;
    }

    /// <summary>
    /// Persistent attachment state (Phase 2 lifecycle). Phase 1 only reads <see cref="Holder" /> when present and
    /// enabled to resolve which rig owns the grip bone; otherwise the timeline owner is used.
    /// </summary>
    public struct WeaponAttachment : IComponentData, IEnableableComponent
    {
        /// <summary> Character (rig root) entity holding the weapon. </summary>
        public Entity Holder;

        /// <summary> Current grip key. </summary>
        public uint Grip;
    }
}
