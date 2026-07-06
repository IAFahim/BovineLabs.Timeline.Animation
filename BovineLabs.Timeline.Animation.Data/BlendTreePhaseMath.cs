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
        /// <summary>
        /// Largest magnitude of timeline-local delta accepted as a genuine frame step (seconds), in either
        /// direction. Kept below a plausible loop-clip length: a loop wrap of a locomotion clip (≥~0.3s)
        /// exceeds this and falls back to the signed scaled frame time (forward stays forward), while a real
        /// per-frame step — forward or reverse — sits well under it and is honoured verbatim.
        /// </summary>
        public const float MaxLocalDelta = 0.25f;

        /// <summary>Per-frame phase-time advance while playing (scrubbing keeps raw deltas).</summary>
        /// <param name="localDelta">Delta of the clip's LocalTime since last frame.</param>
        /// <param name="scaledDeltaTime">World delta time × the clip's TimeTransform scale (signed).</param>
        public static float PlayingDelta(float localDelta, float scaledDeltaTime)
        {
            // A genuine single-frame step (either direction) is trusted verbatim, so reverse playback cycles
            // the phase backward. Holds (zero), loop wraps and seeks fall back to the signed scaled frame time
            // — positive under forward play (loops stay forward), negative under reverse play (steps back).
            return localDelta != 0f && math.abs(localDelta) <= MaxLocalDelta
                ? localDelta
                : scaledDeltaTime;
        }
    }
}
