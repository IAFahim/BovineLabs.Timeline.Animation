using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine.Timeline;

namespace BovineLabs.Timeline.Animation.Tests
{
    public class ClipSamplingTests
    {
        private const float Duration = 2f;
        private const float Tolerance = 1e-5f;

        [Test]
        public void NormalizedClipTime_None_SaturatesZeroToOne()
        {
            Assert.AreEqual(0f, ClipSampling.NormalizedClipTime(0f, Duration, TimelineClip.ClipExtrapolation.None, false), Tolerance);
            Assert.AreEqual(1f, ClipSampling.NormalizedClipTime(Duration, Duration, TimelineClip.ClipExtrapolation.None, false), Tolerance);
            Assert.AreEqual(0.5f, ClipSampling.NormalizedClipTime(0.5f * Duration, Duration, TimelineClip.ClipExtrapolation.None, false), Tolerance);
            Assert.AreEqual(1f, ClipSampling.NormalizedClipTime(2f * Duration, Duration, TimelineClip.ClipExtrapolation.None, false), Tolerance);
        }

        [Test]
        public void NormalizedClipTime_Loop_WrapsViaFrac()
        {
            Assert.AreEqual(0.25f, ClipSampling.NormalizedClipTime(1.25f * Duration, Duration, TimelineClip.ClipExtrapolation.Loop, false), Tolerance);
        }

        [Test]
        public void NormalizedClipTime_Looped_TreatedAsLoop()
        {
            Assert.AreEqual(0.25f, ClipSampling.NormalizedClipTime(1.25f * Duration, Duration, TimelineClip.ClipExtrapolation.None, true), Tolerance);
        }

        [Test]
        public void NormalizedClipTime_PingPong_Triangle()
        {
            Assert.AreEqual(0f, ClipSampling.NormalizedClipTime(0f, Duration, TimelineClip.ClipExtrapolation.PingPong, false), Tolerance);
            Assert.AreEqual(1f, ClipSampling.NormalizedClipTime(Duration, Duration, TimelineClip.ClipExtrapolation.PingPong, false), Tolerance);
            Assert.AreEqual(0.5f, ClipSampling.NormalizedClipTime(1.5f * Duration, Duration, TimelineClip.ClipExtrapolation.PingPong, false), Tolerance);
            Assert.AreEqual(0f, ClipSampling.NormalizedClipTime(2f * Duration, Duration, TimelineClip.ClipExtrapolation.PingPong, false), Tolerance);
        }

        [Test]
        public void ComposeTrackClipOffset_IdentityTrack_ReturnsClip()
        {
            var clipPos = new float3(1f, 2f, 3f);
            var clipRot = quaternion.Euler(0.3f, 0.4f, 0.5f);

            ClipSampling.ComposeTrackClipOffset(float3.zero, quaternion.identity, clipPos, clipRot, out var pos, out var rot);

            Assert.AreEqual(clipPos.x, pos.x, Tolerance);
            Assert.AreEqual(clipPos.y, pos.y, Tolerance);
            Assert.AreEqual(clipPos.z, pos.z, Tolerance);
            Assert.AreEqual(clipRot.value.x, rot.value.x, Tolerance);
            Assert.AreEqual(clipRot.value.y, rot.value.y, Tolerance);
            Assert.AreEqual(clipRot.value.z, rot.value.z, Tolerance);
            Assert.AreEqual(clipRot.value.w, rot.value.w, Tolerance);
        }

        [Test]
        public void ComposeTrackClipOffset_AppliesTrackRotationThenTranslation()
        {
            var trackPos = new float3(10f, -5f, 2f);
            var trackRot = quaternion.Euler(0.1f, 0.2f, 0.3f);
            var clipPos = new float3(1f, 0f, 0f);
            var clipRot = quaternion.Euler(0.4f, 0.5f, 0.6f);

            ClipSampling.ComposeTrackClipOffset(trackPos, trackRot, clipPos, clipRot, out var pos, out var rot);

            var expectedPos = trackPos + math.rotate(trackRot, clipPos);
            var expectedRot = math.mul(trackRot, clipRot);

            Assert.AreEqual(expectedPos.x, pos.x, Tolerance);
            Assert.AreEqual(expectedPos.y, pos.y, Tolerance);
            Assert.AreEqual(expectedPos.z, pos.z, Tolerance);
            Assert.AreEqual(expectedRot.value.x, rot.value.x, Tolerance);
            Assert.AreEqual(expectedRot.value.y, rot.value.y, Tolerance);
            Assert.AreEqual(expectedRot.value.z, rot.value.z, Tolerance);
            Assert.AreEqual(expectedRot.value.w, rot.value.w, Tolerance);
        }
    }
}
