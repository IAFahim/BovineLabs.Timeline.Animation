using NUnit.Framework;
using Rukhanka;
using Unity.Mathematics;
using Hash128 = Unity.Entities.Hash128;

namespace BovineLabs.Timeline.Animation.Tests
{
    [TestFixture]
    public class FallbackOverrideResolveTests
    {
        private static Hash128 ClipHash => new Hash128(1u, 2u, 3u, 4u);

        private static Hash128 MaskHash => new Hash128(5u, 6u, 7u, 8u);

        private static TrackFallbackOverride Override()
        {
            return new TrackFallbackOverride
            {
                FallbackClipHash = ClipHash,
                BlendInSpeed = 1.5f,
                BlendOutSpeed = 2.5f,
                PlaybackMode = FallbackPlaybackMode.Clamp,
                LayerIndex = 3,
                BlendMode = AnimationBlendingMode.Additive,
                AvatarMaskHash = MaskHash,
                PositionOffset = new float3(1f, 2f, 3f),
                RotationOffset = quaternion.Euler(0.1f, 0.2f, 0.3f),
                RemoveStartOffset = true,
                ApplyFootIK = true,
            };
        }

        private static FallbackBlend Blend()
        {
            var o = Override();
            return new FallbackBlend
            {
                ClipHash = o.FallbackClipHash,
                BlendInSpeed = o.BlendInSpeed,
                BlendOutSpeed = o.BlendOutSpeed,
                PlaybackMode = o.PlaybackMode,
                LayerIndex = o.LayerIndex,
                BlendMode = o.BlendMode,
                AvatarMaskHash = o.AvatarMaskHash,
                PositionOffset = o.PositionOffset,
                RotationOffset = o.RotationOffset,
                RemoveStartOffset = o.RemoveStartOffset,
                ApplyFootIK = o.ApplyFootIK,
            };
        }

        [Test]
        public void DominantOverride_HigherLayerWins()
        {
            var lower = Override();
            lower.LayerIndex = 1;

            var higher = Override();
            higher.LayerIndex = 2;

            Assert.IsTrue(FallbackOverrideResolve.DominantOverride(higher, lower));
            Assert.IsFalse(FallbackOverrideResolve.DominantOverride(lower, higher));
        }

        [Test]
        public void DominantOverride_EqualLayer_HigherHashWins()
        {
            var lower = Override();
            lower.LayerIndex = 4;
            lower.FallbackClipHash = new Hash128(0u, 0u, 0u, 1u);

            var higher = Override();
            higher.LayerIndex = 4;
            higher.FallbackClipHash = new Hash128(0u, 0u, 0u, 9u);

            Assert.IsTrue(FallbackOverrideResolve.DominantOverride(higher, lower));
            Assert.IsFalse(FallbackOverrideResolve.DominantOverride(lower, higher));
        }

        [Test]
        public void DominantOverride_EqualLayerEqualHash_ReturnsFalse()
        {
            var a = Override();
            var b = Override();

            Assert.IsFalse(FallbackOverrideResolve.DominantOverride(a, b));
            Assert.IsFalse(FallbackOverrideResolve.DominantOverride(b, a));
        }

        [Test]
        public void Matches_AllTwelveFieldsEqual_ReturnsTrue()
        {
            Assert.IsTrue(FallbackOverrideResolve.Matches(Blend(), Override()));
        }

        [Test]
        public void Matches_ClipHashDiffers_ReturnsFalse()
        {
            var o = Override();
            o.FallbackClipHash = new Hash128(9u, 9u, 9u, 9u);
            Assert.IsFalse(FallbackOverrideResolve.Matches(Blend(), o));
        }

        [Test]
        public void Matches_BlendInSpeedDiffers_ReturnsFalse()
        {
            var o = Override();
            o.BlendInSpeed = 99f;
            Assert.IsFalse(FallbackOverrideResolve.Matches(Blend(), o));
        }

        [Test]
        public void Matches_BlendOutSpeedDiffers_ReturnsFalse()
        {
            var o = Override();
            o.BlendOutSpeed = 99f;
            Assert.IsFalse(FallbackOverrideResolve.Matches(Blend(), o));
        }

        [Test]
        public void Matches_PlaybackModeDiffers_ReturnsFalse()
        {
            var o = Override();
            o.PlaybackMode = FallbackPlaybackMode.Hold;
            Assert.IsFalse(FallbackOverrideResolve.Matches(Blend(), o));
        }

        [Test]
        public void Matches_LayerIndexDiffers_ReturnsFalse()
        {
            var o = Override();
            o.LayerIndex = 99;
            Assert.IsFalse(FallbackOverrideResolve.Matches(Blend(), o));
        }

        [Test]
        public void Matches_BlendModeDiffers_ReturnsFalse()
        {
            var o = Override();
            o.BlendMode = AnimationBlendingMode.Override;
            Assert.IsFalse(FallbackOverrideResolve.Matches(Blend(), o));
        }

        [Test]
        public void Matches_AvatarMaskHashDiffers_ReturnsFalse()
        {
            var o = Override();
            o.AvatarMaskHash = new Hash128(9u, 9u, 9u, 9u);
            Assert.IsFalse(FallbackOverrideResolve.Matches(Blend(), o));
        }

        [Test]
        public void Matches_PositionOffsetDiffers_ReturnsFalse()
        {
            var o = Override();
            o.PositionOffset = new float3(9f, 9f, 9f);
            Assert.IsFalse(FallbackOverrideResolve.Matches(Blend(), o));
        }

        [Test]
        public void Matches_RotationOffsetDiffers_ReturnsFalse()
        {
            var o = Override();
            o.RotationOffset = quaternion.Euler(1f, 1f, 1f);
            Assert.IsFalse(FallbackOverrideResolve.Matches(Blend(), o));
        }

        [Test]
        public void Matches_RemoveStartOffsetDiffers_ReturnsFalse()
        {
            var o = Override();
            o.RemoveStartOffset = false;
            Assert.IsFalse(FallbackOverrideResolve.Matches(Blend(), o));
        }

        [Test]
        public void Matches_ApplyFootIKDiffers_ReturnsFalse()
        {
            var o = Override();
            o.ApplyFootIK = false;
            Assert.IsFalse(FallbackOverrideResolve.Matches(Blend(), o));
        }

        [Test]
        public void Matches_FallbackVsFallback_AllFieldsEqual_ReturnsTrue()
        {
            Assert.IsTrue(FallbackOverrideResolve.Matches(Blend(), Blend()));
        }

        [Test]
        public void Matches_FallbackVsFallback_ClipHashDiffers_ReturnsFalse()
        {
            var d = Blend();
            d.ClipHash = new Hash128(9u, 9u, 9u, 9u);
            Assert.IsFalse(FallbackOverrideResolve.Matches(Blend(), d));
        }

        [Test]
        public void Matches_FallbackVsTrackOverride_MapsFieldsCorrectly()
        {
            var f = Blend();
            var o = Override();

            Assert.AreEqual(f.ClipHash, o.FallbackClipHash);
            Assert.IsTrue(FallbackOverrideResolve.Matches(in f, in o));

            o.FallbackClipHash = new Hash128(0u, 0u, 0u, 123u);
            Assert.AreNotEqual(f.ClipHash, o.FallbackClipHash);
            Assert.IsFalse(FallbackOverrideResolve.Matches(in f, in o));
        }
    }
}
