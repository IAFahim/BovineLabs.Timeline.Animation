using BovineLabs.Core.Extensions;
using BovineLabs.Core.Iterators;
using BovineLabs.Reaction.Data.Core;
using BovineLabs.Timeline.Data;
using BovineLabs.Timeline.EntityLinks;
using BovineLabs.Timeline.EntityLinks.Data;
using Rukhanka;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;

namespace BovineLabs.Timeline.Animation
{
    [UpdateInGroup(typeof(TimelineComponentAnimationGroup))]
    [UpdateBefore(typeof(AnimationProcessSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.LocalSimulation | WorldSystemFilterFlags.ClientSimulation |
                       WorldSystemFilterFlags.ServerSimulation)]
    public partial struct CharacterLookAtTrackSystem : ISystem
    {
        private const float RelaxRate = 12f;

        private TrackBlendImpl<CharacterLookAtData, CharacterLookAtAnimated> _blendImpl;

        private UnsafeComponentLookup<Targets> _targetsLookup;
        private UnsafeComponentLookup<EntityLinkSource> _sourcesLookup;
        private UnsafeBufferLookup<EntityLinkEntry> _entriesLookup;

        private ComponentLookup<LocalToWorld> _ltwLookup;
        private ComponentLookup<LocalTransform> _localTransformLookup;
        private ComponentLookup<Parent> _parentLookup;
        private ComponentLookup<CharacterLookAtTarget> _lookAtTargetLookup;
        private ComponentLookup<AimIKComponent> _aimIKLookup;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            _blendImpl.OnCreate(ref state);

            _targetsLookup = state.GetUnsafeComponentLookup<Targets>(true);
            _sourcesLookup = state.GetUnsafeComponentLookup<EntityLinkSource>(true);
            _entriesLookup = state.GetUnsafeBufferLookup<EntityLinkEntry>(true);

            _ltwLookup = state.GetComponentLookup<LocalToWorld>(true);
            _localTransformLookup = state.GetComponentLookup<LocalTransform>();
            _parentLookup = state.GetComponentLookup<Parent>(true);
            _lookAtTargetLookup = state.GetComponentLookup<CharacterLookAtTarget>(true);
            _aimIKLookup = state.GetComponentLookup<AimIKComponent>();
        }

        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
            _blendImpl.OnDestroy(ref state);
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            _targetsLookup.Update(ref state);
            _sourcesLookup.Update(ref state);
            _entriesLookup.Update(ref state);
            _ltwLookup.Update(ref state);
            _localTransformLookup.Update(ref state);
            _parentLookup.Update(ref state);
            _lookAtTargetLookup.Update(ref state);
            _aimIKLookup.Update(ref state);

            state.Dependency = new PrepareJob
            {
                TargetsLookup = _targetsLookup,
                Sources = _sourcesLookup,
                Entries = _entriesLookup,
                LtwLookup = _ltwLookup
            }.ScheduleParallel(state.Dependency);

            var blendData = _blendImpl.Update(ref state);

            state.Dependency = new RelaxJob
            {
                DeltaTime = SystemAPI.Time.DeltaTime,
                AimIKLookup = _aimIKLookup
            }.Schedule(state.Dependency);

            state.Dependency = new WriteLookAtJob
            {
                BlendData = blendData,
                LookAtTargetLookup = _lookAtTargetLookup,
                LocalTransformLookup = _localTransformLookup,
                ParentLookup = _parentLookup,
                LtwLookup = _ltwLookup,
                AimIKLookup = _aimIKLookup
            }.Schedule(state.Dependency);
        }

        [BurstCompile]
        [WithAll(typeof(ClipActive))]
        [WithAll(typeof(TimelineActive))]
        private partial struct PrepareJob : IJobEntity
        {
            [ReadOnly] public UnsafeComponentLookup<Targets> TargetsLookup;
            [ReadOnly] public UnsafeComponentLookup<EntityLinkSource> Sources;
            [ReadOnly] public UnsafeBufferLookup<EntityLinkEntry> Entries;
            [ReadOnly] public ComponentLookup<LocalToWorld> LtwLookup;

