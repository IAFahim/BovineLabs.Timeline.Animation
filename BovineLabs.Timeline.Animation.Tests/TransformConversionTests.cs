using NUnit.Framework;
using Unity.Mathematics;

namespace BovineLabs.Timeline.Animation.Tests
{
    [TestFixture]
    public class TransformConversionTests
    {
        [Test]
        public void WorldToParentLocal_IdentityParent_ReturnsWorldUnchanged()
        {
            var worldPos = new float3(3f, -4f, 5f);
            var worldRot = quaternion.EulerXYZ(0.3f, -0.7f, 1.1f);

            var ok = TransformConversion.WorldToParentLocal(float4x4.identity, worldPos, worldRot,
                out var localPos, out var localRot);

            Assert.IsTrue(ok);
            Assert.That(math.distance(localPos, worldPos), Is.LessThan(1e-5f));
            Assert.That(math.abs(math.dot(localRot, worldRot)), Is.GreaterThan(1f - 1e-5f));
        }

        [Test]
        public void WorldToParentLocal_TranslatedRotatedParent_RoundTrips()
        {
            var parentRot = quaternion.EulerXYZ(0.5f, 1.2f, -0.3f);
            var parentL2W = float4x4.TRS(new float3(10f, -2f, 4f), parentRot, new float3(1f));
            var worldPos = new float3(7f, 1f, -3f);
            var worldRot = quaternion.EulerXYZ(-0.2f, 0.9f, 0.4f);

            var ok = TransformConversion.WorldToParentLocal(parentL2W, worldPos, worldRot,
                out var localPos, out var localRot);

            Assert.IsTrue(ok);

            var backPos = math.transform(parentL2W, localPos);
            var backRot = math.mul(parentRot, localRot);
            Assert.That(math.distance(backPos, worldPos), Is.LessThan(1e-5f));
            Assert.That(math.abs(math.dot(backRot, worldRot)), Is.GreaterThan(1f - 1e-5f));
        }

        [Test]
        public void WorldToParentLocal_DegenerateParent_ReturnsFalse()
        {
            var worldPos = new float3(1f, 2f, 3f);
            var worldRot = quaternion.EulerXYZ(0.1f, 0.2f, 0.3f);

            var ok = TransformConversion.WorldToParentLocal(default, worldPos, worldRot,
                out var localPos, out var localRot);

            Assert.IsFalse(ok);
            Assert.That(math.distance(localPos, worldPos), Is.LessThan(1e-6f));
            Assert.That(math.abs(math.dot(localRot, worldRot)), Is.GreaterThan(1f - 1e-6f));
        }

        [Test]
        public void WorldPositionToParentLocal_DegenerateParent_ReturnsFalse()
        {
            var worldPos = new float3(4f, 5f, 6f);

            var ok = TransformConversion.WorldPositionToParentLocal(default, worldPos,
                out var localPos);

            Assert.IsFalse(ok);
            Assert.That(math.distance(localPos, worldPos), Is.LessThan(1e-6f));
        }

        [Test]
        public void WorldPositionToParentLocal_ValidParent_InvertsPosition()
        {
            var parentL2W = float4x4.TRS(new float3(2f, 3f, -1f),
                quaternion.EulerXYZ(0.4f, -0.6f, 0.8f), new float3(1f));
            var worldPos = new float3(9f, -2f, 5f);

            var ok = TransformConversion.WorldPositionToParentLocal(parentL2W, worldPos,
                out var localPos);

            Assert.IsTrue(ok);
            var backPos = math.transform(parentL2W, localPos);
            Assert.That(math.distance(backPos, worldPos), Is.LessThan(1e-5f));
        }
    }
}
