using BovineLabs.Timeline.Data;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;

namespace BovineLabs.Timeline.Animation
{
    [UpdateInGroup(typeof(TransformSystemGroup))]
    [UpdateBefore(typeof(LocalToWorldSystem))]
    [BurstCompile]
    [WorldSystemFilter(WorldSystemFilterFlags.LocalSimulation | WorldSystemFilterFlags.ClientSimulation |
                       WorldSystemFilterFlags.ServerSimulation)]
    public partial struct WeaponAnchorBlendSystem : ISystem
    {
        private const float RelaxRate = 12f;
        private const float RestPositionEpsilonSq = 1e-8f;
        private const float RestRotationDot = 0.99999f;
        private const float VelocitySmoothing = 0.5f;

        private NativeParallelMultiHashMap<Entity, WeaponAnchorSample> _samples;
        private NativeList<Entity> _weapons;
        private EntityQuery _clipQuery;
        private EntityQuery _resolveQuery;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            _samples = new NativeParallelMultiHashMap<Entity, WeaponAnchorSample>(64, Allocator.Persistent);
            _weapons = new NativeList<Entity>(64, Allocator.Persistent);
            _clipQuery = SystemAPI.QueryBuilder()
                .WithAll<ClipActive, TimelineActive, WeaponAnchorData, TrackBinding>()
                .Build();
            _resolveQuery = SystemAPI.QueryBuilder()
                .WithAllRW<LocalTransform>()
                .WithAllRW<WeaponAnchorSample>()
                .WithAllRW<WeaponAnchorRest>()
                .Build();

            // A24: gate on the persistent weapon marker (WeaponAnchorSample), NOT the active-clip query. With no
            // weapon in the world there is nothing to gather or resolve; but while a weapon exists the resolve/apply
            // pass must keep running after the last clip ends to ease the weapon back to its rest pose.
            state.RequireForUpdate<WeaponAnchorSample>();
        }

        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
            if (_samples.IsCreated) _samples.Dispose();
            if (_weapons.IsCreated) _weapons.Dispose();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var count = _clipQuery.CalculateEntityCountWithoutFiltering();
            if (_samples.Capacity < count)
                _samples.Capacity = math.max(_samples.Capacity * 2, count);
            _samples.Clear();

            state.Dependency = new GatherJob
            {
                LocalTransformLookup = state.GetComponentLookup<LocalTransform>(true),
                ParentLookup = state.GetComponentLookup<Parent>(true),
                PostTransformMatrixLookup = state.GetComponentLookup<PostTransformMatrix>(true),
                ClipWeightLookup = state.GetComponentLookup<ClipWeight>(true),
                Samples = _samples.AsParallelWriter()
            }.ScheduleParallel(_clipQuery, state.Dependency);

            state.Dependency = new ExtractWeaponsJob
            {
                Samples = _samples,
                Weapons = _weapons
            }.Schedule(state.Dependency);

            state.Dependency = new FillBufferJob
            {
                Weapons = _weapons.AsDeferredJobArray(),
                Samples = _samples,
                Buffers = state.GetBufferLookup<WeaponAnchorSample>()
            }.Schedule(_weapons, 16, state.Dependency);

            // A11: resolve is split into a read-only gather that computes the final world poses (reading bone/parent
            // LocalTransform lookups only) and an apply pass that writes each weapon's own `ref LocalTransform` with no
            // LocalTransform lookup at all — so a weapon that ever lands in another weapon's bone/parent chain can't
            // race a same-frame read against a write. The poses hand off by dense query index (identical query).
            var poses = new NativeArray<ResolvedPose>(_resolveQuery.CalculateEntityCount(), Allocator.TempJob);

            state.Dependency = new GatherPoseJob
            {
                LocalToWorldLookup = state.GetComponentLookup<LocalToWorld>(true),
                LocalTransformLookup = state.GetComponentLookup<LocalTransform>(true),
                PostTransformMatrixLookup = state.GetComponentLookup<PostTransformMatrix>(true),
                ParentLookup = state.GetComponentLookup<Parent>(true),
                AttachmentLookup = state.GetComponentLookup<WeaponAttachment>(true),
                AttachmentAnchorLookup = state.GetComponentLookup<WeaponAttachmentAnchor>(true),
                Poses = poses
            }.ScheduleParallel(_resolveQuery, state.Dependency);

