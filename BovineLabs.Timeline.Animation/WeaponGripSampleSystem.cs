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
        private EntityQuery _attachmentQuery;

        // Not [BurstCompile]: RequireAnyForUpdate(a, b) allocates a managed EntityQuery[]
        // (params), which Burst rejects (BC1028). OnCreate runs once — no Burst benefit.
        public void OnCreate(ref SystemState state)
        {
            _bones = new NativeParallelHashMap<RigBoneKey, Entity>(256, Allocator.Persistent);
            _warned = new NativeParallelHashSet<ulong>(16, Allocator.Persistent);

            _boneQuery = SystemAPI.QueryBuilder().WithAll<AnimatorEntityRefComponent>().Build();
            _clipQuery = SystemAPI.QueryBuilder()
                .WithAll<ClipActive, TimelineActive, WeaponGripClipData, TrackBinding, DirectorRoot>()
                .WithAllRW<WeaponAnchorData>()
                .Build();
            _attachmentQuery = SystemAPI.QueryBuilder()
                .WithAll<WeaponAttachment, ObjectId>()
                .WithAllRW<WeaponAttachmentAnchor>()
                .Build();

            state.RequireForUpdate<WeaponGripRegistry>();
            state.RequireAnyForUpdate(_clipQuery, _attachmentQuery);
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

            var registry = SystemAPI.GetSingleton<WeaponGripRegistry>().Value;

            var gripHandle = new ResolveGripJob
            {
                Registry = registry,
                ObjectIdLookup = SystemAPI.GetComponentLookup<ObjectId>(true),
                AttachmentLookup = SystemAPI.GetComponentLookup<WeaponAttachment>(true),
                Bones = _bones,
                Warned = _warned.AsParallelWriter()
            }.ScheduleParallel(_clipQuery, state.Dependency);

            // Persistent attachments (b): while enabled and no grip clip is sampling the weapon, the resolved
            // anchor feeds one full-weight sample in WeaponAnchorBlendSystem — the weapon stays in the hand.
            var attachmentHandle = new ResolveAttachmentJob
            {
                Registry = registry,
                Bones = _bones,
                Warned = _warned.AsParallelWriter()
            }.ScheduleParallel(_attachmentQuery, state.Dependency);

            state.Dependency = Unity.Jobs.JobHandle.CombineDependencies(gripHandle, attachmentHandle);
        }

        /// <summary> Finds the index of <paramref name="key" /> in <paramref name="grips" />, else the default grip; -1 when neither resolves. </summary>
        internal static int ResolveGripIndex(ref WeaponGrips grips, uint key)
        {
            for (var i = 0; i < grips.Grips.Length; i++)
            {
                if (grips.Grips[i].Key == key)
                    return i;
            }

            return (uint)grips.DefaultGrip < (uint)grips.Grips.Length ? grips.DefaultGrip : -1;
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
                var index = ResolveGripIndex(ref grips, clip.Grip);
                if (index < 0)
                    return;

#if BL_DEBUG
                if (grips.Grips[index].Key != clip.Grip && clip.Grip != 0 &&
                    Warned.Add(((ulong)(uint)objectId.RawValue << 32) | clip.Grip))
                {
                    UnityEngine.Debug.LogWarning("WeaponGripSampleSystem: clip references a missing grip key; using the weapon's default grip.");
                }
#endif

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

        /// <summary>
        /// Resolves the persistent-attachment anchor for every enabled <see cref="WeaponAttachment" /> (query is
        /// enabled-only by default): attachment grip → bone on the holder's rig. Consumed as a full-weight sample by
        /// WeaponAnchorBlendSystem while no grip clip covers the weapon.
        /// </summary>
        [BurstCompile]
        private partial struct ResolveAttachmentJob : IJobEntity
        {
            [ReadOnly] public BlobAssetReference<WeaponGripRegistryBlob> Registry;
            [ReadOnly] public NativeParallelHashMap<RigBoneKey, Entity> Bones;
            public NativeParallelHashSet<ulong>.ParallelWriter Warned;

            private void Execute(in ObjectId objectId, in WeaponAttachment attachment, ref WeaponAttachmentAnchor anchor)
            {
                anchor.Bone = Entity.Null;

                if (attachment.Holder == Entity.Null)
                    return;

                if (!Registry.Value.Weapons.TryGetValue(objectId, out var gripsPtr))
                {
#if BL_DEBUG
                    if (Warned.Add((ulong)(uint)objectId.RawValue << 32))
                        UnityEngine.Debug.LogWarning("WeaponGripSampleSystem: attached weapon ObjectId has no grips in the registry.");
#endif
                    return;
                }

                ref var grips = ref gripsPtr.Ref;
                var index = ResolveGripIndex(ref grips, attachment.Grip);
                if (index < 0)
                    return;

                ref var grip = ref grips.Grips[index];
                if (!Bones.TryGetValue(new RigBoneKey { Rig = attachment.Holder, Hash = grip.BoneHash }, out var bone))
                    return;

                anchor.Bone = bone;
                anchor.LocalPosition = grip.Position;
                anchor.LocalRotation = grip.Rotation;
            }
        }
    }
}
#endif
