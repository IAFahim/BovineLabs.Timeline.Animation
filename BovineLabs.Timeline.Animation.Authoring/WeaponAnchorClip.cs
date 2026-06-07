using BovineLabs.Timeline.Authoring;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Timeline;

namespace BovineLabs.Timeline.Animation.Authoring
{
    public class WeaponAnchorClip : DOTSClip, ITimelineClipAsset
    {
        public ExposedReference<Transform> bone;
        public Vector3 localPosition = Vector3.zero;
        public Vector3 localRotationEuler = Vector3.zero;

        public override double duration => 1;
        public ClipCaps clipCaps => ClipCaps.Blending | ClipCaps.Looping;

        public override void Bake(Entity clipEntity, BakingContext context)
        {
            var resolved = bone.Resolve(context.Director);
            var boneEntity = resolved != null
                ? context.Baker.GetEntity(resolved, TransformUsageFlags.Dynamic)
                : Entity.Null;

            context.Baker.AddComponent(clipEntity, new WeaponAnchorData
            {
                Bone = boneEntity,
                LocalPosition = localPosition,
                LocalRotation = quaternion.Euler(math.radians(localRotationEuler))
            });

            base.Bake(clipEntity, context);
        }
    }
}
