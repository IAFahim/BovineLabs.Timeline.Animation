using NUnit.Framework;

namespace BovineLabs.Timeline.Animation.Tests
{
    public class BlendTreePhaseMathTests
    {
        private const float FrameTime = 1f / 60f;

        [Test]
        public void NormalForwardStep_UsesLocalDelta()
        {
            Assert.AreEqual(FrameTime, BlendTreePhaseMath.PlayingDelta(FrameTime, 99f));
        }

        [Test]
        public void OneSecondLoopWrap_AdvancesByFrameTime()
        {
            // A looping 1s state timeline wraps with delta ≈ -0.983s — must not rewind the cycle.
            Assert.AreEqual(FrameTime, BlendTreePhaseMath.PlayingDelta(-0.983f, FrameTime));
        }

        [Test]
        public void HeldClip_AdvancesByFrameTime()
        {
            Assert.AreEqual(FrameTime, BlendTreePhaseMath.PlayingDelta(0f, FrameTime));
        }

        [Test]
        public void LargeForwardSeek_AdvancesByFrameTime()
        {
            Assert.AreEqual(FrameTime, BlendTreePhaseMath.PlayingDelta(5f, FrameTime));
        }

        [Test]
        public void ExactThresholdDelta_StillLocal()
        {
            Assert.AreEqual(BlendTreePhaseMath.MaxLocalDelta,
                BlendTreePhaseMath.PlayingDelta(BlendTreePhaseMath.MaxLocalDelta, FrameTime));
        }

        [Test]
        public void NegativeScaledDeltaTime_ClampsToZero()
        {
            Assert.AreEqual(0f, BlendTreePhaseMath.PlayingDelta(-0.5f, -FrameTime));
        }
    }
}
