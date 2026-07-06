using NUnit.Framework;
using Unity.Mathematics;

namespace BovineLabs.Timeline.Animation.Tests
{
    [TestFixture]
    public class InertializationQuinticTests
    {
        private const float Eps = 1e-4f;

        [Test]
        public void Quintic_AtZero_ReturnsX0()
        {
            Assert.AreEqual(1.25f, InertializationMath.Quintic(1.25f, 0f, 0f, 0f, 0.2f), Eps);
            Assert.AreEqual(1.25f, InertializationMath.Quintic(1.25f, 3f, -7f, 0f, 0.2f), Eps);
        }

        [Test]
        public void Quintic_AtOrPastDuration_ReturnsZero()
        {
            Assert.AreEqual(0f, InertializationMath.Quintic(1f, 2f, 3f, 0.2f, 0.2f), Eps);
            Assert.AreEqual(0f, InertializationMath.Quintic(1f, 2f, 3f, 0.5f, 0.2f), Eps);
        }

        [Test]
        public void Quintic_ZeroDuration_ReturnsZero()
        {
            Assert.AreEqual(0f, InertializationMath.Quintic(1f, 2f, 3f, 0f, 0f), Eps);
        }

        [Test]
        public void Quintic_OvershootGuard_ClosesEarlyWhenOffsetAlreadyClosing()
        {
            // guard = -5*x0/v0 = -5*0.01/-1 = 0.05 < duration(0.2) -> effective window shortened to 0.05.
            Assert.AreEqual(0f, InertializationMath.Quintic(0.01f, -1f, 0f, 0.05f, 0.2f), Eps);
            Assert.AreEqual(0f, InertializationMath.Quintic(0.01f, -1f, 0f, 0.08f, 0.2f), Eps);
            Assert.AreNotEqual(0f, InertializationMath.Quintic(0.01f, -1f, 0f, 0.02f, 0.2f));
        }

        [Test]
        public void Quintic_PureOffsetDecay_StaysBoundedAndMonotone()
        {
            const float duration = 0.2f;
            var prev = 1f;
            for (var i = 0; i <= 40; i++)
            {
                var t = duration * i / 40f;
                var v = InertializationMath.Quintic(1f, 0f, 0f, t, duration);

                Assert.LessOrEqual(v, 1f + Eps, "must not overshoot above x0");
                Assert.GreaterOrEqual(v, -Eps, "must not undershoot below 0");
                Assert.LessOrEqual(v, prev + Eps, "pure-offset decay must be monotone non-increasing");
                prev = v;
            }
        }

        [Test]
        public void Quintic_OverdampedInput_StaysBounded()
        {
            const float duration = 0.15f;
            const float x0 = 0.4f;
            const float v0 = 6f;
            const float a0 = 40f;
            var bound = math.abs(x0) + math.abs(v0) * duration + math.abs(a0) * duration * duration;

            for (var i = 0; i <= 60; i++)
            {
                var t = duration * i / 60f;
                var v = InertializationMath.Quintic(x0, v0, a0, t, duration);
                Assert.LessOrEqual(math.abs(v), bound, "quintic must not blow up within its window");
            }

            Assert.AreEqual(0f, InertializationMath.Quintic(x0, v0, a0, duration, duration), Eps);
        }
    }

    [TestFixture]
    public class InertializationAngleAxisTests
    {
        private const float Eps = 1e-3f;

        [Test]
        public void ToAngleAxis_RoundTrips_ForGeneralRotation()
        {
            AssertRoundTrip(math.normalize(new float3(1f, 2f, 3f)), 1.2f);
            AssertRoundTrip(math.normalize(new float3(-2f, 0.5f, 1f)), 2.7f);
        }

        [Test]
        public void ToAngleAxis_RoundTrips_NearIdentity()
        {
            var q = quaternion.AxisAngle(math.normalize(new float3(1f, 2f, 3f)), 1e-5f);
            InertializationMath.ToAngleAxis(q, out var axis, out var angle);

            Assert.Less(angle, 1e-3f);
            AssertSameRotation(q, quaternion.AxisAngle(axis, angle));
        }

        [Test]
        public void ToAngleAxis_RoundTrips_ForNegativeW()
        {
            // angle 1.5*pi -> w = cos(0.75*pi) < 0; extraction must reduce to the shortest-arc equivalent.
            var axisIn = math.normalize(new float3(0.3f, -1f, 0.6f));
            var q = quaternion.AxisAngle(axisIn, 1.5f * math.PI);
            Assert.Less(q.value.w, 0f);

            InertializationMath.ToAngleAxis(q, out var axis, out var angle);
            AssertSameRotation(q, quaternion.AxisAngle(axis, angle));
        }

        private static void AssertRoundTrip(float3 axisIn, float angleIn)
        {
            var q = quaternion.AxisAngle(axisIn, angleIn);
            InertializationMath.ToAngleAxis(q, out var axis, out var angle);
            AssertSameRotation(q, quaternion.AxisAngle(axis, angle));
        }

        private static void AssertSameRotation(quaternion a, quaternion b)
        {
            var v = new float3(0.7f, -0.4f, 0.55f);
            var ra = math.mul(a, v);
            var rb = math.mul(b, v);
            Assert.AreEqual(ra.x, rb.x, Eps);
            Assert.AreEqual(ra.y, rb.y, Eps);
            Assert.AreEqual(ra.z, rb.z, Eps);
        }
    }

    [TestFixture]
    public class InertializationPhaseJumpTests
    {
        [Test]
        public void PhaseJump_MonotoneAdvanceWithDtJitter_DoesNotTrigger()
        {
            const float clipLen = 0.3f;
            const float nominalDt = 1f / 60f;

            var rng = new Random(0x1234567u);
            var prevTime = 0f;
            var lastTime = math.frac(nominalDt / clipLen); // one nominal step in
            var prevStep = nominalDt / clipLen;

            for (var frame = 0; frame < 400; frame++)
            {
                var dt = nominalDt * rng.NextFloat(0.5f, 1.5f); // +/-50% jitter
                var step = dt / clipLen;
                var time = math.frac(lastTime + step);

                Assert.IsFalse(
                    InertializationMath.IsPhaseJump(time, lastTime, prevTime, clipLen),
                    $"frame {frame}: monotone advance under dt jitter must not read as a phase jump");

                prevTime = lastTime;
                lastTime = time;
                prevStep = step;
            }

            Assert.Greater(prevStep, 0f);
        }

        [Test]
        public void PhaseJump_GenuineTimeReset_Triggers()
        {
            const float clipLen = 0.3f;
            // steady 0.06/frame advance, then a hard cut back to 0 (a clip change / rewind seam).
            Assert.IsTrue(InertializationMath.IsPhaseJump(0f, 0.5f, 0.44f, clipLen));
        }

        [Test]
        public void PhaseJump_CleanFullCycleWrap_DoesNotTrigger()
        {
            const float clipLen = 0.3f;
            // last=0.97, prev=0.91 (step 0.06); a clean wrap lands on frac(0.97+0.06)=0.03.
            Assert.IsFalse(InertializationMath.IsPhaseJump(0.03f, 0.97f, 0.91f, clipLen));
        }
    }
}
