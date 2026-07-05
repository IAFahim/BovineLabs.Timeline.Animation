using Rukhanka;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;

namespace BovineLabs.Timeline.Animation.Authoring
{
    /// <summary>
    /// Baking post-pass. The Ragdoll generator puts an <c>OverrideTransformIKAuthoring</c> on each ragdoll bone,
    /// which bakes an <see cref="OverrideTransformIKComponent"/> ENABLED. But the bone must NOT follow its physics
    /// body until the ragdoll turns on, so this system (running after all bakers) flips that enableable OFF on
    /// every ragdoll bone. RagdollApplySystem re-enables it on the ragdoll enter edge.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.BakingSystem)]
    public partial struct RagdollBakingSystem : ISystem
    {
        private EntityQuery _bodyQuery;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            _bodyQuery = SystemAPI.QueryBuilder()
                .WithAll<RagdollBody>()
                .WithOptions(EntityQueryOptions.IncludeDisabledEntities)
                .Build();
            state.RequireForUpdate(_bodyQuery);
        }

        public void OnUpdate(ref SystemState state)
        {
            var bodies = _bodyQuery.ToComponentDataArray<RagdollBody>(Allocator.Temp);
            for (var i = 0; i < bodies.Length; i++)
            {
                var bone = bodies[i].Bone;
                if (bone != Entity.Null && state.EntityManager.HasComponent<OverrideTransformIKComponent>(bone))
                {
                    state.EntityManager.SetComponentEnabled<OverrideTransformIKComponent>(bone, false);
                }
            }

            bodies.Dispose();
        }
    }
}
