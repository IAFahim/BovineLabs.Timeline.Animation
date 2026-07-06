using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace BovineLabs.Timeline.Animation
{
    [UpdateInGroup(typeof(TransformSystemGroup))]
    [UpdateBefore(typeof(LocalToWorldSystem))]
    [BurstCompile]
    [WorldSystemFilter(WorldSystemFilterFlags.LocalSimulation | WorldSystemFilterFlags.ClientSimulation |
                       WorldSystemFilterFlags.ServerSimulation)]
    public partial struct FollowPositionOnlySystem : ISystem
    {
        private ComponentLookup<LocalTransform> _localTransformLookup;
        private ComponentLookup<LocalToWorld> _localToWorldLookup;
        private ComponentLookup<Parent> _parentLookup;
        private ComponentLookup<PostTransformMatrix> _postTransformMatrixLookup;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            _localTransformLookup = state.GetComponentLookup<LocalTransform>(true);
            _localToWorldLookup = state.GetComponentLookup<LocalToWorld>(true);
            _parentLookup = state.GetComponentLookup<Parent>(true);
            _postTransformMatrixLookup = state.GetComponentLookup<PostTransformMatrix>(true);

            // A24: pure per-frame follow with no persistent state to reconcile on removal (dropping the component just
            // stops the follow) => safe to skip entirely when no FollowPositionOnly exists.
            state.RequireForUpdate<FollowPositionOnly>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            _localTransformLookup.Update(ref state);
            _localToWorldLookup.Update(ref state);
            _parentLookup.Update(ref state);
            _postTransformMatrixLookup.Update(ref state);

            var job = new FollowPositionJob
            {
                LocalTransformLookup = _localTransformLookup,
                LocalToWorldLookup = _localToWorldLookup,
                ParentLookup = _parentLookup,
                PostTransformMatrixLookup = _postTransformMatrixLookup
            };

            job.ScheduleParallel();
        }

        [BurstCompile]
        public partial struct FollowPositionJob : IJobEntity
        {
            [ReadOnly] public ComponentLookup<LocalTransform> LocalTransformLookup;
            [ReadOnly] public ComponentLookup<LocalToWorld> LocalToWorldLookup;
            [ReadOnly] public ComponentLookup<Parent> ParentLookup;
            [ReadOnly] public ComponentLookup<PostTransformMatrix> PostTransformMatrixLookup;

            public void Execute(Entity entity, in FollowPositionOnly follow, ref LocalTransform lt)
            {
                var target = follow.TargetBone;
                float3 targetPos;

                if (BoneWorld.TryComputeWorldMatrix(target, LocalTransformLookup, ParentLookup,
                        PostTransformMatrixLookup, out var targetWorld))
                    targetPos = targetWorld.c3.xyz;
                else if (LocalToWorldLookup.TryGetComponent(target, out var targetL2W))
                    targetPos = targetL2W.Position;
                else
                    return;

                // A4: runs before LocalToWorldSystem — prefer a parent world matrix recomputed from
                // fresh LocalTransform over the one-frame-stale LocalToWorld cache.
                float4x4 parentWorld = default;
                var hasParentWorld = ParentLookup.TryGetComponent(entity, out var selfParent) &&
                                     (BoneWorld.TryComputeWorldMatrix(selfParent.Value, LocalTransformLookup,
                                          ParentLookup, PostTransformMatrixLookup, out parentWorld) ||
                                      TryGetL2W(selfParent.Value, out parentWorld));

                if (hasParentWorld &&
                    TransformConversion.WorldPositionToParentLocal(parentWorld, targetPos, out var localPos))
                {
                    lt.Position = math.all(math.isfinite(localPos)) ? localPos : targetPos;
                }
                else
                {
                    lt.Position = targetPos;
                }
            }

            private bool TryGetL2W(Entity entity, out float4x4 world)
            {
                if (LocalToWorldLookup.TryGetComponent(entity, out var l2w))
                {
                    world = l2w.Value;
                    return true;
                }

                world = default;
                return false;
            }
        }
    }
}
