using Unity.Mathematics;

namespace BovineLabs.Timeline.Animation
{
    /// <summary>
    /// Free-running cycle clock for blend-tree tracks. Blend trees loop their gait cycle by
    /// accumulated phase, not by timeline-local time, so the timeline clock is only trusted for
    /// plausible forward frame steps. Anything else — loop wraps (negative delta, e.g. a looping
    /// 1s state timeline rewinds by ~0.98s every wrap), holds (zero delta), restarts and seeks
    /// (huge delta) — advances by scaled frame time instead, keeping the cycle seamless
    /// regardless of timeline duration. Mirrors the ContinuousLoop/PhaseVelocity contract of the
    /// single-clip track system.
    /// </summary>
    public static class BlendTreePhaseMath
    {
        /// <summary>Largest timeline-local delta accepted as a genuine frame step (seconds).</summary>
        public const float MaxLocalDelta = 1f;

        /// <summary>Per-frame phase-time advance while playing (scrubbing keeps raw deltas).</summary>
        /// <param name="localDelta">Delta of the clip's LocalTime since last frame.</param>
        /// <param name="scaledDeltaTime">World delta time × the clip's TimeTransform scale.</param>
        public static float PlayingDelta(float localDelta, float scaledDeltaTime)
        {
            return localDelta > 0f && localDelta <= MaxLocalDelta
                ? localDelta
                : math.max(scaledDeltaTime, 0f);
        }
    }
}
