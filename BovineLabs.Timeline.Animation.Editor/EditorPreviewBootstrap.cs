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

            // Mirror RukhankaSystemsBootstrap's runtime order exactly: Process -> IK injection -> Application.
            // Order is fixed by hand (no sorting), so insertion order IS execution order.
            EnableSystemSorting = false;

            // 1. Process System computes/resizes the animation buffers.
            AddSystemToUpdateList(World.GetOrCreateSystem<AnimationProcessSystem>());

            // 2. IK injection pass (AimIK look-at, two-bone foot IK, FABRIK, override, dynamic bones).
            //    Rukhanka's IK systems carry no Editor world-filter, so they are NOT auto-created in
            //    this preview world the way they are in the game world. Create each explicitly and add
            //    it to the injection group, then let the group sort them by their own
            //    OrderFirst/UpdateAfter attributes (DynamicBoneChain first, TwoBoneIK after AimIK+FABRIK).
            var ikGroup = World.GetOrCreateSystemManaged<RukhankaAnimationInjectionSystemGroup>();
            ikGroup.AddSystemToUpdateList(World.GetOrCreateSystem<DynamicBoneChainSystem>());
            ikGroup.AddSystemToUpdateList(World.GetOrCreateSystem<AimIKSystem>());
            ikGroup.AddSystemToUpdateList(World.GetOrCreateSystem<FABRIKSystem>());
            ikGroup.AddSystemToUpdateList(World.GetOrCreateSystem<TwoBoneIKSystem>());
            ikGroup.AddSystemToUpdateList(World.GetOrCreateSystem<OverrideTransformIKSystem>());
            ikGroup.SortSystems();
            AddSystemToUpdateList(ikGroup);

            // 3. Application System applies the final (post-IK) pose to the Entity transforms.
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

                // Add our custom rigid-ordered group instead of the manual barrier
                World.GetOrCreateSystem<EditorRukhankaRunnerGroup>()
            };

            foreach (var sys in systems)
            {
                timelineGroup.AddSystemToUpdateList(sys);
                registeredSystems.Add(sys);
            }

            // CRITICAL: Ensure Rukhanka's Blob Database updates in the Editor World!
            // Without this, baked animations triggered by the Timeline won't resolve.
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
                    /* World destruction in progress */
                }

            registeredSystems.Clear();
        }

        protected override void OnUpdate()
        {
        }
    }
}
#endif