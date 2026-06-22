using BovineLabs.Timeline.Data;
using Rukhanka;
using Unity.Collections;
using Unity.Entities;
using Unity.Transforms;

namespace BovineLabs.Timeline.Animation
{
    [UpdateInGroup(typeof(TimelineComponentAnimationGroup))]
    [UpdateAfter(typeof(TimelineAnimationUnificationSystem))]
    [UpdateBefore(typeof(AnimationProcessSystem))]
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

        // Cleanup tag stamped onto every spawned ghost recording the clip entity that owns it.
        // ICleanupComponentData survives the ghost's own destruction, but its purpose here is to let
        // ReconcileOrphanedGhosts find and reclaim ghosts whose owning clip was destroyed while the clip
        // was still ClipActive (timeline/SubScene teardown, director destroyed, archetype change), a path
        // ResetInactiveClips can never observe because the clip entity no longer exists.
        private struct AfterImageGhostOwner : ICleanupComponentData
        {
            public Entity ClipEntity;
        }

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<AfterImageClipData>();
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
                if (clipData.ValueRO.SpawnedEntity != Entity.Null) continue;

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
                    UnityEngine.Debug.LogWarning(
                        "[AfterImage] Ghost prefab is missing an AnimationToProcessComponent buffer; the captured " +
                        "pose was dropped and the ghost will render in bind pose. Give the ghost prefab a rig setup.");
                }
#endif

                ecb.AddComponent(instance, new AfterImageGhostOwner { ClipEntity = req.ClipEntity });

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
                    // Remove the cleanup tag first so DestroyEntity fully reclaims the ghost rather than
                    // leaving a husk (ICleanupComponentData keeps an entity alive until its cleanup
                    // components are stripped).
                    ecb.RemoveComponent<AfterImageGhostOwner>(spawnedEntity);
                    ecb.DestroyEntity(spawnedEntity);
                }

                ecb.SetComponent(entity, new AfterImageClipData { SpawnedEntity = Entity.Null });
            }
        }

        // Reclaim ghosts whose owning clip entity was destroyed while still ClipActive (timeline/SubScene
        // teardown, director destroyed, archetype change). ResetInactiveClips cannot see those clips - they
        // no longer exist - so without this pass each such teardown would leak one orphan ghost forever.
        private void ReconcileOrphanedGhosts(ref SystemState state, EntityCommandBuffer ecb)
        {
            foreach (var (owner, entity) in
                     SystemAPI.Query<RefRO<AfterImageGhostOwner>>()
                         .WithEntityAccess())
            {
                var clipEntity = owner.ValueRO.ClipEntity;

                // The owner clip is still alive and still points at this ghost: leave it to the normal
                // active/inactive lifecycle (CollectAndSpawn / ResetInactiveClips).
                if (state.EntityManager.Exists(clipEntity) &&
                    SystemAPI.HasComponent<AfterImageClipData>(clipEntity) &&
                    SystemAPI.GetComponent<AfterImageClipData>(clipEntity).SpawnedEntity == entity)
                {
                    continue;
                }

                // Orphaned: owner clip gone (or no longer references this ghost). Reclaim it.
                ecb.RemoveComponent<AfterImageGhostOwner>(entity);
                ecb.DestroyEntity(entity);
            }
        }
    }
}