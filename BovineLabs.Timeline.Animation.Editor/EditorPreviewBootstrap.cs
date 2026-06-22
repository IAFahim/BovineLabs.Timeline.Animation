#if UNITY_EDITOR
using System.Collections.Generic;
using Rukhanka;
using Unity.Entities;

namespace BovineLabs.Timeline.Animation.Editor
{
    [WorldSystemFilter(WorldSystemFilterFlags.Editor)]
    [UpdateInGroup(typeof(TimelineComponentAnimationGroup))]
    [UpdateBefore(typeof(TimelineAnimationUnificationSystem))]
    public partial struct DisableAnimationCullingInEditorSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            foreach (var (cull, entity) in SystemAPI.Query<RefRW<CullAnimationsTag>>().WithEntityAccess())
                SystemAPI.SetComponentEnabled<CullAnimationsTag>(entity, false);
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.Editor)]
    [UpdateInGroup(typeof(TimelineComponentAnimationGroup))]
    [UpdateAfter(typeof(TimelineAnimationUnificationSystem))]
    public partial class EditorRukhankaRunnerGroup : ComponentSystemGroup
    {
        protected override void OnCreate()
        {
            base.OnCreate();

            EnableSystemSorting = false;

            AddSystemToUpdateList(World.GetOrCreateSystem<AnimationProcessSystem>());

            var ikGroup = World.GetOrCreateSystemManaged<RukhankaAnimationInjectionSystemGroup>();
            ikGroup.AddSystemToUpdateList(World.GetOrCreateSystem<DynamicBoneChainSystem>());
            ikGroup.AddSystemToUpdateList(World.GetOrCreateSystem<AimIKSystem>());
            ikGroup.AddSystemToUpdateList(World.GetOrCreateSystem<FABRIKSystem>());
            ikGroup.AddSystemToUpdateList(World.GetOrCreateSystem<TwoBoneIKSystem>());
            ikGroup.AddSystemToUpdateList(World.GetOrCreateSystem<OverrideTransformIKSystem>());
            ikGroup.SortSystems();
            AddSystemToUpdateList(ikGroup);

            AddSystemToUpdateList(World.GetOrCreateSystem<AnimationApplicationSystem>());
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.Editor)]
    [UpdateInGroup(typeof(InitializationSystemGroup), OrderLast = true)]
    [CreateAfter(typeof(TimelineComponentAnimationGroup))]
    public partial class EditorPreviewBootstrap : SystemBase
    {
        private readonly List<SystemHandle> registeredSystems = new();

        protected override void OnCreate()
        {
            var timelineGroup = World.GetOrCreateSystemManaged<TimelineComponentAnimationGroup>();

            SystemHandle[] systems =
            {
                World.GetOrCreateSystem<DisableAnimationCullingInEditorSystem>(),
                World.GetOrCreateSystem<TimelineAnimationBlendTree2DTrackSystem>(),
                World.GetOrCreateSystem<TimelineSingleAnimationTrackSystem>(),
                World.GetOrCreateSystem<TimelineAnimationUnificationSystem>(),

                World.GetOrCreateSystem<EditorRukhankaRunnerGroup>()
            };

            foreach (var sys in systems)
            {
                timelineGroup.AddSystemToUpdateList(sys);
                registeredSystems.Add(sys);
            }

            var initGroup = World.GetExistingSystemManaged<InitializationSystemGroup>();
            if (initGroup != null)
            {
                var blobDbSystem = World.GetOrCreateSystem<BlobDatabaseUpdateSystem>();
                initGroup.AddSystemToUpdateList(blobDbSystem);
            }

            timelineGroup.SortSystems();
            Enabled = false;
        }

        protected override void OnDestroy()
        {
            if (World.IsCreated && registeredSystems.Count > 0)
                try
                {
                    var timelineGroup = World.GetExistingSystemManaged<TimelineComponentAnimationGroup>();
                    if (timelineGroup != null)
                        foreach (var sys in registeredSystems)
                            timelineGroup.RemoveSystemFromUpdateList(sys);
                }
                catch
                {
                }

            registeredSystems.Clear();
        }

        protected override void OnUpdate()
        {
        }
    }
}
#endif