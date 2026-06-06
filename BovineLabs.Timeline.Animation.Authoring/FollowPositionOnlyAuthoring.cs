using Unity.Entities;
using UnityEngine;

namespace BovineLabs.Timeline.Animation.Authoring
{
    public class FollowPositionOnlyAuthoring : MonoBehaviour
    {
        public Transform target;

        private void LateUpdate()
        {
            if (target == null) return;
            transform.localPosition =
                transform.parent != null
                    ? transform.parent.InverseTransformPoint(target.position)
                    : target.position;
        }

        private class FollowPositionOnlyAuthoringBaker : Baker<FollowPositionOnlyAuthoring>
        {
            public override void Bake(FollowPositionOnlyAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.WorldSpace);
                AddComponent(entity, new FollowPositionOnly
                {
                    TargetBone = GetEntity(authoring.target, TransformUsageFlags.Dynamic)
                });
            }
        }
    }
}