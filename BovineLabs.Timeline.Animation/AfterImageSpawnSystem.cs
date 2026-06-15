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
                    var l2w = state.EntityManager.GetComponentData<LocalToWorld>(source);
                    rootTransform = LocalTransform.FromPositionRotation(l2w.Position, l2w.Rotation);
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
                    ecb.DestroyEntity(spawnedEntity);

                ecb.SetComponent(entity, new AfterImageClipData { SpawnedEntity = Entity.Null });
            }
        }
    }
}