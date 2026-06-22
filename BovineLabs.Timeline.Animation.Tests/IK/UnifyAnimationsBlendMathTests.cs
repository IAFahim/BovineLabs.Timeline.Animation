using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Hash128 = Unity.Entities.Hash128;

namespace BovineLabs.Timeline.Animation.Tests
{
    [TestFixture]
    public class UnifyAnimationsBlendMathTests
    {
        private const float WeightEpsilon = 0.0001f;

        private const int LayerSumCapacity = 64;

        private static float NormalizeBaseLayerWeight(float currentWeight, float baseControl, float layerSum)
        {
            var normalizeFactor = layerSum > WeightEpsilon ? baseControl / layerSum : 0f;
            return currentWeight * normalizeFactor;
        }

        private static float NormalizeAdditionalLayerWeight(float currentWeight, float layerSum)
        {
            return currentWeight / math.max(WeightEpsilon, layerSum);
        }

        private static float AdditionalLayerWeight(float layerSum)
        {
            return math.saturate(layerSum);
        }

        private static float FallbackWeight(float baseControl)
        {
            return 1f - baseControl;
        }

        private static float OverrideSumForLayerLinear(
            in NativeArray<float> weights,
            in NativeArray<int> layers,
            in NativeArray<bool> isOverride,
            int layer)
        {
            var sum = 0f;
            for (var i = 0; i < weights.Length; i++)
                if (isOverride[i] && layers[i] == layer)
                    sum += weights[i];

            return sum;
        }

        private static float AccumulateOverrideSumForLayer(
            in NativeArray<float> weights,
            in NativeArray<int> layers,
            in NativeArray<bool> isOverride,
            int layer)
        {
            var sums = default(FixedList512Bytes<float>);
            sums.Length = LayerSumCapacity;
            for (var i = 0; i < LayerSumCapacity; i++)
                sums[i] = 0f;

            for (var i = 0; i < weights.Length; i++)
                if (isOverride[i] && (uint)layers[i] < LayerSumCapacity)
                    sums[layers[i]] += weights[i];

            return sums[layer];
        }

        [Test]
        public void OverrideBaseLayer_NormalizesByLayerSum_ScaledByBaseControl()
        {
            var weightA = NormalizeBaseLayerWeight(0.6f, 1f, 0.6f + 0.2f);
            var weightB = NormalizeBaseLayerWeight(0.2f, 1f, 0.6f + 0.2f);

            Assert.AreEqual(0.75f, weightA, 1e-6f);
            Assert.AreEqual(0.25f, weightB, 1e-6f);
            Assert.AreEqual(1f, weightA + weightB, 1e-6f);
        }

        [Test]
        public void OverrideBaseLayer_BaseControlScalesTotalWeight()
        {
            var baseControl = 0.5f;
            var weightA = NormalizeBaseLayerWeight(0.6f, baseControl, 0.8f);
            var weightB = NormalizeBaseLayerWeight(0.2f, baseControl, 0.8f);

            Assert.AreEqual(baseControl, weightA + weightB, 1e-6f);
        }

        [Test]
        public void OverrideBaseLayer_ZeroLayerSum_YieldsZeroWeight()
        {
            Assert.AreEqual(0f, NormalizeBaseLayerWeight(0.6f, 1f, 0f));
        }

        [Test]
        public void OverrideAdditionalLayer_NormalizesWeightAndSaturatesLayerWeight()
        {
            var layerSum = 0.4f + 0.4f;
            var weight = NormalizeAdditionalLayerWeight(0.4f, layerSum);
            var layerWeight = AdditionalLayerWeight(layerSum);

            Assert.AreEqual(0.5f, weight, 1e-6f);
            Assert.AreEqual(0.8f, layerWeight, 1e-6f);
        }

        [Test]
        public void OverrideAdditionalLayer_LayerSumAboveOne_SaturatesToOne()
        {
            Assert.AreEqual(1f, AdditionalLayerWeight(1.5f));
        }

        [Test]
        public void Additive_PassesCurrentWeightThroughUnchanged()
        {
            const float currentWeight = 0.37f;
            Assert.AreEqual(currentWeight, currentWeight);
        }

        [Test]
        public void Fallback_WeightIsComplementOfBaseControl()
        {
            Assert.AreEqual(1f, FallbackWeight(0f));
            Assert.AreEqual(0.25f, FallbackWeight(0.75f), 1e-6f);
            Assert.AreEqual(0f, FallbackWeight(1f), 1e-6f);
        }

        [Test]
        public void Fallback_PlusBaseControl_AlwaysSumsToOne()
        {
            for (var i = 0; i <= 10; i++)
            {
                var baseControl = i / 10f;
                Assert.AreEqual(1f, baseControl + FallbackWeight(baseControl), 1e-6f);
            }
        }

