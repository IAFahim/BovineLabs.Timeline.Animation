using Rukhanka;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Physics.Systems;
using Unity.Transforms;

namespace BovineLabs.Timeline.Animation
{
    /// <summary>
    /// The ragdoll bridge. Reacts to each rig's <see cref="ActiveRagdoll"/> enabled edge (toggled by
    /// RagdollTrackSystem) and drives its physics bodies:
    /// <list type="bullet">
    /// <item>ENTER: snap each body onto its bone's current animated world pose, zero its velocity, flip it dynamic
    /// (<see cref="PhysicsMassOverride"/> IsKinematic = 0), remove <see cref="Disabled"/> so it joins the physics
    /// world, and enable the bone's <see cref="OverrideTransformIKComponent"/> so the visual bone follows the body.</item>
    /// <item>EXIT: disable the bone IK, make the body kinematic again, and re-disable it (out of the world).</item>
    /// </list>
    /// While OFF (default) bodies are Disabled and no IK runs, so the feature costs nothing until a RagdollClip
    /// plays. Runs in fixed-step after the physics build so the enter-snap reads the freshest bone poses.
    /// </summary>
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup))]
    [UpdateAfter(typeof(PhysicsSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.LocalSimulation | WorldSystemFilterFlags.ClientSimulation |
                       WorldSystemFilterFlags.ServerSimulation)]
    public partial struct RagdollApplySystem : ISystem
    {
        private ComponentLookup<ActiveRagdoll> _activeRagdoll;
        private ComponentLookup<LocalToWorld> _localToWorld;
        private ComponentLookup<OverrideTransformIKComponent> _overrideIK;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            _activeRagdoll = state.GetComponentLookup<ActiveRagdoll>(true);
            _localToWorld = state.GetComponentLookup<LocalToWorld>(true);
            _overrideIK = state.GetComponentLookup<OverrideTransformIKComponent>();
            state.RequireForUpdate<RagdollBody>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            _activeRagdoll.Update(ref state);
            _localToWorld.Update(ref state);
            _overrideIK.Update(ref state);

            var ecb = SystemAPI.GetSingleton<EndFixedStepSimulationEntityCommandBufferSystem.Singleton>()
                .CreateCommandBuffer(state.WorldUnmanaged)
                .AsParallelWriter();

            state.Dependency = new ApplyJob
            {
                ActiveRagdoll = _activeRagdoll,
                LocalToWorld = _localToWorld,
                OverrideIK = _overrideIK,
                Ecb = ecb,
            }.ScheduleParallel(state.Dependency);
        }

        [BurstCompile]
        [WithOptions(EntityQueryOptions.IncludeDisabledEntities)]
        private partial struct ApplyJob : IJobEntity
        {
            [ReadOnly] public ComponentLookup<ActiveRagdoll> ActiveRagdoll;
            [ReadOnly] public ComponentLookup<LocalToWorld> LocalToWorld;
            [NativeDisableParallelForRestriction] public ComponentLookup<OverrideTransformIKComponent> OverrideIK;
            public EntityCommandBuffer.ParallelWriter Ecb;

            private void Execute(
                [ChunkIndexInQuery] int sortKey,
                Entity entity,
                in RagdollBody body,
                ref RagdollBodyState bodyState,
                ref LocalTransform transform,
                ref PhysicsMassOverride mass,
                ref PhysicsVelocity velocity)
            {
                var isActive = ActiveRagdoll.HasComponent(body.RigRoot) &&
                               ActiveRagdoll.IsComponentEnabled(body.RigRoot);

                if (isActive && !bodyState.Fired)
                {
                    // ENTER — snap onto the bone's current animated world pose, then hand it to physics.
                    if (LocalToWorld.TryGetComponent(body.Bone, out var boneLtw))
                    {
                        transform.Position = boneLtw.Position;
                        transform.Rotation = boneLtw.Rotation;
                        transform.Scale = 1f;
                    }

                    velocity.Linear = float3.zero;
                    velocity.Angular = float3.zero;

                    mass.IsKinematic = 0;
                    Ecb.RemoveComponent<Disabled>(sortKey, entity);
                    SetIkEnabled(body.Bone, true);

                    bodyState.Fired = true;
                }
                else if (!isActive && bodyState.Fired)
                {
                    // EXIT — return to animation: bone stops following, body goes inert and out of the world.
                    SetIkEnabled(body.Bone, false);
                    mass.IsKinematic = 1;
                    Ecb.AddComponent<Disabled>(sortKey, entity);

                    bodyState.Fired = false;
                }
            }

            private void SetIkEnabled(Entity bone, bool value)
            {
                if (bone != Entity.Null && OverrideIK.HasComponent(bone))
                {
                    OverrideIK.SetComponentEnabled(bone, value);
                }
            }
        }
    }
}