            private void Execute(ref CharacterLookAtAnimated animated, in TrackBinding binding)
            {
                var data = animated.AuthoredData;
                var bindingEntity = binding.Value;

                var point = data.StaticOrOffsetPoint;

                switch (data.SourceMode)
                {
                    case PointSourceMode.LinkedTarget:
                        if (bindingEntity != Entity.Null &&
                            TargetsLookup.TryGetComponent(bindingEntity, out var targets) &&
                            EntityLinkResolver.TryResolve(bindingEntity, targets, data.ReadRootFrom, data.TargetLinkKey,
                                Sources, Entries, out var resolved) &&
                            LtwLookup.TryGetComponent(resolved, out var resolvedLtw))
                            point = LocalTransform.FromMatrix(resolvedLtw.Value).Position;
                        break;

                    case PointSourceMode.OwnerOffset:
                        if (bindingEntity != Entity.Null && LtwLookup.TryGetComponent(bindingEntity, out var ownerLtw))
                            point = math.transform(ownerLtw.Value, data.StaticOrOffsetPoint);
                        break;
                }

                animated.Value = new CharacterLookAtData
                {
                    LookPoint = point,
                    Weight = data.Weight,
                    AngleLimits = data.AngleLimits,
                    SourceMode = PointSourceMode.StaticWorld,
                    StaticOrOffsetPoint = point,
                    TargetLinkKey = 0,
                    ReadRootFrom = default
                };
            }
        }

        [BurstCompile]
        private partial struct RelaxJob : IJobEntity
        {
            public float DeltaTime;

            [NativeDisableParallelForRestriction] public ComponentLookup<AimIKComponent> AimIKLookup;

            private void Execute(in CharacterLookAtTarget lookAtTarget)
            {
                if (!AimIKLookup.TryGetComponent(lookAtTarget.AimIKEntity, out var aim)) return;

                aim.weight = math.lerp(aim.weight, 0f, 1f - math.exp(-RelaxRate * DeltaTime));
                AimIKLookup[lookAtTarget.AimIKEntity] = aim;
            }
        }

        [BurstCompile]
        private struct WriteLookAtJob : IJob
        {
            [ReadOnly] public NativeParallelHashMap<Entity, MixData<CharacterLookAtData>>.ReadOnly BlendData;
            [ReadOnly] public ComponentLookup<CharacterLookAtTarget> LookAtTargetLookup;
            [ReadOnly] public ComponentLookup<Parent> ParentLookup;
            [ReadOnly] public ComponentLookup<LocalToWorld> LtwLookup;

            public ComponentLookup<LocalTransform> LocalTransformLookup;
            public ComponentLookup<AimIKComponent> AimIKLookup;

            public void Execute()
            {
                var enumerator = BlendData.GetEnumerator();
                while (enumerator.MoveNext())
                {
                    var current = enumerator.Current;
                    Apply(current.Key, current.Value);
                }
            }

            private void Apply(Entity entity, MixData<CharacterLookAtData> mixData)
            {
                if (!LookAtTargetLookup.TryGetComponent(entity, out var lookAtTarget)) return;

                var angleLimits = mixData.Value1.AngleLimits;

                var blended = JobHelpers.Blend<CharacterLookAtData, CharacterLookAtMixer>(ref mixData, default);

                if (math.any(math.isnan(blended.LookPoint))) return;

                var minA = math.min(angleLimits.x, angleLimits.y);
                var maxA = math.max(angleLimits.x, angleLimits.y);

                var targetEntity = lookAtTarget.TargetEntity;
                if (targetEntity != Entity.Null && LocalTransformLookup.HasComponent(targetEntity))
                {
                    var targetTransform = LocalTransformLookup[targetEntity];

                    if (ParentLookup.TryGetComponent(targetEntity, out var parent) &&
                        LtwLookup.TryGetComponent(parent.Value, out var parentLtw))
                        targetTransform.Position = math.transform(math.inverse(parentLtw.Value), blended.LookPoint);
                    else
                        targetTransform.Position = blended.LookPoint;

                    LocalTransformLookup[targetEntity] = targetTransform;
                }

                if (AimIKLookup.TryGetComponent(lookAtTarget.AimIKEntity, out var aim))
                {
                    aim.weight = math.saturate(blended.Weight);
                    aim.angleLimits = new float2(minA, maxA);
                    AimIKLookup[lookAtTarget.AimIKEntity] = aim;
                }
            }
        }
    }
}