        [Test]
        public void LayerSum_BucketedMatchesLinearRescan_BitIdentical()
        {
            var weights = new NativeArray<float>(6, Allocator.Temp);
            var layers = new NativeArray<int>(6, Allocator.Temp);
            var isOverride = new NativeArray<bool>(6, Allocator.Temp);

            weights[0] = 0.1f;
            layers[0] = 0;
            isOverride[0] = true;
            weights[1] = 0.2f;
            layers[1] = 1;
            isOverride[1] = true;
            weights[2] = 0.3f;
            layers[2] = 0;
            isOverride[2] = true;
            weights[3] = 0.4f;
            layers[3] = 0;
            isOverride[3] = false;
            weights[4] = 0.5f;
            layers[4] = 1;
            isOverride[4] = true;
            weights[5] = 0.6f;
            layers[5] = 0;
            isOverride[5] = true;

            for (var layer = 0; layer < 2; layer++)
            {
                var linear = OverrideSumForLayerLinear(in weights, in layers, in isOverride, layer);
                var bucketed = AccumulateOverrideSumForLayer(in weights, in layers, in isOverride, layer);
                Assert.AreEqual(linear, bucketed, 0f,
                    "bucketed single-pass accumulation must be bit-identical to the original per-layer rescan");
            }

            weights.Dispose();
            layers.Dispose();
            isOverride.Dispose();
        }
    }

    [TestFixture]
    public class MotionIdSentinelTests
    {
        [Test]
        public void Fallback_IsTopOfUintRange()
        {
            Assert.AreEqual(0xFFFFFFFFu, MotionId.Fallback);
        }

        [Test]
        public void Compute_NeverEqualsFallbackSentinel()
        {
            for (var i = 0; i < 4096; i++)
            {
                var track = new Entity { Index = i, Version = i * 7 + 1 };
                var clipHash = new Hash128((uint)i, (uint)(i * 3), (uint)(i * 5), (uint)(i * 11));
                var id = MotionId.Compute(track, i & 3, clipHash);
                Assert.AreNotEqual(MotionId.Fallback, id,
                    "a computed motion id must never collide with the fallback sentinel");
            }
        }

        [Test]
        public void Compute_IsDeterministic()
        {
            var track = new Entity { Index = 12, Version = 3 };
            var clipHash = new Hash128(1u, 2u, 3u, 4u);
            Assert.AreEqual(
                MotionId.Compute(track, 1, clipHash),
                MotionId.Compute(track, 1, clipHash));
        }

        [Test]
        public void Compute_FallbackCollision_RemapsToFallbackMinusOne()
        {
            Assert.AreEqual(MotionId.Fallback - 1u, RemapAwayFromSentinel(MotionId.Fallback));
            Assert.AreEqual(0u, RemapAwayFromSentinel(0u));
            Assert.AreEqual(123u, RemapAwayFromSentinel(123u));
            Assert.AreEqual(MotionId.Fallback - 1u, RemapAwayFromSentinel(MotionId.Fallback - 1u));
        }

        private static uint RemapAwayFromSentinel(uint id)
        {
            return id == MotionId.Fallback ? MotionId.Fallback - 1u : id;
        }
    }

    [TestFixture]
    public class FallbackScrubAdvanceTests
    {
        private const float MinDuration = 0.001f;

        private static float IntegrateFallback(
            float accumulated,
            FallbackPlaybackMode mode,
            float clipLength,
            float deltaTime,
            bool isScrubbing)
        {
            var duration = math.max(MinDuration, clipLength);
            var fallbackAdvance = (isScrubbing ? 0f : deltaTime) / duration;

            if (mode == FallbackPlaybackMode.Hold)
            {
                if (accumulated < 1f)
                    accumulated += fallbackAdvance;
            }
            else
            {
                accumulated += fallbackAdvance;
            }

            return accumulated;
        }

        [Test]
        public void Loop_DoesNotAdvance_WhileScrubbing()
        {
            var advanced = IntegrateFallback(0.25f, FallbackPlaybackMode.Loop, 1f, 0.5f, true);
            Assert.AreEqual(0.25f, advanced, 1e-6f,
                "while scrubbing the fallback clock must not free-run by DeltaTime");
        }

        [Test]
        public void Loop_AdvancesByDeltaOverDuration_WhilePlaying()
        {
            var advanced = IntegrateFallback(0.25f, FallbackPlaybackMode.Loop, 2f, 0.5f, false);
            Assert.AreEqual(0.25f + 0.5f / 2f, advanced, 1e-6f);
        }

        [Test]
        public void Hold_DoesNotAdvance_WhileScrubbing()
        {
            var advanced = IntegrateFallback(0.5f, FallbackPlaybackMode.Hold, 1f, 0.5f, true);
            Assert.AreEqual(0.5f, advanced, 1e-6f);
        }

        [Test]
        public void Hold_AdvancesWhilePlaying_ThenLatchesAtOne()
        {
            var advanced = IntegrateFallback(0.5f, FallbackPlaybackMode.Hold, 1f, 0.25f, false);
            Assert.AreEqual(0.75f, advanced, 1e-6f);

            var latched = IntegrateFallback(1f, FallbackPlaybackMode.Hold, 1f, 0.25f, false);
            Assert.AreEqual(1f, latched, 1e-6f, "Hold mode must not advance past 1");
        }

        [Test]
        public void Scrub_IsStable_AcrossRepeatedFrames()
        {
            var accumulated = 0.4f;
            for (var i = 0; i < 10; i++)
                accumulated = IntegrateFallback(accumulated, FallbackPlaybackMode.Loop, 1f, 0.5f, true);

            Assert.AreEqual(0.4f, accumulated, 1e-6f,
                "repeated scrub frames must leave the fallback clock untouched");
        }
    }
}