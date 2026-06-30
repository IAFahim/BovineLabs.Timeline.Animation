using UnityEngine.Timeline;

namespace BovineLabs.Timeline.Animation.Authoring
{
    /// <summary>
    /// Helper for resolving a track's authored sibling index at bake time, used to break same-layer fallback-override ties.
    /// </summary>
    internal static class FallbackTrackOrder
    {
        public static int Compute(TrackAsset track)
        {
            var index = 0;

            if (track.parent is TrackAsset parentTrack)
            {
                foreach (var child in parentTrack.GetChildTracks())
                {
                    if (ReferenceEquals(child, track)) return index;
                    index++;
                }
            }
            else if (track.timelineAsset != null)
            {
                foreach (var child in track.timelineAsset.GetRootTracks())
                {
                    if (ReferenceEquals(child, track)) return index;
                    index++;
                }
            }

            return index;
        }
    }
}
