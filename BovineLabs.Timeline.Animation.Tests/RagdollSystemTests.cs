using BovineLabs.Testing;
using NUnit.Framework;
using Rukhanka;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Transforms;

namespace BovineLabs.Timeline.Animation.Tests
{
    public class RagdollSystemTests : ECSTestsFixture
    {
        [Test]
        public void EnterSnapsBodyToBonePoseAndGoesDynamic()
        {
            var boneRot = quaternion.RotateY(math.radians(90f));
            var bonePos = new float3(5f, 0f, 3f);
            var bone = CreateBone(bonePos, boneRot);

            var rig = CreateRig(active: true);
            var localPos = new float3(1f, 0f, 0f);
            var localRot = quaternion.RotateZ(math.radians(30f));
            var body = CreateBody(rig, bone, localPos, localRot, LocalTransform.FromPosition(new float3(-99f, -99f, -99f)));

            RunSystem();

            var transform = Manager.GetComponentData<LocalTransform>(body);
            var expectedPos = bonePos + math.mul(boneRot, localPos);
            var expectedRot = math.mul(boneRot, localRot);
            Assert.IsTrue(math.all(math.abs(transform.Position - expectedPos) < 1e-4f),
                "the body must snap to bone.LTW composed with its bone-local offset on the activation edge");
            Assert.IsTrue(math.abs(math.dot(transform.Rotation, expectedRot)) > 1f - 1e-4f,
                "the body rotation must snap to bone.LTW rotation composed with its bone-local rotation");
            Assert.AreEqual(1f, transform.Scale, 1e-5f, "the snapped body scale must be reset to 1");

            Assert.AreEqual(0, Manager.GetComponentData<PhysicsMassOverride>(body).IsKinematic,
                "the body must be made dynamic on the activation edge");
            Assert.IsFalse(Manager.HasComponent<Disabled>(body),
                "the body must rejoin the physics world (Disabled removed) on the activation edge");
            Assert.IsTrue(Manager.IsComponentEnabled<OverrideTransformIKComponent>(bone),
                "the bone IK must be enabled so the visual bone follows the body");
            Assert.IsTrue(Manager.GetComponentData<RagdollBodyState>(body).Fired,
                "the per-body edge latch must be set after entering the ragdoll");
        }

        [Test]
        public void JointResolvesBodyFromEntityB()
        {
            var bone = CreateBone(float3.zero, quaternion.identity);
            var rig = CreateRig(active: true);
            var body = CreateBody(rig, bone, float3.zero, quaternion.identity, LocalTransform.Identity);

            // The ragdoll body sits in EntityB (not EntityA) of the constrained pair — the joint must still resolve
            // it and drop Disabled while the rig is ragdolling.
            var joint = Manager.CreateEntity();
            Manager.AddComponentData(joint, new PhysicsConstrainedBodyPair(Entity.Null, body, false));
            Manager.AddComponent<Disabled>(joint);

            RunSystem();

            Assert.IsFalse(Manager.HasComponent<Disabled>(joint),
                "a joint whose ragdoll body is in EntityB must have Disabled removed while the rig is active");
        }

        private Entity CreateRig(bool active)
        {
            var rig = Manager.CreateEntity(typeof(ActiveRagdoll));
            Manager.SetComponentEnabled<ActiveRagdoll>(rig, active);
            return rig;
        }

        private Entity CreateBone(float3 position, quaternion rotation)
        {
            var bone = Manager.CreateEntity();
            Manager.AddComponentData(bone, new LocalToWorld { Value = float4x4.TRS(position, rotation, new float3(1f)) });
            Manager.AddComponentData(bone, new OverrideTransformIKComponent { positionWeight = 1f, rotationWeight = 1f });
            Manager.SetComponentEnabled<OverrideTransformIKComponent>(bone, false);
            return bone;
        }

        private Entity CreateBody(Entity rig, Entity bone, float3 boneLocalPos, quaternion boneLocalRot,
            LocalTransform baked)
        {
            var body = Manager.CreateEntity();
            Manager.AddComponentData(body, new RagdollBody
            {
                RigRoot = rig,
                Bone = bone,
                BoneLocalPos = boneLocalPos,
                BoneLocalRot = boneLocalRot,
            });
            Manager.AddComponentData(body, new RagdollBodyState { Fired = false });
            Manager.AddComponentData(body, baked);
            Manager.AddComponentData(body, new PhysicsMassOverride { IsKinematic = 1, SetVelocityToZero = 1 });
            Manager.AddComponentData(body, default(PhysicsVelocity));
            Manager.AddComponent<Disabled>(body);
            return body;
        }

        private void RunSystem()
        {
            // RagdollApplySystem writes its enter/exit structural changes into the EndFixedStepSimulation ECB
            // singleton; update that system afterwards to play the buffer back, mirroring the real frame order.
            var ecbSystem = World.GetOrCreateSystemManaged<EndFixedStepSimulationEntityCommandBufferSystem>();
            World.GetOrCreateSystem<RagdollApplySystem>().Update(WorldUnmanaged);
            Manager.CompleteAllTrackedJobs();
            ecbSystem.Update();
            Manager.CompleteAllTrackedJobs();
        }
    }
}
