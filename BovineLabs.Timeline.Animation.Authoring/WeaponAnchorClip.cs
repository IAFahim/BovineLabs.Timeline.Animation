using BovineLabs.Timeline.Authoring;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Timeline;

namespace BovineLabs.Timeline.Animation.Authoring
{
    public class WeaponAnchorClip : DOTSClip, ITimelineClipAsset
    {
        [Tooltip("Bone transform the weapon is anchored to while this clip is active.")]
        public ExposedReference<Transform> bone;

        [Tooltip("Local position offset from the bone, in the bone's space.")]
        public Vector3 localPosition = Vector3.zero;

        [Tooltip("Local rotation offset from the bone, in euler degrees.")]
        public Vector3 localRotationEuler = Vector3.zero;

        public override double duration => 1;
        public ClipCaps clipCaps => ClipCaps.Blending | ClipCaps.Looping;

        public override void Bake(Entity clipEntity, BakingContext context)
        {
            var resolved = bone.Resolve(context.Director);
            if (resolved == null)
                Debug.LogError(
                    $"{nameof(WeaponAnchorClip)} '{name}' could not resolve its bone reference; the clip will not anchor anything.",
                    this);

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