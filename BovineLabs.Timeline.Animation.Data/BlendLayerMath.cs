using Unity.Burst;
using Unity.Mathematics;

namespace BovineLabs.Timeline.Animation
{
    [BurstCompile]
    public static class BlendLayerMath
    {
        public const float WeightEpsilon = 0.0001f;

        // Shared blend duration -> speed sentinel. A duration of 0 (or below the 0.001s floor) means "instant cut":
        // encoded as speed 0, the instant-snap sentinel IntegrateWeights expects. Only a strictly positive duration
        // smooths. Used by the global fallback path AND every per-clip/per-track blend so a zero snaps consistently.
        public static float DurationToSpeed(float d) => d > 0.001f ? 1f / d : 0f;

        public static float NormalizeBaseLayerWeight(float currentWeight, float baseControl, float layerSum)
        {
            var normalizeFactor = layerSum > WeightEpsilon ? baseControl / layerSum : 0f;
            return currentWeight * normalizeFactor;
        }

        public static float NormalizeAdditionalLayerWeight(float currentWeight, float layerSum)
        {
            return currentWeight / math.max(WeightEpsilon, layerSum);
        }

        public static float AdditionalLayerWeight(float layerSum)
        {
            return math.saturate(layerSum);
        }

        public static float FallbackWeight(float baseControl)
        {
            return 1f - baseControl;
        }
    }
}
