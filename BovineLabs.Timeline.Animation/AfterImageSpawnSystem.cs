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
            state.CompleteDependency();
            CollectAndSpawn(ref state);
            ResetInactiveClips(ref state);
        }

        private void CollectAndSpawn(ref SystemState state)
        {
            var requests = new NativeList<SpawnRequest>(8, Allocator.Temp);
            var atpPool = new NativeList<AnimationToProcessComponent>(64, Allocator.Temp);

            // ── Phase 1: read everything BEFORE any structural change ──
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

            // ── Phase 2: structural changes — all reads are finished ──
            for (var i = 0; i < requests.Length; i++)
            {
                var req = requests[i];
                var instance = state.EntityManager.Instantiate(req.Prefab);

                if (state.EntityManager.HasComponent<LocalTransform>(instance))
                    state.EntityManager.SetComponentData(instance, req.RootTransform);

                if (req.AtpCount > 0 && state.EntityManager.HasBuffer<AnimationToProcessComponent>(instance))
                {
                    var dstBuf = state.EntityManager.GetBuffer<AnimationToProcessComponent>(instance);
                    dstBuf.Clear();
                    for (var j = 0; j < req.AtpCount; j++)
                        dstBuf.Add(atpPool[req.AtpOffset + j]);
                }

                state.EntityManager.SetComponentData(req.ClipEntity, new AfterImageClipData
                {
                    SpawnedEntity = instance
                });
            }

            atpPool.Dispose();
            requests.Dispose();
        }

        private void ResetInactiveClips(ref SystemState state)
        {
            var resetQuery = SystemAPI.QueryBuilder()
                .WithAll<AfterImageClipData>()
                .WithNone<ClipActive>()
                .Build();

            if (resetQuery.IsEmpty) return;

            var entities = resetQuery.ToEntityArray(Allocator.Temp);
            for (var i = 0; i < entities.Length; i++)
            {
                var data = state.EntityManager.GetComponentData<AfterImageClipData>(entities[i]);
                if (data.SpawnedEntity != Entity.Null)
                {
                    data.SpawnedEntity = Entity.Null;
                    state.EntityManager.SetComponentData(entities[i], data);
                }
            }

            entities.Dispose();
        }
    }
}