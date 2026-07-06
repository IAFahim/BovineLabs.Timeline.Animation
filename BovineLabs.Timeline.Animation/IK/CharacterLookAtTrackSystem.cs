using BovineLabs.Core;
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
    [WorldSystemFilter(WorldSystemFilterFlags.LocalSimulation | WorldSystemFilterFlags.ClientSimulation |
                       WorldSystemFilterFlags.ServerSimulation)]
    public partial struct CharacterLookAtTrackSystem : ISystem
    {
        private const float RelaxRate = 12f;

        // Distance in front of the character used as the fallback gaze point when a linked look-at target cannot be resolved.
        private const float ForwardFallbackDistance = 5f;

        private TrackBlendImpl<CharacterLookAtData, CharacterLookAtAnimated> _blendImpl;

        // Per-entity so a misconfigured character does not silence the warning for every other one (a global latch
        // never re-warns under CoreCLR, which has no domain reload).
        private NativeParallelHashSet<Entity> _missingRigWarned;

        private UnsafeComponentLookup<Targets> _targetsLookup;
        private UnsafeComponentLookup<EntityLinkSource> _sourcesLookup;
        private UnsafeBufferLookup<EntityLinkEntry> _entriesLookup;

        private ComponentLookup<LocalToWorld> _ltwLookup;
        private ComponentLookup<LocalTransform> _localTransformLookup;
        private ComponentLookup<Parent> _parentLookup;
        private ComponentLookup<PostTransformMatrix> _postTransformMatrixLookup;
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
            _postTransformMatrixLookup = state.GetComponentLookup<PostTransformMatrix>(true);
            _lookAtTargetLookup = state.GetComponentLookup<CharacterLookAtTarget>(true);
            _aimIKLookup = state.GetComponentLookup<AimIKComponent>();

            _missingRigWarned = new NativeParallelHashSet<Entity>(256, Allocator.Persistent);

            // A24: gate on the persistent look-at rig component (AimIKComponent), NOT the active-clip query. With no
            // look-at rig there is nothing to prepare, blend, relax or write; while a rig exists the RelaxJob must
            // keep running after the last clip ends to ease the AimIK weight back to rest.
            state.RequireForUpdate<AimIKComponent>();
        }

        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
            _blendImpl.OnDestroy(ref state);

            _missingRigWarned.Dispose();
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
            _postTransformMatrixLookup.Update(ref state);
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
                PostTransformMatrixLookup = _postTransformMatrixLookup,
                LtwLookup = _ltwLookup,
                AimIKLookup = _aimIKLookup,
                Logger = SystemAPI.GetSingleton<BLLogger>(),
                MissingRigWarned = _missingRigWarned
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
                            data.Target.TryResolve(bindingEntity, targets, Sources, Entries, out var resolved) &&
                            LtwLookup.TryGetComponent(resolved, out var resolvedLtw))
                        {
                            point = LocalTransform.FromMatrix(resolvedLtw.Value).Position;
                        }
                        else if (bindingEntity != Entity.Null && LtwLookup.TryGetComponent(bindingEntity, out var fallbackOwnerLtw))
                        {
                            // Unresolved look-at target: gaze along the character's forward direction
                            // rather than snapping the head to world origin (0,0,0).
                            point = fallbackOwnerLtw.Position +
                                    math.normalizesafe(fallbackOwnerLtw.Forward) * ForwardFallbackDistance;
                        }

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
                    Target = default
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
            [ReadOnly] public ComponentLookup<PostTransformMatrix> PostTransformMatrixLookup;
            [ReadOnly] public ComponentLookup<LocalToWorld> LtwLookup;

            public ComponentLookup<LocalTransform> LocalTransformLookup;
            public ComponentLookup<AimIKComponent> AimIKLookup;

            public BLLogger Logger;
            public NativeParallelHashSet<Entity> MissingRigWarned;

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
                if (!LookAtTargetLookup.TryGetComponent(entity, out var lookAtTarget))
                {
                    if (MissingRigWarned.Add(entity))
                    {
                        Logger.LogWarning512(
                            "[CharacterLookAt] A look-at clip is active but the bound character has no CharacterLookAtTarget rig (run Build Look-At Rig on the character). Look-at will be skipped.");
                    }

                    return;
                }

                var blended = JobHelpers.Blend<CharacterLookAtData, CharacterLookAtMixer>(ref mixData, default);

                if (math.any(math.isnan(blended.LookPoint))) return;

                // Blended limits (CharacterLookAtMixer.Lerp already interpolates AngleLimits) rather than slot 1's raw
                // limits, so two overlapping clips with different limits blend instead of snapping to whichever is first.
                var minA = math.min(blended.AngleLimits.x, blended.AngleLimits.y);
                var maxA = math.max(blended.AngleLimits.x, blended.AngleLimits.y);

                var targetEntity = lookAtTarget.TargetEntity;
                if (targetEntity != Entity.Null && LocalTransformLookup.HasComponent(targetEntity))
                {
                    var targetTransform = LocalTransformLookup[targetEntity];

                    // This runs before LocalToWorldSystem, so the parent's LocalToWorld is one frame stale — recompute
                    // the parent's world matrix from fresh LocalTransform (LocalToWorld fallback for non-hierarchy
                    // parents) so a head tracking a moving character does not trail a frame behind.
                    if (ParentLookup.TryGetComponent(targetEntity, out var parent) &&
                        TryGetFreshWorld(parent.Value, out var parentWorld))
                        targetTransform.Position = math.transform(math.inverse(parentWorld), blended.LookPoint);
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
                else if (MissingRigWarned.Add(entity))
                {
                    // CharacterLookAtTarget is baked unconditionally, so the absent-rig warning above never
                    // fires when only the AimIK half is missing. Surface it here instead of silently no-opping.
                    Logger.LogWarning512(
                        "[CharacterLookAt] A look-at clip is active but the bound character's look-at rig is not built (no AimIK on the head bone — run Build Look-At Rig). Look-at will be skipped.");
                }
            }

            private bool TryGetFreshWorld(Entity entity, out float4x4 world)
            {
                if (BoneWorld.TryComputeWorldMatrix(entity, LocalTransformLookup, ParentLookup,
                        PostTransformMatrixLookup, out world))
                    return true;

                if (LtwLookup.TryGetComponent(entity, out var l2w))
                {
                    world = l2w.Value;
                    return true;
                }

                return false;
            }
        }
    }
}