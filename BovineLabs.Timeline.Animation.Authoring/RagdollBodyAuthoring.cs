using BovineLabs.Timeline.Physics.Authoring;
using Unity.Entities;
using Unity.Physics;
using UnityEngine;

namespace BovineLabs.Timeline.Animation.Authoring
{
    /// <summary>
    /// Placed on each ragdoll physics-body GameObject by the Ragdoll generator. Records which rig root + rig bone
    /// this body maps to (<see cref="RagdollBody"/>) and starts the body inert: disabled (out of the physics
    /// world → zero cost while not ragdolling) and kinematic. RagdollApplySystem flips it dynamic on the ragdoll
    /// enter edge.
    /// </summary>
    [DisallowMultipleComponent]
    public class RagdollBodyAuthoring : MonoBehaviour
    {
        [Tooltip("The rig root (Animator / RigDefinitionAuthoring GameObject) that owns this ragdoll.")]
        public Transform rigRoot;

        [Tooltip("The rig bone this body represents. Its OverrideTransformIK makes the visual bone follow this body.")]
        public Transform bone;

        private class RagdollBodyAuthoringBaker : Baker<RagdollBodyAuthoring>
        {
            public override void Bake(RagdollBodyAuthoring authoring)
            {
                if (authoring.rigRoot == null || authoring.bone == null)
                {
                    return;
                }

                // WorldSpace so the body is an unparented physics root — RagdollApplySystem writes the bone's
                // world pose straight into its LocalTransform on the ragdoll enter edge.
                var e = GetEntity(TransformUsageFlags.Dynamic | TransformUsageFlags.WorldSpace);

                // Capture the body's pose in the bone's local frame so the runtime snap preserves the authored
                // capsule orientation (the frame the joint pivots were baked in).
                var boneRotInv = Quaternion.Inverse(authoring.bone.rotation);
                var localPos = boneRotInv * (authoring.transform.position - authoring.bone.position);
                var localRot = boneRotInv * authoring.transform.rotation;

                AddComponent(e, new RagdollBody
                {
                    RigRoot = GetEntity(authoring.rigRoot, TransformUsageFlags.None),
                    Bone = GetEntity(authoring.bone, TransformUsageFlags.Dynamic),
                    BoneLocalPos = localPos,
                    BoneLocalRot = localRot,
                });
                AddComponent(e, new RagdollBodyState { Fired = false });

                // Start inert: kinematic + out of the physics world. RagdollApplySystem removes Disabled and sets
                // IsKinematic = 0 on the ragdoll enter edge, and reverses it on exit.
                AddComponent(e, new PhysicsMassOverride { IsKinematic = 1, SetVelocityToZero = 1 });
                AddComponent<Disabled>(e);

                // A corpse is pure physics (gravity + joints + collision). Opt OUT of the gameplay force/knockback
                // accumulator — otherwise AutoPhysicsForceAccumulatorBakingSystem provisions PendingForce/
                // ExternalVelocity on it and the character's force writers launch the whole ragdoll across the map.
                AddComponent<PhysicsForceAccumulatorOptOut>(e);
            }
        }
    }
}
