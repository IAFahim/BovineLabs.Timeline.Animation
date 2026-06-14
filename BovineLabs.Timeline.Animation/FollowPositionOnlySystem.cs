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

                if (ParentLookup.TryGetComponent(entity, out var selfParent) &&
                    LocalToWorldLookup.TryGetComponent(selfParent.Value, out var parentL2W))
                    lt.Position = math.transform(math.inverse(parentL2W.Value), targetPos);
                else
                    lt.Position = targetPos;
            }
        }
    }
}