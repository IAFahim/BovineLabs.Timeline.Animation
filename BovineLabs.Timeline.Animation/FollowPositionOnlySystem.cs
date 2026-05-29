using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Transforms;

namespace BovineLabs.Timeline.Animation
{
    [UpdateInGroup(typeof(TransformSystemGroup))]
    [UpdateBefore(typeof(LocalToWorldSystem))]
    [BurstCompile]
    [Unity.Entities.WorldSystemFilter(Unity.Entities.WorldSystemFilterFlags.LocalSimulation | Unity.Entities.WorldSystemFilterFlags.ClientSimulation | Unity.Entities.WorldSystemFilterFlags.ServerSimulation)]
    public partial struct FollowPositionOnlySystem : ISystem
    {
        private ComponentLookup<LocalTransform> _localTransformLookup;
        private ComponentLookup<LocalToWorld> _localToWorldLookup;
        private ComponentLookup<Parent> _parentLookup;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            _localTransformLookup = state.GetComponentLookup<LocalTransform>(true);
            _localToWorldLookup = state.GetComponentLookup<LocalToWorld>(true);
            _parentLookup = state.GetComponentLookup<Parent>(true);
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            _localTransformLookup.Update(ref state);
            _localToWorldLookup.Update(ref state);
            _parentLookup.Update(ref state);

            var job = new FollowPositionJob
            {
                LocalTransformLookup = _localTransformLookup,
                LocalToWorldLookup = _localToWorldLookup,
                ParentLookup = _parentLookup
            };

            job.ScheduleParallel();
        }

        [BurstCompile]
        public partial struct FollowPositionJob : IJobEntity
        {
            [ReadOnly] public ComponentLookup<LocalTransform> LocalTransformLookup;
            [ReadOnly] public ComponentLookup<LocalToWorld> LocalToWorldLookup;
            [ReadOnly] public ComponentLookup<Parent> ParentLookup;

            public void Execute(Entity entity, in FollowPositionOnly follow, ref LocalTransform lt)
            {
                var target = follow.TargetBone;

                // Rule 1.1: Prefer LocalTransform for unparented entities
                if (LocalTransformLookup.TryGetComponent(target, out var targetLt) && !ParentLookup.HasComponent(target))
                {
                    lt.Position = targetLt.Position;
                    return;
                }

                // Fallback to LocalToWorld for parented entities
                if (LocalToWorldLookup.TryGetComponent(target, out var targetL2W))
                    lt.Position = targetL2W.Position;
            }
        }
    }
}
