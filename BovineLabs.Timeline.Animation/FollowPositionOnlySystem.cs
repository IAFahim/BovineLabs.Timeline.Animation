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
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var job = new FollowPositionJob
            {
                TargetL2WLookup = SystemAPI.GetComponentLookup<LocalToWorld>(true)
            };

            job.ScheduleParallel();
        }

        [BurstCompile]
        public partial struct FollowPositionJob : IJobEntity
        {
            [ReadOnly] public ComponentLookup<LocalToWorld> TargetL2WLookup;

            public void Execute(Entity entity, in FollowPositionOnly follow, ref LocalTransform lt)
            {
                if (TargetL2WLookup.TryGetComponent(follow.TargetBone, out var targetL2W))
                    // Only update LocalTransform.Position; TransformSystemGroup derives LocalToWorld from it.
                    // Writing LocalToWorld directly would race with TransformSystemGroup.
                    lt.Position = targetL2W.Position;
            }
        }
    }
}