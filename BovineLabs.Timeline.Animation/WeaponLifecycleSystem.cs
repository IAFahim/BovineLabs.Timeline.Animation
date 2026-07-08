#if !BL_DISABLE_OBJECT_DEFINITION
using BovineLabs.Nerve.ObjectManagement;
using BovineLabs.Timeline.Data;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Physics;
using Unity.Transforms;

namespace BovineLabs.Timeline.Animation
{
    /// <summary>
    /// Consumes <see cref="WeaponStateClipData" /> activation edges: Equip (spawn via ObjectDefinition + attach, or
    /// re-attach the holder's existing instance of the same weapon), ReAttach (retarget grip), Drop (physics handoff
    /// with the blended pose velocity) and Pickup (attach the bound ground weapon, easing in from its world pose).
    /// Edges are latched via <see cref="WeaponStateFired" /> and re-armed when the clip deactivates. An Equip records
    /// the spawned instance on the holder (<see cref="EquippedWeapon" />) so a later Drop/ReAttach/Pickup with no bound
    /// weapon can address it and so looping timelines reuse one weapon instead of accumulating; a reconcile pass
    /// destroys orphaned instances. Structural changes go through an ECB; no per-frame structural work.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [BurstCompile]
    [WorldSystemFilter(WorldSystemFilterFlags.LocalSimulation | WorldSystemFilterFlags.ClientSimulation |
                       WorldSystemFilterFlags.ServerSimulation)]
    public partial struct WeaponLifecycleSystem : ISystem
    {
        private EntityQuery _pendingQuery;
        private EntityQuery _rearmQuery;
        private EntityQuery _ownerQuery;

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

            _ownerQuery = SystemAPI.QueryBuilder().WithAll<EquippedWeaponOwner>().Build();

            state.RequireAnyForUpdate(_pendingQuery, _rearmQuery, _ownerQuery);
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            // Re-arm finished clips so looping timelines fire their edge again next pass.
            new RearmJob().Run(_rearmQuery);

            var attachmentLookup = SystemAPI.GetComponentLookup<WeaponAttachment>(true);
            var anchorLookup = SystemAPI.GetComponentLookup<WeaponAttachmentAnchor>(true);
            var poseVelocityLookup = SystemAPI.GetComponentLookup<WeaponPoseVelocity>(true);
            var physicsVelocityLookup = SystemAPI.GetComponentLookup<PhysicsVelocity>(true);
            var simulateLookup = SystemAPI.GetComponentLookup<Simulate>(true);
            var restLookup = SystemAPI.GetComponentLookup<WeaponAnchorRest>(true);
            var sampleLookup = SystemAPI.GetBufferLookup<WeaponAnchorSample>(true);
            var equippedLookup = SystemAPI.GetComponentLookup<EquippedWeapon>(true);
            var hasRegistry = SystemAPI.TryGetSingleton(out ObjectDefinitionRegistry registry);

            var ecb = new EntityCommandBuffer(Allocator.Temp);

            // Reconcile orphaned Equip-spawned weapons against the committed state, before this pass's edges run:
            // a weapon whose holder died (its normal EquippedWeapon was auto-stripped) or was replaced no longer
            // matches its back-reference. Mirrors AfterImageSpawnSystem.ReconcileOrphanedGhosts.
            foreach (var (owner, weapon) in SystemAPI.Query<RefRO<EquippedWeaponOwner>>().WithEntityAccess())
            {
                var owningHolder = owner.ValueRO.Holder;
                if (state.EntityManager.Exists(owningHolder) &&
                    equippedLookup.TryGetComponent(owningHolder, out var held) && held.Weapon == weapon)
                    continue;

                ecb.RemoveComponent<EquippedWeaponOwner>(weapon);
                ecb.DestroyEntity(weapon);
            }

