#if !BL_DISABLE_OBJECT_DEFINITION
using System.Collections.Generic;
using BovineLabs.Core.ObjectManagement;
using BovineLabs.Testing;
using BovineLabs.Timeline.Data;
using NUnit.Framework;
using Unity.Collections;
using Unity.Core;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace BovineLabs.Timeline.Animation.Tests
{
    public class WeaponLifecycleSystemTests : ECSTestsFixture
    {
        private const float DeltaTime = 1f / 60f;

        private readonly List<NativeHashMap<ObjectId, Entity>> registryMaps = new();

        private double elapsed;

        [TearDown]
        public void DisposeMaps()
        {
            foreach (var map in registryMaps)
                if (map.IsCreated)
                    map.Dispose();
            registryMaps.Clear();
        }

        [Test]
        public void PersistentAttachmentDrivesPoseWithoutSamples()
        {
            var bone = CreateBone(new float3(1f, 2f, 3f));
            var weapon = CreateWeapon(new float3(5f, 0f, 0f));
            Attach(weapon, bone, new float3(0f, 0.5f, 0f));

            Update();

            // No grip clip contributed a sample this frame, yet the weapon must stay on the anchor.
            var transform = Manager.GetComponentData<LocalTransform>(weapon);
            Assert.IsTrue(math.all(math.abs(transform.Position - new float3(1f, 2.5f, 3f)) < 1e-4f),
                "an enabled WeaponAttachment must keep driving the pose when no anchor samples exist");

            var rest = Manager.GetComponentData<WeaponAnchorRest>(weapon);
            Assert.IsTrue(rest.Captured, "the pre-attach pose must be captured on the activation edge");
            Assert.IsTrue(math.all(math.abs(rest.Position - new float3(5f, 0f, 0f)) < 1e-5f));
        }

        [Test]
        public void DisabledAttachmentRelaxesBackToRest()
        {
            var bone = CreateBone(new float3(1f, 2f, 3f));
            var weapon = CreateWeapon(new float3(5f, 0f, 0f));
            Attach(weapon, bone, float3.zero);

            Update();
            Manager.SetComponentEnabled<WeaponAttachment>(weapon, false);

            for (var i = 0; i < 600; i++)
                Update();

            var transform = Manager.GetComponentData<LocalTransform>(weapon);
            Assert.IsTrue(math.all(math.abs(transform.Position - new float3(5f, 0f, 0f)) < 1e-2f),
                "a dropped attachment must relax back toward the captured rest pose");
        }

        [Test]
        public void AnchoredPoseTracksWorldVelocity()
        {
            var bone = CreateBone(new float3(1f, 0f, 0f));
            var weapon = CreateWeapon(float3.zero);
            Attach(weapon, bone, float3.zero);
            Manager.AddComponent<WeaponPoseVelocity>(weapon);

            Update();

            // Move the anchor bone one frame later; the tracked velocity must reflect the world delta.
            Manager.SetComponentData(bone, LocalTransform.FromPosition(new float3(1.5f, 0f, 0f)));
            Update();

            var velocity = Manager.GetComponentData<WeaponPoseVelocity>(weapon);
            var expected = new float3(0.5f, 0f, 0f) / DeltaTime;
            Assert.IsTrue(math.all(math.abs(velocity.Linear - expected) < 1e-2f),
                "drop hand-off velocity must equal the blended pose's world delta over DeltaTime");
        }

        [Test]
        public void PickupEasesInsteadOfSnapping()
        {
            var bone = CreateBone(new float3(10f, 0f, 0f));
            var weapon = CreateWeapon(float3.zero);
            Attach(weapon, bone, float3.zero);
            Manager.AddComponentData(weapon, new WeaponAttachEase { Active = 1 });

            Update();

            var x = Manager.GetComponentData<LocalTransform>(weapon).Position.x;
            Assert.Greater(x, 0f, "pickup must start moving toward the anchor");
            Assert.Less(x, 10f - 1e-3f, "pickup must not snap to the anchor on the first frame");

            for (var i = 0; i < 600; i++)
                Update();

            Assert.AreEqual(0, Manager.GetComponentData<WeaponAttachEase>(weapon).Active,
                "the ease must clear itself once the pose converges on the anchor");
            Assert.IsTrue(math.abs(Manager.GetComponentData<LocalTransform>(weapon).Position.x - 10f) < 1e-2f);
        }

        [Test]
        public void EquipReusesInsteadOfAccumulating()
        {
            const int objectId = 42;
            var prefab = CreatePrefab();
            CreateRegistry(objectId, prefab);
            var holder = Manager.CreateEntity();
            var clip = CreateStateClip(WeaponStateMode.Equip, objectId, holder, Entity.Null);

            // First activation spawns exactly one weapon and records it on the holder.
            RunLifecycle();
            Assert.AreEqual(1, WeaponCount(), "the first Equip activation must spawn exactly one weapon");
            Assert.IsTrue(Manager.HasComponent<EquippedWeapon>(holder), "Equip must record the weapon on the holder");
            var first = Manager.GetComponentData<EquippedWeapon>(holder).Weapon;
            Assert.IsTrue(Manager.Exists(first));

            // Re-arm the edge (clip deactivates then re-activates, as a looping timeline does).
            Manager.SetComponentEnabled<ClipActive>(clip, false);
            RunLifecycle();
            Manager.SetComponentEnabled<ClipActive>(clip, true);
            RunLifecycle();

            Assert.AreEqual(1, WeaponCount(), "a second Equip of the same weapon must reuse the instance, not accumulate");
            Assert.AreEqual(first, Manager.GetComponentData<EquippedWeapon>(holder).Weapon,
                "the reused weapon must be the same instance");
        }

        [Test]
        public void DropResolvesEquipSpawnedWeaponFromHolder()
        {
            const int objectId = 7;
            var prefab = CreatePrefab();
            CreateRegistry(objectId, prefab);
            var holder = Manager.CreateEntity();

            var equip = CreateStateClip(WeaponStateMode.Equip, objectId, holder, Entity.Null);
            RunLifecycle();
            var weapon = Manager.GetComponentData<EquippedWeapon>(holder).Weapon;

            // A Drop clip with no bound weapon must fall back to the holder's equipped instance.
            Manager.SetComponentEnabled<ClipActive>(equip, false);
            CreateStateClip(WeaponStateMode.Drop, objectId, holder, Entity.Null);
            RunLifecycle();

            Assert.IsTrue(Manager.Exists(weapon), "a dropped weapon must survive as a free physics object");
            Assert.IsFalse(Manager.IsComponentEnabled<WeaponAttachment>(weapon), "Drop must detach the resolved weapon");
            Assert.IsFalse(Manager.HasComponent<EquippedWeaponOwner>(weapon),
                "Drop must sever ownership so the reconcile pass never destroys the dropped weapon");
            Assert.IsFalse(Manager.HasComponent<EquippedWeapon>(holder), "Drop must free the holder's equipped slot");
        }

        [Test]
        public void OrphanedWeaponIsDestroyedWhenHolderDies()
        {
            const int objectId = 3;
            var prefab = CreatePrefab();
            CreateRegistry(objectId, prefab);
            var holder = Manager.CreateEntity();

            CreateStateClip(WeaponStateMode.Equip, objectId, holder, Entity.Null);
            RunLifecycle();
            var weapon = Manager.GetComponentData<EquippedWeapon>(holder).Weapon;
            Assert.IsTrue(Manager.Exists(weapon));

            // Holder death strips its normal EquippedWeapon; the reconcile pass must collect the orphaned weapon.
            Manager.DestroyEntity(holder);
            RunLifecycle();

            Assert.IsFalse(Manager.Exists(weapon), "a weapon whose holder died must be destroyed by the reconcile pass");
        }

        private int WeaponCount()
        {
            using var query = Manager.CreateEntityQuery(ComponentType.ReadOnly<EquippedWeaponOwner>());
            return query.CalculateEntityCount();
        }

        private Entity CreatePrefab()
        {
            var prefab = Manager.CreateEntity();
            Manager.AddComponentData(prefab, LocalTransform.Identity);
            return prefab;
        }

        private void CreateRegistry(int objectId, Entity prefab)
        {
            var map = new NativeHashMap<ObjectId, Entity>(1, Allocator.Persistent);
            map.Add(new ObjectId(objectId), prefab);
            registryMaps.Add(map);

            var entity = Manager.CreateEntity();
            Manager.AddComponentData(entity, new ObjectDefinitionRegistry(map));
        }

        private Entity CreateStateClip(WeaponStateMode mode, int objectId, Entity holder, Entity binding)
        {
            var clip = Manager.CreateEntity();
            Manager.AddComponent<ClipActive>(clip);
            Manager.AddComponent<TimelineActive>(clip);
            Manager.AddComponentData(clip, new WeaponStateClipData { Mode = mode, ObjectId = objectId });
            Manager.AddComponentData(clip, new TrackBinding { Value = binding });
            Manager.AddComponentData(clip, new DirectorRoot { Director = holder });
            Manager.AddComponent<WeaponStateFired>(clip);
            Manager.SetComponentEnabled<WeaponStateFired>(clip, false);
            return clip;
        }

        private void RunLifecycle()
        {
            var system = World.CreateSystem<WeaponLifecycleSystem>();
            system.Update(WorldUnmanaged);
            Manager.CompleteAllTrackedJobs();
        }

        private void Update()
        {
            elapsed += DeltaTime;
            World.SetTime(new TimeData(this.elapsed, DeltaTime));

            var system = World.CreateSystem<WeaponAnchorBlendSystem>();
            system.Update(WorldUnmanaged);
            Manager.CompleteAllTrackedJobs();
        }

        private Entity CreateBone(float3 position)
        {
            var bone = Manager.CreateEntity();
            Manager.AddComponentData(bone, LocalTransform.FromPosition(position));
            return bone;
        }

        private Entity CreateWeapon(float3 position)
        {
            var weapon = Manager.CreateEntity();
            Manager.AddBuffer<WeaponAnchorSample>(weapon);
            Manager.AddComponentData(weapon, LocalTransform.FromPosition(position));
            Manager.AddComponent<WeaponAnchorRest>(weapon);
            return weapon;
        }

        private void Attach(Entity weapon, Entity bone, float3 localOffset)
        {
            Manager.AddComponentData(weapon, new WeaponAttachment { Holder = bone });
            Manager.AddComponentData(weapon, new WeaponAttachmentAnchor
            {
                Bone = bone,
                LocalPosition = localOffset,
                LocalRotation = quaternion.identity
            });
        }
    }
}
#endif
