#if !BL_DISABLE_OBJECT_DEFINITION
using BovineLabs.Core.ObjectManagement;
using BovineLabs.Timeline.Data;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Physics;
using Unity.Transforms;

namespace BovineLabs.Timeline.Animation
{
    /// <summary>
    /// Consumes <see cref="WeaponStateClipData" /> activation edges: Equip (spawn via ObjectDefinition + attach),
    /// ReAttach (retarget grip), Drop (physics handoff with the blended pose velocity) and Pickup (attach the bound
    /// ground weapon, easing in from its world pose). Edges are latched via <see cref="WeaponStateFired" /> and
    /// re-armed when the clip deactivates. Structural changes go through an ECB; no per-frame structural work.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [BurstCompile]
    [WorldSystemFilter(WorldSystemFilterFlags.LocalSimulation | WorldSystemFilterFlags.ClientSimulation |
                       WorldSystemFilterFlags.ServerSimulation)]
    public partial struct WeaponLifecycleSystem : ISystem
    {
        private EntityQuery _pendingQuery;
        private EntityQuery _rearmQuery;

        // Not [BurstCompile]: RequireAnyForUpdate(a, b) allocates a managed EntityQuery[]
        // (params), which Burst rejects (BC1028). OnCreate runs once — no Burst benefit.
        public void OnCreate(ref SystemState state)
        {
            _pendingQuery = SystemAPI.QueryBuilder()
                .WithAll<ClipActive, TimelineActive, WeaponStateClipData, TrackBinding, DirectorRoot>()
                .WithDisabled<WeaponStateFired>()
                .Build();

            _rearmQuery = SystemAPI.QueryBuilder()
                .WithAll<WeaponStateClipData, WeaponStateFired>()
                .WithNone<ClipActive>()
                .Build();

            state.RequireAnyForUpdate(_pendingQuery, _rearmQuery);
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            // Re-arm finished clips so looping timelines fire their edge again next pass.
            new RearmJob().Run(_rearmQuery);

            if (_pendingQuery.IsEmpty)
                return;

            var attachmentLookup = SystemAPI.GetComponentLookup<WeaponAttachment>(true);
            var anchorLookup = SystemAPI.GetComponentLookup<WeaponAttachmentAnchor>(true);
            var poseVelocityLookup = SystemAPI.GetComponentLookup<WeaponPoseVelocity>(true);
            var physicsVelocityLookup = SystemAPI.GetComponentLookup<PhysicsVelocity>(true);
            var simulateLookup = SystemAPI.GetComponentLookup<Simulate>(true);
            var restLookup = SystemAPI.GetComponentLookup<WeaponAnchorRest>(true);
            var sampleLookup = SystemAPI.GetBufferLookup<WeaponAnchorSample>(true);
            var hasRegistry = SystemAPI.TryGetSingleton(out ObjectDefinitionRegistry registry);

            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (clip, binding, root, fired) in SystemAPI
                         .Query<RefRO<WeaponStateClipData>, RefRO<TrackBinding>, RefRO<DirectorRoot>,
                             EnabledRefRW<WeaponStateFired>>()
                         .WithAll<ClipActive, TimelineActive>()
                         .WithDisabled<WeaponStateFired>())
            {
                fired.ValueRW = true;

                var data = clip.ValueRO;
                var weapon = binding.ValueRO.Value;
                var holder = root.ValueRO.Director;

                switch (data.Mode)
                {
                    case WeaponStateMode.Equip:
                    {
                        if (!hasRegistry || !registry.TryGetValue(new ObjectId(data.ObjectId), out var prefab))
                            break;

                        // Spawn attached at the authored grip — the weapon appears at the designed pose,
                        // never at an incidental one.
                        var spawned = ecb.Instantiate(prefab);
                        ecb.AddComponent(spawned, new WeaponAttachment { Holder = holder, Grip = data.Grip });
                        ecb.AddComponent<WeaponAttachmentAnchor>(spawned);
                        ecb.AddComponent<WeaponPoseVelocity>(spawned);
                        ecb.AddComponent(spawned, default(WeaponAnchorRest));
                        if (!sampleLookup.HasBuffer(prefab))
                            ecb.AddBuffer<WeaponAnchorSample>(spawned);
                        if (simulateLookup.HasComponent(prefab))
                            ecb.SetComponentEnabled<Simulate>(spawned, false);
                        break;
                    }

                    case WeaponStateMode.ReAttach:
                    {
                        if (weapon == Entity.Null)
                            break;

                        // Retarget only; the pose change rides the sample crossfade downstream.
                        if (attachmentLookup.TryGetComponent(weapon, out var attachment))
                        {
                            attachment.Grip = data.Grip;
                            ecb.SetComponent(weapon, attachment);
                            ecb.SetComponentEnabled<WeaponAttachment>(weapon, true);
                        }
                        else
                        {
                            ecb.AddComponent(weapon, new WeaponAttachment { Holder = holder, Grip = data.Grip });
                        }

                        EnsureAttachRuntime(ref ecb, weapon, anchorLookup, poseVelocityLookup, restLookup, sampleLookup, false);
                        break;
                    }

                    case WeaponStateMode.Drop:
                    {
                        if (weapon == Entity.Null || !attachmentLookup.HasComponent(weapon))
                            break;

                        ecb.SetComponentEnabled<WeaponAttachment>(weapon, false);

                        // Physics handoff: last two frames of blended pose delta so it flies believably.
                        // (Angular is world-space; close enough for a throw at typical inertia tensors.)
                        if (physicsVelocityLookup.HasComponent(weapon) &&
                            poseVelocityLookup.TryGetComponent(weapon, out var poseVelocity) &&
                            poseVelocity.HasPrev != 0)
                        {
                            ecb.SetComponent(weapon, new PhysicsVelocity
                            {
                                Linear = poseVelocity.Linear,
                                Angular = poseVelocity.Angular
                            });
                        }

                        if (simulateLookup.HasComponent(weapon))
                            ecb.SetComponentEnabled<Simulate>(weapon, true);
                        break;
                    }

                    case WeaponStateMode.Pickup:
                    {
                        if (weapon == Entity.Null)
                            break;

                        if (attachmentLookup.TryGetComponent(weapon, out var attachment))
                        {
                            attachment.Holder = holder;
                            attachment.Grip = data.Grip;
                            ecb.SetComponent(weapon, attachment);
                            ecb.SetComponentEnabled<WeaponAttachment>(weapon, true);
                        }
                        else
                        {
                            ecb.AddComponent(weapon, new WeaponAttachment { Holder = holder, Grip = data.Grip });
                        }

                        // Reset rest so the blend system snapshots the ground pose as blend-from, and ease the
                        // weapon into the grip instead of snapping ("with style").
                        EnsureAttachRuntime(ref ecb, weapon, anchorLookup, poseVelocityLookup, restLookup, sampleLookup, true);
                        ecb.AddComponent(weapon, new WeaponAttachEase { Active = 1 });

                        if (simulateLookup.HasComponent(weapon))
                            ecb.SetComponentEnabled<Simulate>(weapon, false);
                        break;
                    }
                }
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }

