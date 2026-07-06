using Unity.Mathematics;
using UnityEngine.Timeline;

namespace BovineLabs.Timeline.Animation
{
    internal static class ClipSampling
    {
        // #35: intentionally NOT a drop-in for Rukhanka's NormalizeAnimationTime. This drives the timeline clip's
        // extrapolation modes (PingPong / Loop / clamped-Saturate) and omits Rukhanka's per-clip cycle offset. Do not
        // "unify" the two — they answer different questions (timeline extrapolation vs animation-clip cycle phase).
        internal static float NormalizedClipTime(float timeSeconds, float duration,
            TimelineClip.ClipExtrapolation extrapolation, bool looped)
        {
            if (extrapolation == TimelineClip.ClipExtrapolation.PingPong)
            {
                var t = math.fmod(math.abs(timeSeconds), duration * 2f);
                return (duration - math.abs(t - duration)) / duration;
            }

            if (extrapolation == TimelineClip.ClipExtrapolation.Loop || looped)
            {
                return math.frac(timeSeconds / duration);
            }

            return math.saturate(timeSeconds / duration);
        }

        internal static void ComposeTrackClipOffset(float3 trackPos, quaternion trackRot, float3 clipPos,
            quaternion clipRot, out float3 pos, out quaternion rot)
        {
            pos = trackPos + math.rotate(trackRot, clipPos);
            rot = math.mul(trackRot, clipRot);
        }
    }
}