            if (!_pendingQuery.IsEmpty)
            {
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

                    // Non-Equip edges fall back to the holder's equipped weapon when the track binds nothing —
                    // so a Drop/ReAttach/Pickup can address a weapon that Equip spawned at runtime.
                    if (data.Mode != WeaponStateMode.Equip && weapon == Entity.Null &&
                        equippedLookup.TryGetComponent(holder, out var equipped))
                        weapon = equipped.Weapon;

                    switch (data.Mode)
                    {
                        case WeaponStateMode.Equip:
                        {
                            if (!hasRegistry || !registry.TryGetValue(new ObjectId(data.ObjectId), out var prefab))
                                break;

                            // Re-attach an existing live instance of the same ObjectId instead of spawning a duplicate
                            // — a looping timeline (or replayed action) no longer accumulates one weapon per activation.
                            if (equippedLookup.TryGetComponent(holder, out var current) &&
                                current.ObjectId == data.ObjectId && state.EntityManager.Exists(current.Weapon))
                            {
                                ReAttach(ref ecb, current.Weapon, holder, data.Grip, attachmentLookup, anchorLookup,
                                    poseVelocityLookup, restLookup, sampleLookup);
                                break;
                            }

                            // Spawn attached at the authored grip — the weapon appears at the designed pose, never at
                            // an incidental one. The deferred entity is recorded on the holder in the same ECB;
                            // playback remaps it to the real weapon entity.
                            var spawned = ecb.Instantiate(prefab);
                            ecb.AddComponent(spawned, new WeaponAttachment { Holder = holder, Grip = data.Grip });
                            ecb.AddComponent<WeaponAttachmentAnchor>(spawned);
                            ecb.AddComponent<WeaponPoseVelocity>(spawned);
                            ecb.AddComponent(spawned, default(WeaponAnchorRest));
                            if (!sampleLookup.HasBuffer(prefab))
                                ecb.AddBuffer<WeaponAnchorSample>(spawned);
                            if (simulateLookup.HasComponent(prefab))
                                ecb.SetComponentEnabled<Simulate>(spawned, false);

                            ecb.AddComponent(spawned, new EquippedWeaponOwner { Holder = holder });
                            ecb.AddComponent(holder, new EquippedWeapon { Weapon = spawned, ObjectId = data.ObjectId });
                            break;
                        }

                        case WeaponStateMode.ReAttach:
                        {
                            if (weapon == Entity.Null)
                                break;

                            ReAttach(ref ecb, weapon, holder, data.Grip, attachmentLookup, anchorLookup,
                                poseVelocityLookup, restLookup, sampleLookup);
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

                            // Sever ownership so the dropped weapon becomes a free physics object the reconcile pass
                            // never garbage-collects; free the holder's slot so it can equip again.
                            ecb.RemoveComponent<EquippedWeaponOwner>(weapon);
                            if (equippedLookup.TryGetComponent(holder, out var heldOnDrop) && heldOnDrop.Weapon == weapon)
                                ecb.RemoveComponent<EquippedWeapon>(holder);
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
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }

        /// <summary> Retargets a live weapon onto the holder's grip (used by ReAttach and by Equip's reuse path). </summary>
        private static void ReAttach(
            ref EntityCommandBuffer ecb,
            Entity weapon,
            Entity holder,
            uint grip,
            in ComponentLookup<WeaponAttachment> attachmentLookup,
            in ComponentLookup<WeaponAttachmentAnchor> anchorLookup,
            in ComponentLookup<WeaponPoseVelocity> poseVelocityLookup,
            in ComponentLookup<WeaponAnchorRest> restLookup,
            in BufferLookup<WeaponAnchorSample> sampleLookup)
        {
            // Retarget only; the pose change rides the sample crossfade downstream.
            if (attachmentLookup.TryGetComponent(weapon, out var attachment))
            {
                attachment.Grip = grip;
                ecb.SetComponent(weapon, attachment);
                ecb.SetComponentEnabled<WeaponAttachment>(weapon, true);
            }
            else
            {
                ecb.AddComponent(weapon, new WeaponAttachment { Holder = holder, Grip = grip });
            }

            EnsureAttachRuntime(ref ecb, weapon, anchorLookup, poseVelocityLookup, restLookup, sampleLookup, false);
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
