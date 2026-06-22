using Unity.Burst;
using Unity.Mathematics;

namespace BovineLabs.Timeline.Animation
{
    [BurstCompile]
    public static class BlendLayerMath
    {
        public const float WeightEpsilon = 0.0001f;

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
