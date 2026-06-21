using System;
using BovineLabs.Timeline.Animation.Authoring;
using UnityEditor;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.Timeline;
using Object = UnityEngine.Object;

namespace BovineLabs.Timeline.Animation.Editor
{
    [CustomTimelineEditor(typeof(RukhankaAnimationTrack))]
    public class RukhankaAnimationTrackEditor : TrackEditor
    {
        public override TrackDrawOptions GetTrackOptions(TrackAsset track, Object binding)
        {
            var options = base.GetTrackOptions(track, binding);
            options.trackColor = new Color(0.16f, 0.54f, 0.88f);
            return options;
        }
    }

    [CustomTimelineEditor(typeof(RukhankaAnimationClip))]
    public class RukhankaAnimationClipEditor : ClipEditor
    {
        public override void OnCreate(TimelineClip clip, TrackAsset track, TimelineClip clonedFrom)
        {
            if (clonedFrom == null)
            {
                RukhankaAnimationClipTimeline.MatchSource(clip, track, true, true);
            }
        }

        public override ClipDrawOptions GetClipOptions(TimelineClip clip)
        {
            var options = base.GetClipOptions(clip);

            if (clip.asset is RukhankaAnimationClip asset && asset.animationClipHolder != null)
            {
                options.tooltip = asset.animationClipHolder.name;
            }

            return options;
        }
    }

    internal static class RukhankaAnimationClipTimeline
    {
        private const double MinDuration = 0.001d;
        private const double Epsilon = 0.000001d;
        private const string UndoName = "Match Rukhanka Animation Clip Length";

        public static bool MatchSelected(Object asset, bool resetPlayback)
        {
            var changed = false;
            var clips = TimelineEditor.selectedClips;

            for (var i = 0; i < clips.Length; i++)
            {
                var clip = clips[i];

                if (clip != null && clip.asset == asset)
                {
                    changed |= MatchSource(clip, clip.GetParentTrack(), resetPlayback, true);
                }
            }

            if (changed)
            {
                TimelineEditor.Refresh(RefreshReason.ContentsModified | RefreshReason.SceneNeedsUpdate | RefreshReason.WindowNeedsRedraw);
            }

            return changed;
        }

        public static bool MatchSource(TimelineClip clip, TrackAsset track, bool resetPlayback, bool recordUndo)
        {
            if (clip == null || clip.asset is not RukhankaAnimationClip asset || asset.animationClipHolder == null)
            {
                return false;
            }

            var animationClip = asset.animationClipHolder;
            var owner = track != null ? track : clip.GetParentTrack();

            if (recordUndo && owner != null)
            {
                Undo.RegisterCompleteObjectUndo(owner, UndoName);
            }

            var changed = ApplyClipFields(clip, animationClip, resetPlayback);

            if (changed && owner != null)
            {
                EditorUtility.SetDirty(owner);
            }

            return changed;
        }

        // Syncs the clip's fields to its source animation; returns true if any field actually changed.
        // Each sync is "only write when the value differs" so we don't dirty the clip needlessly.
        private static bool ApplyClipFields(TimelineClip clip, AnimationClip animationClip, bool resetPlayback)
        {
            var duration = Math.Max(MinDuration, animationClip.length);

            var changed = false;

            if (!Approximately(clip.duration, duration))
            {
                clip.duration = duration;
                changed = true;
            }

            if (resetPlayback)
            {
                changed |= ResetPlayback(clip);
            }

            if (clip.displayName != animationClip.name)
            {
                clip.displayName = animationClip.name;
                changed = true;
            }

            return changed;
        }

        // Restores neutral playback (1x speed, no clip-in trim); returns whether either field changed.
        private static bool ResetPlayback(TimelineClip clip)
        {
            var changed = false;

            if (!Approximately(clip.timeScale, 1d))
            {
                clip.timeScale = 1d;
                changed = true;
            }

            if (!Approximately(clip.clipIn, 0d))
            {
                clip.clipIn = 0d;
                changed = true;
            }

            return changed;
        }

        private static bool Approximately(double left, double right)
        {
            return Math.Abs(left - right) <= Epsilon;
        }
    }
}
