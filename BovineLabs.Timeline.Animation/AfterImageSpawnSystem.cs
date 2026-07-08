using BovineLabs.Timeline.Data;
using Rukhanka;
using Unity.Collections;
using Unity.Entities;
using Unity.Transforms;
using UnityEngine;

namespace BovineLabs.Timeline.Animation
{
    [UpdateInGroup(typeof(TimelineComponentAnimationGroup))]
    [UpdateAfter(typeof(TimelineAnimationUnificationSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.LocalSimulation | WorldSystemFilterFlags.ClientSimulation |
                       WorldSystemFilterFlags.ServerSimulation)]
    public partial struct AfterImageSpawnSystem : ISystem
    {
        private struct SpawnRequest
        {
            public Entity ClipEntity;
            public Entity Prefab;
            public LocalTransform RootTransform;
            public int AtpOffset;
            public int AtpCount;
        }

        private struct AfterImageGhostOwner : ICleanupComponentData
        {
            public Entity ClipEntity;
        }

        /// <summary> Present only while the ghost is alive. Because <see cref="AfterImageGhostOwner"/> is a cleanup
        /// component, an externally destroyed ghost lingers as a corpse that still passes
        /// <c>EntityManager.Exists</c>; the absence of this (regular) tag is how a corpse is detected. </summary>
        private struct AfterImageGhostAlive : IComponentData
        {
        }

        public void OnCreate(ref SystemState state)
        {
            // Ghosts (including cleanup-pending corpses) must keep the system updating even after the last clip
            // is torn down, otherwise orphan reconciliation never runs and zombies leak.
            var clips = SystemAPI.QueryBuilder().WithAll<AfterImageClipData>().Build();
            var ghosts = SystemAPI.QueryBuilder().WithAll<AfterImageGhostOwner>().Build();
            state.RequireAnyForUpdate(clips, ghosts);
        }

        public void OnUpdate(ref SystemState state)
        {
            var ecb = SystemAPI.GetSingleton<BeginSimulationEntityCommandBufferSystem.Singleton>()
                .CreateCommandBuffer(state.WorldUnmanaged);

            CollectAndSpawn(ref state, ecb);
            ResetInactiveClips(ref state, ecb);
            ReconcileOrphanedGhosts(ref state, ecb);
        }

        private void CollectAndSpawn(ref SystemState state, EntityCommandBuffer ecb)
        {
            var requests = new NativeList<SpawnRequest>(8, Allocator.Temp);
            var atpPool = new NativeList<AnimationToProcessComponent>(64, Allocator.Temp);

            foreach (var (clipData, binding, clip, entity) in
                     SystemAPI.Query<RefRO<AfterImageClipData>, RefRO<TrackBinding>, RefRO<Clip>>()
                         .WithAll<ClipActive>()
                         .WithEntityAccess())
            {
                var spawned = clipData.ValueRO.SpawnedEntity;
                if (spawned != Entity.Null)
                {
                    // Ghost still alive: nothing to do. Ghost destroyed externally (prefab lifetime, scene unload,
                    // gameplay cleanup): clear the stale pointer so the clip is honest — it may respawn while the clip
                    // is still active, matching the "ghost per activation" intent. NOTE: an externally destroyed ghost
                    // still Exists() as a cleanup-pending corpse (AfterImageGhostOwner is ICleanupComponentData), so
                    // liveness is the alive tag, not existence; the corpse itself is finalized by reconcile below.
                    if (state.EntityManager.Exists(spawned) &&
                        state.EntityManager.HasComponent<AfterImageGhostAlive>(spawned)) continue;
                    ecb.SetComponent(entity, new AfterImageClipData { SpawnedEntity = Entity.Null });
                }

                var trackEntity = clip.ValueRO.Track;
                if (!SystemAPI.HasComponent<AfterImageTrackData>(trackEntity)) continue;

                var trackData = SystemAPI.GetComponent<AfterImageTrackData>(trackEntity);
                if (trackData.Prefab == Entity.Null) continue;

                var source = binding.ValueRO.Value;

                var rootTransform = LocalTransform.Identity;
                if (state.EntityManager.HasComponent<LocalToWorld>(source))
                {
                    var l2W = state.EntityManager.GetComponentData<LocalToWorld>(source);
                    rootTransform = LocalTransform.FromPositionRotation(l2W.Position, l2W.Rotation);
                }

                var atpOffset = atpPool.Length;
                var atpCount = 0;
                if (state.EntityManager.HasBuffer<AnimationToProcessComponent>(source))
                {
                    var srcBuf = state.EntityManager.GetBuffer<AnimationToProcessComponent>(source);
                    atpCount = srcBuf.Length;
                    for (var j = 0; j < srcBuf.Length; j++)
                        atpPool.Add(srcBuf[j]);
                }

                requests.Add(new SpawnRequest
                {
                    ClipEntity = entity,
                    Prefab = trackData.Prefab,
                    RootTransform = rootTransform,
                    AtpOffset = atpOffset,
                    AtpCount = atpCount
                });
            }

            for (var i = 0; i < requests.Length; i++)
            {
                var req = requests[i];
                var instance = ecb.Instantiate(req.Prefab);

                if (state.EntityManager.HasComponent<LocalTransform>(req.Prefab))
                    ecb.SetComponent(instance, req.RootTransform);

                if (req.AtpCount > 0 && state.EntityManager.HasBuffer<AnimationToProcessComponent>(req.Prefab))
                {
                    var dstBuf = ecb.SetBuffer<AnimationToProcessComponent>(instance);
                    for (var j = 0; j < req.AtpCount; j++)
                        dstBuf.Add(atpPool[req.AtpOffset + j]);
                }
#if UNITY_EDITOR
                else if (req.AtpCount > 0)
                {
                    Debug.LogWarning(
                        "[AfterImage] Ghost prefab is missing an AnimationToProcessComponent buffer; the captured " +
                        "pose was dropped and the ghost will render in bind pose. Give the ghost prefab a rig setup.");
                }
#endif

                ecb.AddComponent(instance, new AfterImageGhostOwner { ClipEntity = req.ClipEntity });
                ecb.AddComponent<AfterImageGhostAlive>(instance);

                ecb.SetComponent(req.ClipEntity, new AfterImageClipData
                {
                    SpawnedEntity = instance
                });
            }

            atpPool.Dispose();
            requests.Dispose();
        }

        private void ResetInactiveClips(ref SystemState state, EntityCommandBuffer ecb)
        {
            foreach (var (clipData, entity) in
                     SystemAPI.Query<RefRO<AfterImageClipData>>()
                         .WithNone<ClipActive>()
                         .WithEntityAccess())
            {
                var spawnedEntity = clipData.ValueRO.SpawnedEntity;
                if (spawnedEntity == Entity.Null) continue;

                if (state.EntityManager.Exists(spawnedEntity))
                {
                    // Removing the cleanup component from a corpse finalizes its destruction, so only a live ghost
                    // may also be destroyed (a second DestroyEntity on the finalized corpse would throw at playback).
                    ecb.RemoveComponent<AfterImageGhostOwner>(spawnedEntity);
                    if (state.EntityManager.HasComponent<AfterImageGhostAlive>(spawnedEntity))
                        ecb.DestroyEntity(spawnedEntity);
                }

                ecb.SetComponent(entity, new AfterImageClipData { SpawnedEntity = Entity.Null });
            }
        }

        private void ReconcileOrphanedGhosts(ref SystemState state, EntityCommandBuffer ecb)
        {
            foreach (var (owner, entity) in
                     SystemAPI.Query<RefRO<AfterImageGhostOwner>>()
                         .WithEntityAccess())
            {
                var clipEntity = owner.ValueRO.ClipEntity;

                // A corpse (externally destroyed, cleanup pending) must always be finalized — even when its clip
                // still points at it, because CollectAndSpawn clears/respawns that pointer this same update.
                var isCorpse = !SystemAPI.HasComponent<AfterImageGhostAlive>(entity);

                var isOwned = state.EntityManager.Exists(clipEntity) &&
                              SystemAPI.HasComponent<AfterImageClipData>(clipEntity) &&
                              SystemAPI.GetComponent<AfterImageClipData>(clipEntity).SpawnedEntity == entity;

                if (!isCorpse && isOwned) continue;

                // Removing the cleanup component finalizes a corpse; a live orphan additionally needs the destroy.
                ecb.RemoveComponent<AfterImageGhostOwner>(entity);
                if (!isCorpse)
                    ecb.DestroyEntity(entity);
            }
        }
    }
}