        /// <summary> Adds the anchor-pipeline components a lifecycle-managed weapon needs, when missing. </summary>
        private static void EnsureAttachRuntime(
            ref EntityCommandBuffer ecb,
            Entity weapon,
            in ComponentLookup<WeaponAttachmentAnchor> anchorLookup,
            in ComponentLookup<WeaponPoseVelocity> poseVelocityLookup,
            in ComponentLookup<WeaponAnchorRest> restLookup,
            in BufferLookup<WeaponAnchorSample> sampleLookup,
            bool resetRest)
        {
            if (!anchorLookup.HasComponent(weapon))
                ecb.AddComponent<WeaponAttachmentAnchor>(weapon);
            if (!poseVelocityLookup.HasComponent(weapon))
                ecb.AddComponent<WeaponPoseVelocity>(weapon);
            if (!sampleLookup.HasBuffer(weapon))
                ecb.AddBuffer<WeaponAnchorSample>(weapon);
            if (!restLookup.HasComponent(weapon))
                ecb.AddComponent(weapon, default(WeaponAnchorRest));
            else if (resetRest)
                ecb.SetComponent(weapon, default(WeaponAnchorRest));
        }

        /// <summary> Disables the fired latch once the clip window has passed. </summary>
        [BurstCompile]
        [WithAll(typeof(WeaponStateClipData))]
        [WithNone(typeof(ClipActive))]
        private partial struct RearmJob : IJobEntity
        {
            private static void Execute(EnabledRefRW<WeaponStateFired> fired)
            {
                fired.ValueRW = false;
            }
        }
    }
}
#endif
