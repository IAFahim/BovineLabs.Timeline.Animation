using BovineLabs.Timeline.Data;
using Unity.Burst;
using Unity.Collections;
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

        private NativeParallelMultiHashMap<Entity, WeaponAnchorSample> _samples;
        private NativeList<Entity> _weapons;
        private EntityQuery _clipQuery;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            _samples = new NativeParallelMultiHashMap<Entity, WeaponAnchorSample>(64, Allocator.Persistent);
            _weapons = new NativeList<Entity>(64, Allocator.Persistent);
            _clipQuery = SystemAPI.QueryBuilder()
                .WithAll<ClipActive, TimelineActive, WeaponAnchorData, TrackBinding>()
                .Build();
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

            state.Dependency = new ResolveJob
            {
                DeltaTime = SystemAPI.Time.DeltaTime,
                LocalToWorldLookup = state.GetComponentLookup<LocalToWorld>(true),
                ParentLookup = state.GetComponentLookup<Parent>(true)
            }.ScheduleParallel(state.Dependency);
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

        [BurstCompile]
        private partial struct ResolveJob : IJobEntity
        {
            public float DeltaTime;

            [ReadOnly] public ComponentLookup<LocalToWorld> LocalToWorldLookup;
            [ReadOnly] public ComponentLookup<Parent> ParentLookup;

            private void Execute(Entity entity, ref DynamicBuffer<WeaponAnchorSample> samples,
                ref LocalTransform transform, ref WeaponAnchorRest rest)
            {
                if (AnchorMath.WeightedBlend(samples, out var worldPosition, out var worldRotation))
                {
                    // Anchored this frame: snapshot the pre-attach pose on the activation edge, then drive the bone pose.
                    if (!rest.Captured)
                    {
                        rest.Position = transform.Position;
                        rest.Rotation = transform.Rotation;
                        rest.Captured = true;
                    }

                    if (ParentLookup.TryGetComponent(entity, out var parent) &&
                        LocalToWorldLookup.TryGetComponent(parent.Value, out var parentL2W) &&
                        TransformConversion.WorldToParentLocal(parentL2W.Value, worldPosition, worldRotation,
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

                    samples.Clear();
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