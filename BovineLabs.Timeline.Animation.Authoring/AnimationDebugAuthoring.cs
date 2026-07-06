using Unity.Entities;
using UnityEngine;

namespace BovineLabs.Timeline.Animation.Authoring
{
    public class AnimationDebugAuthoring : MonoBehaviour
    {
        public class Baker : Baker<AnimationDebugAuthoring>
        {
            public override void Bake(AnimationDebugAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.None);

                // #35: AnimationDebugState is only read by AnimationDebugSystem, which is compiled under
                // `UNITY_EDITOR || BL_DEBUG`. Gate the bake on the identical condition so a release build (neither
                // define) never carries a debug component that no system consumes.
#if UNITY_EDITOR || BL_DEBUG
                AddComponent(entity, new AnimationDebugState());
#endif
            }
        }
    }
}