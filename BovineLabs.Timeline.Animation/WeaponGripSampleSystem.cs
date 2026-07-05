#if !BL_DISABLE_OBJECT_DEFINITION
using System;
using BovineLabs.Core.ObjectManagement;
using BovineLabs.Timeline.Data;
using Rukhanka;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace BovineLabs.Timeline.Animation
{
    /// <summary>
    /// Resolves active <see cref="WeaponGripClipData" /> clips into <see cref="WeaponAnchorData" /> each frame:
    /// weapon ObjectId → <see cref="WeaponGripRegistry" /> → grip by key (default grip fallback) → bone entity on
    /// the holder's rig by Rukhanka name hash. Everything downstream (blend, ease, relax-to-rest) is the unchanged
    /// <see cref="WeaponAnchorBlendSystem" /> pipeline.
    /// </summary>
    [UpdateInGroup(typeof(TransformSystemGroup))]
    [UpdateBefore(typeof(WeaponAnchorBlendSystem))]
    [BurstCompile]
    [WorldSystemFilter(WorldSystemFilterFlags.LocalSimulation | WorldSystemFilterFlags.ClientSimulation |
                       WorldSystemFilterFlags.ServerSimulation)]
    public partial struct WeaponGripSampleSystem : ISystem
    {
        private NativeParallelHashMap<RigBoneKey, Entity> _bones;
        private NativeParallelHashSet<ulong> _warned;
        private EntityQuery _boneQuery;
        private EntityQuery _clipQuery;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            _bones = new NativeParallelHashMap<RigBoneKey, Entity>(256, Allocator.Persistent);
            _warned = new NativeParallelHashSet<ulong>(16, Allocator.Persistent);

            _boneQuery = SystemAPI.QueryBuilder().WithAll<AnimatorEntityRefComponent>().Build();
            _clipQuery = SystemAPI.QueryBuilder()
                .WithAll<ClipActive, TimelineActive, WeaponGripClipData, TrackBinding, DirectorRoot>()
                .WithAllRW<WeaponAnchorData>()
                .Build();

            state.RequireForUpdate<WeaponGripRegistry>();
            state.RequireForUpdate(_clipQuery);
        }

        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
            if (_bones.IsCreated) _bones.Dispose();
            if (_warned.IsCreated) _warned.Dispose();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var boneCount = _boneQuery.CalculateEntityCountWithoutFiltering();
            if (_bones.Capacity < boneCount)
                _bones.Capacity = math.max(_bones.Capacity * 2, boneCount);
            _bones.Clear();

            state.Dependency = new BuildBoneMapJob
            {
                RigLookup = SystemAPI.GetComponentLookup<RigDefinitionComponent>(true),
                Bones = _bones.AsParallelWriter()
            }.ScheduleParallel(_boneQuery, state.Dependency);

            state.Dependency = new ResolveGripJob
            {
                Registry = SystemAPI.GetSingleton<WeaponGripRegistry>().Value,
                ObjectIdLookup = SystemAPI.GetComponentLookup<ObjectId>(true),
                AttachmentLookup = SystemAPI.GetComponentLookup<WeaponAttachment>(true),
                Bones = _bones,
                Warned = _warned.AsParallelWriter()
            }.ScheduleParallel(_clipQuery, state.Dependency);
        }

        internal struct RigBoneKey : IEquatable<RigBoneKey>
        {
            public Entity Rig;
            public uint Hash;

            public bool Equals(RigBoneKey other) => Rig == other.Rig && Hash == other.Hash;

            public override int GetHashCode() => (int)math.hash(new uint3((uint)Rig.Index, (uint)Rig.Version, Hash));
        }

        /// <summary> Maps (rig root, Rukhanka bone name hash) → bone entity for every exposed bone. </summary>
        [BurstCompile]
        private partial struct BuildBoneMapJob : IJobEntity
        {
            [ReadOnly] public ComponentLookup<RigDefinitionComponent> RigLookup;
            public NativeParallelHashMap<RigBoneKey, Entity>.ParallelWriter Bones;

            private void Execute(Entity entity, in AnimatorEntityRefComponent boneRef)
            {
                if (!RigLookup.TryGetComponent(boneRef.animatorEntity, out var rig) || !rig.rigBlob.IsCreated)
                    return;

                ref var bones = ref rig.rigBlob.Value.bones;
                if ((uint)boneRef.boneIndexInAnimationRig >= (uint)bones.Length)
                    return;

                Bones.TryAdd(new RigBoneKey
                {
                    Rig = boneRef.animatorEntity,
                    Hash = bones[boneRef.boneIndexInAnimationRig].hash
                }, entity);
            }
        }

        [BurstCompile]
        [WithAll(typeof(ClipActive), typeof(TimelineActive))]
        private partial struct ResolveGripJob : IJobEntity
        {
            [ReadOnly] public BlobAssetReference<WeaponGripRegistryBlob> Registry;
            [ReadOnly] public ComponentLookup<ObjectId> ObjectIdLookup;
            [ReadOnly] public ComponentLookup<WeaponAttachment> AttachmentLookup;
            [ReadOnly] public NativeParallelHashMap<RigBoneKey, Entity> Bones;
            public NativeParallelHashSet<ulong>.ParallelWriter Warned;

            private void Execute(
                in WeaponGripClipData clip, in TrackBinding binding, in DirectorRoot root, ref WeaponAnchorData anchor)
            {
                // Default: no contribution this frame. BoneWorld rejects Entity.Null so the legacy GatherJob skips us.
                anchor.Bone = Entity.Null;

                var weapon = binding.Value;
                if (!ObjectIdLookup.TryGetComponent(weapon, out var objectId))
                    return;

                if (!Registry.Value.Weapons.TryGetValue(objectId, out var gripsPtr))
                {
#if BL_DEBUG
                    if (Warned.Add((ulong)(uint)objectId.RawValue << 32))
                        UnityEngine.Debug.LogWarning("WeaponGripSampleSystem: weapon ObjectId has no grips in the registry.");
#endif
                    return;
                }

                ref var grips = ref gripsPtr.Ref;
                var index = -1;
                for (var i = 0; i < grips.Grips.Length; i++)
                {
                    if (grips.Grips[i].Key == clip.Grip)
                    {
                        index = i;
                        break;
                    }
                }

                if (index < 0)
                {
#if BL_DEBUG
                    if (clip.Grip != 0 && Warned.Add(((ulong)(uint)objectId.RawValue << 32) | clip.Grip))
                        UnityEngine.Debug.LogWarning("WeaponGripSampleSystem: clip references a missing grip key; using the weapon's default grip.");
#endif
                    index = grips.DefaultGrip;
                    if ((uint)index >= (uint)grips.Grips.Length)
                        return;
                }

                ref var grip = ref grips.Grips[index];

                var holder = root.Director;
                if (AttachmentLookup.TryGetComponent(weapon, out var attachment) &&
                    AttachmentLookup.IsComponentEnabled(weapon) &&
                    attachment.Holder != Entity.Null)
                {
                    holder = attachment.Holder;
                }

                if (!Bones.TryGetValue(new RigBoneKey { Rig = holder, Hash = grip.BoneHash }, out var bone))
                    return;

                anchor.Bone = bone;
                anchor.LocalPosition = grip.Position;
                anchor.LocalRotation = grip.Rotation;
            }
        }
    }
}
#endif
