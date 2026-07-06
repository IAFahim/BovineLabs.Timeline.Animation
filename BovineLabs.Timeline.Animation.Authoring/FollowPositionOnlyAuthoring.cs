using Unity.Entities;
using UnityEngine;

namespace BovineLabs.Timeline.Animation.Authoring
{
    public class FollowPositionOnlyAuthoring : MonoBehaviour
    {
        public Transform target;

        // #35: EDIT-TIME PREVIEW ONLY. This Mono LateUpdate mirrors the target in the editor scene view so designers
        // can see the follow before entering play mode. At runtime the baked FollowPositionOnly component + the
        // Burst FollowPositionOnlySystem own this behaviour — do not "de-duplicate" the two; they serve different modes.
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