using Unity.Entities;
using Unity.Mathematics;

namespace BovineLabs.Timeline.Animation
{
    /// <summary>
    /// The ragdoll switch. Lives on the rig root (the Animator/RigDefinitionAuthoring entity). Baked DISABLED.
    /// RagdollTrackSystem enables it while a RagdollClip is active on the bound rig; RagdollApplySystem reacts to
    /// its enabled edge to flip the physics bodies dynamic and let the bones follow them.
    /// </summary>
    public struct ActiveRagdoll : IComponentData, IEnableableComponent
    {
    }

    /// <summary>
    /// On each ragdoll physics body entity. Links the body back to its rig root (whose <see cref="ActiveRagdoll"/>
    /// is the switch) and to the rig bone it represents (whose <c>OverrideTransformIKComponent</c> makes the visual
    /// bone follow this body when ragdolling).
    /// </summary>
    public struct RagdollBody : IComponentData
    {
        public Entity RigRoot;
        public Entity Bone;

        // The body's authored pose expressed in the BONE's local frame (captured at bake). On the ragdoll enter
        // edge the body is snapped to bone.LocalToWorld * this offset — NOT the raw bone pose — so the capsule
        // keeps the exact orientation its joint pivots were baked in. Snapping to the raw bone rotation instead
        // displaces every joint pivot and the solver explodes the ragdoll.
        public float3 BoneLocalPos;
        public quaternion BoneLocalRot;
    }

    /// <summary>
    /// Per-body edge bookkeeping for RagdollApplySystem. <see cref="Fired"/> tracks whether the body is currently
    /// in the ragdolling (dynamic) state so enter/exit transitions fire once.
    /// </summary>
    public struct RagdollBodyState : IComponentData
    {
        public bool Fired;
    }

    /// <summary>
    /// On the clip entity of a RagdollClip. Lets RagdollTrackSystem find active ragdoll clips. <see cref="Latch"/>
    /// (from the clip's stayRagdolled) leaves the ragdoll on after the clip ends instead of restoring animation.
    /// </summary>
    public struct RagdollClipTag : IComponentData
    {
        public bool Latch;
    }
}
