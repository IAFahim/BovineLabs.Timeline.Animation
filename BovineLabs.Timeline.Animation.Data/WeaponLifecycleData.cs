using Unity.Entities;
using Unity.Mathematics;

namespace BovineLabs.Timeline.Animation
{
    /// <summary> Lifecycle edge fired by a <see cref="WeaponStateClipData" /> clip when it activates. </summary>
    public enum WeaponStateMode : byte
    {
        /// <summary> Spawn the weapon via ObjectDefinition and attach it at the given grip. </summary>
        Equip,

        /// <summary> Retarget the attachment grip (hand → back holster); pose rides the sample crossfade. </summary>
        ReAttach,

        /// <summary> Detach and hand off to physics with the blended pose velocity. </summary>
        Drop,

        /// <summary> Attach the bound ground weapon, blending from its world pose toward the grip. </summary>
        Pickup
    }

    /// <summary> Baked onto a WeaponStateClip entity; consumed on the activation edge by WeaponLifecycleSystem. </summary>
    public struct WeaponStateClipData : IComponentData
    {
        public WeaponStateMode Mode;

        /// <summary> ObjectDefinition id (Equip only): key into ObjectDefinitionRegistry and WeaponGripRegistry. </summary>
        public int ObjectId;

        /// <summary> Grip key (name hash) for Equip/ReAttach/Pickup. 0 = the weapon's default grip. </summary>
        public uint Grip;
    }

    /// <summary>
    /// Edge latch for <see cref="WeaponStateClipData" />: baked disabled, enabled once the edge fires and re-armed
    /// (disabled) when the clip deactivates so looping timelines fire again. No structural churn per frame.
    /// </summary>
    public struct WeaponStateFired : IComponentData, IEnableableComponent
    {
    }

    /// <summary>
    /// Persistent-attachment anchor, resolved every frame by WeaponGripSampleSystem for weapons with an enabled
    /// <see cref="WeaponAttachment" />: grip key → bone entity + local offsets. While no grip clip is sampling the
    /// weapon, WeaponAnchorBlendSystem consumes this as one full-weight sample — the equip cutscene ends and the
    /// weapon stays in the hand. Bone == Entity.Null means unresolved (no contribution).
    /// </summary>
    public struct WeaponAttachmentAnchor : IComponentData
    {
        public Entity Bone;
        public float3 LocalPosition;
        public quaternion LocalRotation;
    }

    /// <summary>
    /// World-space velocity of the blended anchor pose, tracked by WeaponAnchorBlendSystem from the last two frames.
    /// Drop copies it into PhysicsVelocity so a released weapon flies believably. The tracked value is an exponential
    /// moving average of the per-frame finite difference (seeded exact on the first sample) so a single hitch frame
    /// cannot produce an absurd drop velocity.
    /// </summary>
    public struct WeaponPoseVelocity : IComponentData
    {
        public float3 Linear;
        public float3 Angular;

        public float3 PrevPosition;
        public quaternion PrevRotation;
        public byte HasPrev;

        /// <summary> 0 until the first velocity sample seeds the EMA exactly; 1 thereafter (smoothing engaged). </summary>
        public byte HasVelocity;
    }

    /// <summary>
    /// Recorded on a weapon holder (the timeline DirectorRoot director) by <see cref="WeaponStateMode.Equip" />:
    /// which weapon instance it currently has equipped and the ObjectDefinition id it was spawned from. Equip re-attaches
    /// this instance instead of spawning a duplicate when the ids match (no accumulation on looping timelines);
    /// Drop/ReAttach/Pickup fall back to it when the track has no bound weapon.
    /// </summary>
    public struct EquippedWeapon : IComponentData
    {
        public Entity Weapon;
        public int ObjectId;
    }

    /// <summary>
    /// Cleanup back-reference on an Equip-spawned weapon pointing at its holder — mirrors AfterImageGhostOwner. The
    /// reconcile pass in WeaponLifecycleSystem destroys the weapon when the holder dies (the holder's normal
    /// <see cref="EquippedWeapon" /> is auto-stripped, so the back-reference no longer matches) or when the holder
    /// re-equips a different weapon. Drop severs this link so a dropped weapon becomes a free physics object.
    /// </summary>
    public struct EquippedWeaponOwner : ICleanupComponentData
    {
        public Entity Holder;
    }

    /// <summary>
    /// Pickup ease: while active, WeaponAnchorBlendSystem relaxes the weapon from its current (ground) pose toward
    /// the anchor target instead of snapping — the detach relax math run in the attach direction. Cleared on arrival.
    /// </summary>
    public struct WeaponAttachEase : IComponentData
    {
        public byte Active;
    }
}
