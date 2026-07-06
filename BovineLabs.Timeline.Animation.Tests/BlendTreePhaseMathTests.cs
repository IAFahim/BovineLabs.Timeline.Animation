using NUnit.Framework;
using Unity.Mathematics;

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
        public void OneSecondLoopWrap_AdvancesByScaledDelta()
        {
            // A looping 1s state timeline wraps with delta ≈ -0.983s — far beyond a plausible frame step, so it
            // falls back to the signed scaled frame time and the cycle keeps moving forward, not rewinding a second.
            Assert.AreEqual(FrameTime, BlendTreePhaseMath.PlayingDelta(-0.983f, FrameTime));
        }

        [Test]
        public void HeldClip_AdvancesByScaledDelta()
        {
            Assert.AreEqual(FrameTime, BlendTreePhaseMath.PlayingDelta(0f, FrameTime));
        }

        [Test]
        public void LargeForwardSeek_AdvancesByScaledDelta()
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
        public void ReverseFrameStep_WithinRange_IsHonored()
        {
            // Reverse playback (director.time -= dt) with a positive world dt: the local delta is a small negative
            // step within range and must be honored verbatim so the phase cycles backward.
            Assert.AreEqual(-FrameTime, BlendTreePhaseMath.PlayingDelta(-FrameTime, FrameTime));
        }

        [Test]
        public void LargeNegativeSeek_FallsBackToSignedScaledDelta()
        {
            // A big negative jump (reverse loop wrap / seek) is not a frame step: fall back to the SIGNED scaled dt,
            // which under reverse playback is itself negative — so the cycle still steps backward, no zero clamp.
            Assert.AreEqual(-FrameTime, BlendTreePhaseMath.PlayingDelta(-0.983f, -FrameTime));
        }

        [Test]
        public void NegativeScaledDelta_IsNotClampedToZero()
        {
            // The old contract clamped the fallback at 0 (freezing reverse play). It must now pass the sign through.
            Assert.AreEqual(-FrameTime, BlendTreePhaseMath.PlayingDelta(5f, -FrameTime));
        }

        [Test]
        public void FracOfNegativeAccumulatedPhase_StaysInUnitRange()
        {
            // Reverse accumulation drives the phase negative; math.frac must still yield a value in [0,1).
            var frac = math.frac(-0.25f);
            Assert.Greater(frac, 0f);
            Assert.Less(frac, 1f);
            Assert.AreEqual(0.75f, frac, 1e-5f);
        }
    }
}
