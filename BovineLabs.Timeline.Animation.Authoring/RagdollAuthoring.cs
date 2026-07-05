using Unity.Entities;
using UnityEngine;

namespace BovineLabs.Timeline.Animation.Authoring
{
    /// <summary>
    /// Placed on the rig root (the Animator / RigDefinitionAuthoring GameObject) by the Ragdoll generator. Bakes
    /// the <see cref="ActiveRagdoll"/> switch, disabled by default, so the rig starts fully animated.
    /// </summary>
    [DisallowMultipleComponent]
    public class RagdollAuthoring : MonoBehaviour
    {
        private class RagdollAuthoringBaker : Baker<RagdollAuthoring>
        {
            public override void Bake(RagdollAuthoring authoring)
            {
                var e = GetEntity(TransformUsageFlags.None);
                AddComponent<ActiveRagdoll>(e);
                SetComponentEnabled<ActiveRagdoll>(e, false);
            }
        }
    }
}
