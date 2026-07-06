using Unity.Mathematics;

namespace BovineLabs.Timeline.Animation
{
    /// <summary>
    /// Pure, Burst-friendly math for <see cref="InertializationState"/> decay: the Bollo quintic, the shortest-arc
    /// angle-axis reduction, the normalized-time wrap, and the same-clip phase-jump predicate. Extracted so the
    /// numerically intricate pieces can be unit tested independently of the ECS job that drives them.
    /// </summary>
    internal static class InertializationMath
    {
        /// <summary>
        /// Floor tolerance (seconds) for the same-clip phase-jump detector. A discontinuity smaller than this in
        /// wall-clock terms never triggers, so frame-time jitter on short clips cannot false-positive.
        /// </summary>
        internal const float PhaseJumpFloorSeconds = 0.05f;

        /// <summary>Weight a new candidate must exceed the latched dominant by before the dominant flips (hysteresis).</summary>
        internal const float DominanceWeightMargin = 0.05f;

        // Full Bollo quintic closed-form for one scalar channel. x(0)=x0, x'(0)=v0, x''(0)=a0; x(T)=x'(T)=x''(T)=0.
        // Per-channel overshoot guard shortens the effective window when the offset is already closing on zero.
        internal static float Quintic(float x0, float v0, float a0, float t, float duration)
        {
            var teff = duration;
            if (math.abs(v0) > 1e-9f)
            {
                var guard = -5f * x0 / v0;
                if (guard > 0f && guard < teff)
                {
                    teff = guard;
                }
            }

            if (teff <= 1e-9f || t >= teff)
            {
                return 0f;
            }

            var t2 = teff * teff;
            var t3 = t2 * teff;
            var t4 = t3 * teff;
            var t5 = t4 * teff;

            var A = -(a0 * t2 + 6f * v0 * teff + 12f * x0) / (2f * t5);
            var B = (3f * a0 * t2 + 16f * v0 * teff + 30f * x0) / (2f * t4);
            var C = -(3f * a0 * t2 + 12f * v0 * teff + 20f * x0) / (2f * t3);

            var p2 = t * t;
            var p3 = p2 * t;
            var p4 = p3 * t;
            var p5 = p4 * t;

            return (A * p5) + (B * p4) + (C * p3) + (a0 * 0.5f * p2) + (v0 * t) + x0;
        }

        // Quaternion -> shortest-arc angle (>= 0) about a unit axis. Mirrors Unity's ToAngleAxis semantics.
        internal static void ToAngleAxis(quaternion q, out float3 axis, out float angle)
        {
            var v = math.normalize(q).value;
            if (v.w < 0f)
            {
                v = -v; // shortest arc
            }

            var w = math.clamp(v.w, -1f, 1f);
            angle = 2f * math.acos(w);

            var s = math.sqrt(math.max(0f, 1f - (w * w)));
            axis = s < 1e-6f ? new float3(0f, 1f, 0f) : v.xyz / s;
        }

        // Wraps a normalized-time delta into [-0.5, 0.5] so a clip's phase difference is measured the short way
        // around the loop (a +0.98 step and a -0.02 step read the same). Used only by the phase-jump detector.
        internal static float WrapHalf(float x) => x - math.round(x);

        // Same-clip phase-jump predicate. Predicts this frame's phase from last frame's phase + the previous
        // per-frame step, then compares the deviation IN SECONDS against a tolerance that grows with the step size
        // (so frame-time jitter, which scales the step, does not false-positive) and never drops below a fixed floor.
        // A clean full-cycle wrap lands ~on the prediction; a genuine time reset does not.
        internal static bool IsPhaseJump(float dominantTime, float lastDominantTime, float prevDominantTime, float clipLengthSeconds)
        {
            var expectedStep = WrapHalf(lastDominantTime - prevDominantTime);
            var discNorm = WrapHalf(dominantTime - math.frac(lastDominantTime + expectedStep));
            var discSeconds = math.abs(discNorm) * clipLengthSeconds;
            var tolSeconds = math.max(PhaseJumpFloorSeconds, 2f * math.abs(expectedStep) * clipLengthSeconds);
            return discSeconds > tolSeconds;
        }
    }
}
