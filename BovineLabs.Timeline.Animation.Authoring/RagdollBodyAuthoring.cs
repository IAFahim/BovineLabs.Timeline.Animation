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

                AddComponent(e, new RagdollBody
                {
                    RigRoot = GetEntity(authoring.rigRoot, TransformUsageFlags.None),
                    Bone = GetEntity(authoring.bone, TransformUsageFlags.Dynamic),
                });
                AddComponent(e, new RagdollBodyState { Fired = false });

                // Start inert: kinematic + out of the physics world. RagdollApplySystem removes Disabled and sets
                // IsKinematic = 0 on the ragdoll enter edge, and reverses it on exit.
                AddComponent(e, new PhysicsMassOverride { IsKinematic = 1, SetVelocityToZero = 1 });
                AddComponent<Disabled>(e);
            }
        }
    }
}