            state.Dependency = new ApplyPoseJob
            {
                DeltaTime = SystemAPI.Time.DeltaTime,
                Poses = poses,
                EaseLookup = state.GetComponentLookup<WeaponAttachEase>(),
                PoseVelocityLookup = state.GetComponentLookup<WeaponPoseVelocity>()
            }.ScheduleParallel(_resolveQuery, state.Dependency);

            poses.Dispose(state.Dependency);
        }

        [BurstCompile]
        [WithAll(typeof(ClipActive), typeof(TimelineActive))]
        private partial struct GatherJob : IJobEntity
        {
            [ReadOnly] public ComponentLookup<LocalTransform> LocalTransformLookup;
            [ReadOnly] public ComponentLookup<Parent> ParentLookup;
            [ReadOnly] public ComponentLookup<PostTransformMatrix> PostTransformMatrixLookup;
            [ReadOnly] public ComponentLookup<ClipWeight> ClipWeightLookup;
            public NativeParallelMultiHashMap<Entity, WeaponAnchorSample>.ParallelWriter Samples;

            private void Execute(Entity clipEntity, in WeaponAnchorData anchor, in TrackBinding binding)
            {
                if (!BoneWorld.TryComputeWorldMatrix(anchor.Bone, LocalTransformLookup, ParentLookup,
                        PostTransformMatrixLookup, out var boneWorld))
                    return;

                var weight = 1f;
                if (ClipWeightLookup.TryGetComponent(clipEntity, out var clipWeight))
                    weight = clipWeight.Value;
                if (weight <= 0f) return;

                var bonePosition = boneWorld.c3.xyz;
                var boneRotation = new quaternion(math.orthonormalize(new float3x3(boneWorld)));

                Samples.Add(binding.Value, new WeaponAnchorSample
                {
                    WorldPosition = bonePosition + math.mul(boneRotation, anchor.LocalPosition),
                    WorldRotation = math.mul(boneRotation, anchor.LocalRotation),
                    Weight = weight
                });
            }
        }

        [BurstCompile]
        private struct ExtractWeaponsJob : IJob
        {
            [ReadOnly] public NativeParallelMultiHashMap<Entity, WeaponAnchorSample> Samples;
            public NativeList<Entity> Weapons;

            public void Execute()
            {
                var (keys, count) = Samples.GetUniqueKeyArray(Allocator.Temp);
                Weapons.Clear();
                Weapons.AddRange(keys.GetSubArray(0, count));
                keys.Dispose();
            }
        }

        [BurstCompile]
        private struct FillBufferJob : IJobParallelForDefer
        {
            [ReadOnly] public NativeArray<Entity> Weapons;
            [ReadOnly] public NativeParallelMultiHashMap<Entity, WeaponAnchorSample> Samples;
            [NativeDisableParallelForRestriction] public BufferLookup<WeaponAnchorSample> Buffers;

            public void Execute(int index)
            {
                var weapon = Weapons[index];
                if (!Buffers.TryGetBuffer(weapon, out var buffer)) return;

                buffer.Clear();
                foreach (var sample in Samples.GetValuesForKey(weapon))
                    buffer.Add(sample);
            }
        }

        /// <summary> Final world pose computed by <see cref="GatherPoseJob" />, handed to <see cref="ApplyPoseJob" /> by query index. </summary>
        private struct ResolvedPose
        {
            public float3 WorldPosition;
            public quaternion WorldRotation;
            public float4x4 ParentWorld;
            public bool Anchored;
            public bool HasParentWorld;
        }

        /// <summary>
        /// Read-only phase of the resolve: computes each weapon's target world pose and fresh parent-world matrix from
        /// bone/parent LocalTransform lookups (never touching the weapon's own LocalTransform), and clears its consumed
        /// sample buffer. Writes only into <see cref="Poses" /> at the dense query index. No LocalTransform is written,
        /// so the RO lookups can't alias a write.
        /// </summary>
        [BurstCompile]
        [WithAll(typeof(WeaponAnchorRest))]
        private partial struct GatherPoseJob : IJobEntity
        {
            [ReadOnly] public ComponentLookup<LocalToWorld> LocalToWorldLookup;
            [ReadOnly] public ComponentLookup<LocalTransform> LocalTransformLookup;
            [ReadOnly] public ComponentLookup<PostTransformMatrix> PostTransformMatrixLookup;
            [ReadOnly] public ComponentLookup<Parent> ParentLookup;
            [ReadOnly] public ComponentLookup<WeaponAttachment> AttachmentLookup;
            [ReadOnly] public ComponentLookup<WeaponAttachmentAnchor> AttachmentAnchorLookup;
            [NativeDisableParallelForRestriction] public NativeArray<ResolvedPose> Poses;

            private void Execute(Entity entity, [EntityIndexInQuery] int index, ref DynamicBuffer<WeaponAnchorSample> samples)
            {
                var anchored = AnchorMath.WeightedBlend(samples, out var worldPosition, out var worldRotation);
                samples.Clear();

                // Persistent attachment: no grip clip covers the weapon this frame but it is still held —
                // one full-weight sample at the attachment grip so the equip pose survives the clip window.
                if (!anchored &&
                    AttachmentLookup.HasComponent(entity) &&
                    AttachmentLookup.IsComponentEnabled(entity) &&
                    AttachmentAnchorLookup.TryGetComponent(entity, out var attachmentAnchor) &&
                    attachmentAnchor.Bone != Entity.Null &&
                    BoneWorld.TryComputeWorldMatrix(attachmentAnchor.Bone, LocalTransformLookup, ParentLookup,
                        PostTransformMatrixLookup, out var boneWorld))
                {
                    var bonePosition = boneWorld.c3.xyz;
                    var boneRotation = new quaternion(math.orthonormalize(new float3x3(boneWorld)));
                    worldPosition = bonePosition + math.mul(boneRotation, attachmentAnchor.LocalPosition);
                    worldRotation = math.mul(boneRotation, attachmentAnchor.LocalRotation);
                    anchored = true;
                }

                // A4: this runs before LocalToWorldSystem, so LocalToWorld is one frame stale. Recompute the parent
                // world matrix from fresh LocalTransform; fall back to the cached LocalToWorld only for parents
                // outside the LocalTransform hierarchy.
                var parentWorld = float4x4.identity;
                var hasParentWorld = anchored &&
                                     ParentLookup.TryGetComponent(entity, out var parent) &&
                                     TryGetFreshWorld(parent.Value, out parentWorld);

                Poses[index] = new ResolvedPose
                {
                    Anchored = anchored,
                    WorldPosition = worldPosition,
                    WorldRotation = worldRotation,
                    HasParentWorld = hasParentWorld,
                    ParentWorld = parentWorld,
                };
            }

            private bool TryGetFreshWorld(Entity entity, out float4x4 world)
            {
                if (BoneWorld.TryComputeWorldMatrix(entity, LocalTransformLookup, ParentLookup,
                        PostTransformMatrixLookup, out world))
                    return true;

                if (LocalToWorldLookup.TryGetComponent(entity, out var l2w))
                {
                    world = l2w.Value;
                    return true;
                }

                return false;
            }
        }

        /// <summary>
        /// Write phase of the resolve: applies the pre-computed <see cref="ResolvedPose" /> to each weapon's own
        /// <c>ref LocalTransform</c>, running the pickup ease, drop-velocity EMA and relax-to-rest against the weapon's
        /// own state only. Reads no LocalTransform lookup, so the write can't alias another job's read.
        /// </summary>
        [BurstCompile]
        [WithAll(typeof(WeaponAnchorSample))]
        private partial struct ApplyPoseJob : IJobEntity
        {
            public float DeltaTime;
            [ReadOnly] public NativeArray<ResolvedPose> Poses;

            // Both written only through the executing entity itself; never another chunk's.
            [NativeDisableParallelForRestriction] public ComponentLookup<WeaponAttachEase> EaseLookup;
            [NativeDisableParallelForRestriction] public ComponentLookup<WeaponPoseVelocity> PoseVelocityLookup;

            private void Execute(Entity entity, [EntityIndexInQuery] int index,
                ref LocalTransform transform, ref WeaponAnchorRest rest)
            {
                var pose = Poses[index];
                var worldPosition = pose.WorldPosition;
                var worldRotation = pose.WorldRotation;

                if (pose.Anchored)
                {
                    // Anchored this frame: snapshot the pre-attach pose on the activation edge, then drive the bone pose.
                    if (!rest.Captured)
                    {
                        rest.Position = transform.Position;
                        rest.Rotation = transform.Rotation;
                        rest.Captured = true;
                    }

                    var parentWorld = pose.ParentWorld;
                    var hasParentWorld = pose.HasParentWorld;

                    // Pickup ease: relax from the current (ground) pose toward the anchor target instead of
                    // snapping — the detach relax math run in the attach direction.
                    if (EaseLookup.TryGetComponent(entity, out var ease) && ease.Active != 0)
                    {
                        var currentPosition = transform.Position;
                        var currentRotation = transform.Rotation;
                        if (hasParentWorld)
                        {
                            currentPosition = math.transform(parentWorld, currentPosition);
                            currentRotation = math.mul(new quaternion(math.orthonormalize(new float3x3(parentWorld))), currentRotation);
                        }

                        var ct = 1f - math.exp(-RelaxRate * DeltaTime);
                        var easedPosition = math.lerp(currentPosition, worldPosition, ct);
                        var easedRotation = math.slerp(currentRotation, worldRotation, ct);

                        if (math.distancesq(easedPosition, worldPosition) <= RestPositionEpsilonSq &&
                            math.abs(math.dot(easedRotation, worldRotation)) >= RestRotationDot)
                        {
                            ease.Active = 0;
                            EaseLookup[entity] = ease;
                        }
                        else
                        {
                            worldPosition = easedPosition;
                            worldRotation = easedRotation;
                        }
                    }

                    // Track the blended pose's world velocity so Drop can hand a believable throw to physics.
                    // The tracked value is an EMA of the per-frame finite difference (seeded exact on the first
                    // sample) so a single hitch frame at the drop moment can't produce an absurd throw.
                    if (PoseVelocityLookup.TryGetComponent(entity, out var velocity))
                    {
                        if (velocity.HasPrev != 0 && DeltaTime > 0f)
                        {
                            var instantLinear = (worldPosition - velocity.PrevPosition) / DeltaTime;

                            var dq = math.mul(worldRotation, math.inverse(velocity.PrevRotation));
                            dq.value = math.select(dq.value, -dq.value, dq.value.w < 0f);
                            var axisLengthSq = math.lengthsq(dq.value.xyz);
                            var instantAngular = axisLengthSq > 1e-12f
                                ? dq.value.xyz * math.rsqrt(axisLengthSq) *
                                  (2f * math.acos(math.clamp(dq.value.w, -1f, 1f)) / DeltaTime)
                                : float3.zero;

                            if (velocity.HasVelocity != 0)
                            {
                                velocity.Linear = math.lerp(velocity.Linear, instantLinear, VelocitySmoothing);
                                velocity.Angular = math.lerp(velocity.Angular, instantAngular, VelocitySmoothing);
                            }
                            else
                            {
                                velocity.Linear = instantLinear;
                                velocity.Angular = instantAngular;
                                velocity.HasVelocity = 1;
                            }
                        }

                        velocity.PrevPosition = worldPosition;
                        velocity.PrevRotation = worldRotation;
                        velocity.HasPrev = 1;
                        PoseVelocityLookup[entity] = velocity;
                    }

                    if (hasParentWorld &&
                        TransformConversion.WorldToParentLocal(parentWorld, worldPosition, worldRotation,
                            out var localPosition, out var localRotation))
                    {
                        transform.Position = localPosition;
                        transform.Rotation = localRotation;
                    }
                    else
                    {
                        transform.Position = worldPosition;
                        transform.Rotation = worldRotation;
                    }

                    return;
                }

                // Deactivated: relax back toward the captured rest pose instead of freezing at the last anchored pose.
                if (!rest.Captured)
                    return;

                var t = 1f - math.exp(-RelaxRate * DeltaTime);
                transform.Position = math.lerp(transform.Position, rest.Position, t);
                transform.Rotation = math.slerp(transform.Rotation, rest.Rotation, t);

                if (math.distancesq(transform.Position, rest.Position) <= RestPositionEpsilonSq &&
                    math.abs(math.dot(transform.Rotation, rest.Rotation)) >= RestRotationDot)
                {
                    transform.Position = rest.Position;
                    transform.Rotation = rest.Rotation;
                    rest.Captured = false;
                }
            }
        }
    }
}
