#if !BL_DISABLE_OBJECT_DEFINITION
using System.Collections.Generic;
using BovineLabs.Core.ObjectManagement;
using BovineLabs.Testing;
using BovineLabs.Timeline.Data;
using NUnit.Framework;
using Rukhanka;
using Rukhanka.Toolbox;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace BovineLabs.Timeline.Animation.Tests
{
    public class WeaponGripSampleSystemTests : ECSTestsFixture
    {
        private static readonly uint OneHand = "OneHand".CalculateHash32();
        private static readonly uint TwoHand = "TwoHand".CalculateHash32();
        private static readonly uint RightHand = "RightHand".CalculateHash32();
        private static readonly uint LeftHand = "LeftHand".CalculateHash32();

        private readonly List<BlobAssetReference<RigDefinitionBlob>> rigBlobs = new();
        private readonly List<BlobAssetReference<WeaponGripRegistryBlob>> registryBlobs = new();

        [TearDown]
        public void DisposeBlobs()
        {
            foreach (var blob in rigBlobs)
                if (blob.IsCreated)
                    blob.Dispose();
            rigBlobs.Clear();

            foreach (var blob in registryBlobs)
                if (blob.IsCreated)
                    blob.Dispose();
            registryBlobs.Clear();
        }

        [Test]
        public void ResolvesGripIntoAnchorData()
        {
            CreateRegistry(weaponId: 7, defaultGrip: 1);
            var (holder, rightBone, _) = CreateRig();
            var weapon = CreateWeapon(7);
            var clip = CreateClip(weapon, holder, OneHand);

            Update();

            var anchor = Manager.GetComponentData<WeaponAnchorData>(clip);
            Assert.AreEqual(rightBone, anchor.Bone, "grip must resolve to the holder's RightHand bone");
            Assert.IsTrue(math.all(math.abs(anchor.LocalPosition - new float3(0.1f, 0.2f, 0.3f)) < 1e-5f));
            Assert.Greater(math.abs(math.dot(anchor.LocalRotation, quaternion.RotateY(0.5f))), 0.99999f);
        }

        [Test]
        public void MissingGripKeyFallsBackToDefaultGrip()
        {
            CreateRegistry(weaponId: 7, defaultGrip: 1);
            var (holder, _, leftBone) = CreateRig();
            var weapon = CreateWeapon(7);
            var clip = CreateClip(weapon, holder, "DoesNotExist".CalculateHash32());

            Update();

            var anchor = Manager.GetComponentData<WeaponAnchorData>(clip);
            Assert.AreEqual(leftBone, anchor.Bone, "unknown grip keys must fall back to the weapon's default grip (TwoHand/LeftHand)");
        }

        [Test]
        public void UnregisteredWeaponContributesNothing()
        {
            CreateRegistry(weaponId: 7, defaultGrip: 0);
            var (holder, rightBone, _) = CreateRig();
            var weapon = CreateWeapon(99);
            var clip = CreateClip(weapon, holder, OneHand);

            // Poison the anchor to prove the system clears it rather than leaving stale data.
            Manager.SetComponentData(clip, new WeaponAnchorData { Bone = rightBone, LocalRotation = quaternion.identity });

            Update();

            Assert.AreEqual(Entity.Null, Manager.GetComponentData<WeaponAnchorData>(clip).Bone);
        }

        [Test]
        public void EnabledAttachmentHolderOverridesDirector()
        {
            CreateRegistry(weaponId: 7, defaultGrip: 0);
            var (director, directorBone, _) = CreateRig();
            var (other, otherBone, _) = CreateRig();
            var weapon = CreateWeapon(7);
            var clip = CreateClip(weapon, director, OneHand);

            Manager.AddComponentData(weapon, new WeaponAttachment { Holder = other, Grip = OneHand });

            Update();
            Assert.AreEqual(otherBone, Manager.GetComponentData<WeaponAnchorData>(clip).Bone,
                "an enabled WeaponAttachment must win over the timeline owner");

            Manager.SetComponentEnabled<WeaponAttachment>(weapon, false);

            Update();
            Assert.AreEqual(directorBone, Manager.GetComponentData<WeaponAnchorData>(clip).Bone,
                "a disabled WeaponAttachment must fall back to the timeline owner");
        }

        private void Update()
        {
            var system = World.CreateSystem<WeaponGripSampleSystem>();
            system.Update(WorldUnmanaged);
            Manager.CompleteAllTrackedJobs();
        }

        private void CreateRegistry(int weaponId, int defaultGrip)
        {
            var blob = WeaponGripRegistryTests.BuildRegistry(weaponId, defaultGrip);
            registryBlobs.Add(blob);

            var entity = Manager.CreateEntity();
            Manager.AddComponentData(entity, new WeaponGripRegistry { Value = blob });
        }

        private (Entity holder, Entity rightBone, Entity leftBone) CreateRig()
        {
            var builder = new BlobBuilder(Allocator.Temp);
            ref var root = ref builder.ConstructRoot<RigDefinitionBlob>();
            var bones = builder.Allocate(ref root.bones, 3);
            bones[0] = new RigBoneInfo { hash = "Root".CalculateHash32() };
            bones[1] = new RigBoneInfo { hash = RightHand };
            bones[2] = new RigBoneInfo { hash = LeftHand };
            var blob = builder.CreateBlobAssetReference<RigDefinitionBlob>(Allocator.Persistent);
            builder.Dispose();
            rigBlobs.Add(blob);

            var holder = Manager.CreateEntity();
            Manager.AddComponentData(holder, new RigDefinitionComponent { rigBlob = blob });

            var rightBone = CreateBone(holder, 1);
            var leftBone = CreateBone(holder, 2);
            return (holder, rightBone, leftBone);
        }

        private Entity CreateBone(Entity holder, int boneIndex)
        {
            var bone = Manager.CreateEntity();
            Manager.AddComponentData(bone, new AnimatorEntityRefComponent
            {
                animatorEntity = holder,
                boneIndexInAnimationRig = boneIndex
            });
            return bone;
        }

        private Entity CreateWeapon(int id)
        {
            var weapon = Manager.CreateEntity();
            Manager.AddComponentData(weapon, new ObjectId(id));
            return weapon;
        }

        private Entity CreateClip(Entity weapon, Entity director, uint grip)
        {
            var clip = Manager.CreateEntity();
            Manager.AddComponent<ClipActive>(clip);
            Manager.AddComponent<TimelineActive>(clip);
            Manager.AddComponentData(clip, new WeaponGripClipData { Grip = grip });
            Manager.AddComponentData(clip, new TrackBinding { Value = weapon });
            Manager.AddComponentData(clip, new DirectorRoot { Director = director });
            Manager.AddComponentData(clip, new WeaponAnchorData { Bone = Entity.Null, LocalRotation = quaternion.identity });
            return clip;
        }
    }
}
#endif
