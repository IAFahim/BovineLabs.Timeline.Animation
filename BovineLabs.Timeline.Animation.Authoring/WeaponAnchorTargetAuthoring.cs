using Unity.Entities;
using UnityEngine;

namespace BovineLabs.Timeline.Animation.Authoring
{
    public class WeaponAnchorTargetAuthoring : MonoBehaviour
    {
        private class WeaponAnchorTargetBaker : Baker<WeaponAnchorTargetAuthoring>
        {
            public override void Bake(WeaponAnchorTargetAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.Dynamic);
                AddBuffer<WeaponAnchorSample>(entity);
                AddComponent(entity, new WeaponAnchorRest { Rotation = Unity.Mathematics.quaternion.identity });
            }
        }
    }
}