#if !BL_DISABLE_OBJECT_DEFINITION
using BovineLabs.Testing;
using NUnit.Framework;
using Unity.Core;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace BovineLabs.Timeline.Animation.Tests
{
    public class WeaponLifecycleSystemTests : ECSTestsFixture
    {
        private const float DeltaTime = 1f / 60f;

        private double elapsed;

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
