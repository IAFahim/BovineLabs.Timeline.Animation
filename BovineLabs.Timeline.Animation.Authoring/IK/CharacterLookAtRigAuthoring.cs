using Unity.Entities;
using UnityEngine;

namespace BovineLabs.Timeline.Animation.Authoring
{
    public class CharacterLookAtRigAuthoring : MonoBehaviour
    {
        [Header("Bones")] public Transform neckBone;

        public Transform headBone;

        [Range(0f, 1f)] public float neckWeight = 0.4f;

        [Range(0f, 1f)] public float headWeight = 0.6f;

        [Header("Aim")] public Vector3 forwardVector = Vector3.forward;

        public float angleLimitMin = -80f;

        public float angleLimitMax = 80f;

        [Header("Target")] public Transform lookAtTarget;

        private class CharacterLookAtRigBaker : Baker<CharacterLookAtRigAuthoring>
        {
            public override void Bake(CharacterLookAtRigAuthoring authoring)
            {
                var animator = authoring.GetComponentInParent<Animator>();
                if (animator == null)
                    return;

                DependsOn(animator);
                DependsOn(authoring.neckBone);
                DependsOn(authoring.headBone);
                DependsOn(authoring.lookAtTarget);

                var animatorEntity = GetEntity(animator, TransformUsageFlags.Dynamic);

                AddComponent(animatorEntity, new CharacterLookAtTarget
                {
                    TargetEntity = GetEntity(authoring.lookAtTarget, TransformUsageFlags.Dynamic),
                    AimIKEntity = GetEntity(authoring.headBone, TransformUsageFlags.Dynamic)
                });
            }
        }
    }
}