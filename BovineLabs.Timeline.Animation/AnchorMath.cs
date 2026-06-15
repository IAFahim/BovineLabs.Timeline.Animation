using Unity.Entities;
using Unity.Mathematics;

namespace BovineLabs.Timeline.Animation
{
    public static class AnchorMath
    {
        private const float Epsilon = 1e-5f;

        public static bool WeightedBlend(
            in DynamicBuffer<WeaponAnchorSample> samples,
            out float3 position,
            out quaternion rotation)
        {
            position = float3.zero;
            rotation = quaternion.identity;

            if (samples.Length == 0) return false;

            var total = 0f;
            for (var i = 0; i < samples.Length; i++)
                total += math.max(0f, samples[i].Weight);

            if (total < Epsilon) return false;

            var reference = samples[0].WorldRotation.value;
            var accumulatedRotation = float4.zero;
            var accumulatedPosition = float3.zero;

            for (var i = 0; i < samples.Length; i++)
            {
                var sample = samples[i];
                var normalized = math.max(0f, sample.Weight) / total;

                accumulatedPosition += sample.WorldPosition * normalized;

                var q = sample.WorldRotation.value;
                var aligned = math.select(q, -q, math.dot(q, reference) < 0f);
                accumulatedRotation += aligned * normalized;
            }

            position = accumulatedPosition;

            if (math.lengthsq(accumulatedRotation) < Epsilon)
            {
                rotation = new quaternion(reference);
                return true;
            }

            rotation = math.normalize(new quaternion(accumulatedRotation));
            return true;
        }
    }
}