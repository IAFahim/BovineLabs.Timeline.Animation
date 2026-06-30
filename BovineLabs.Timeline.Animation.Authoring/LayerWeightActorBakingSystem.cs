using Unity.Burst;
using Unity.Collections;
using Unity.Entities;

namespace BovineLabs.Timeline.Animation.Authoring
{
    /// <summary>
    /// Bake-only link from a LayerWeight track to the actor entity that owns the animation layer-mixing buffers,
    /// so <see cref="LayerWeightActorBakingSystem"/> can provision the override buffer on that actor.
    /// </summary>
    [BakingType]
    public struct LayerWeightActorBakeRef : IComponentData
    {
        public Entity Actor;
    }

    /// <summary>
    /// Provisions the runtime <see cref="LayerWeightOverride"/> buffer on every actor targeted by a LayerWeight
    /// track. Done in a baking system (not the track baker) so multiple LayerWeight tracks targeting the same
    /// actor don't trigger an add-duplicate-component baking error. Actors with no LayerWeight track get no
    /// buffer, so the unification pass sees no override and behaves exactly as before.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.BakingSystem)]
    public partial struct LayerWeightActorBakingSystem : ISystem
    {
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var em = state.EntityManager;
            var ecb = new EntityCommandBuffer(Allocator.Temp);
            var seen = new NativeHashSet<Entity>(16, Allocator.Temp);

            foreach (var bakeRef in SystemAPI.Query<RefRO<LayerWeightActorBakeRef>>()
                         .WithOptions(EntityQueryOptions.IncludeDisabledEntities | EntityQueryOptions.IncludePrefab))
            {
                var actor = bakeRef.ValueRO.Actor;
                if (actor == Entity.Null || !seen.Add(actor))
                    continue;

                if (!em.HasBuffer<LayerWeightOverride>(actor))
                    ecb.AddBuffer<LayerWeightOverride>(actor);
            }

            ecb.Playback(em);
            ecb.Dispose();
            seen.Dispose();
        }
    }
}
