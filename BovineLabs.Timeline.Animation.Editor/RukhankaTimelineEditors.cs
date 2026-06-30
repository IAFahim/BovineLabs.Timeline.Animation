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
            if (clonedFrom == null) RukhankaAnimationClipTimeline.MatchSource(clip, track, true, true);
        }

        public override ClipDrawOptions GetClipOptions(TimelineClip clip)
        {
            var options = base.GetClipOptions(clip);

            if (clip.asset is RukhankaAnimationClip asset && asset.animationClipHolder != null)
                options.tooltip = asset.animationClipHolder.name;

            return options;
        }
    }

    internal static class RukhankaAnimationClipTimeline
    {
        private const double MinDuration = 0.001d;
        private const double Epsilon = 0.000001d;
        private const string UndoName = "Match Rukhanka Animation Clip Length";
        private const string OffsetUndoName = "Match Rukhanka Animation Clip Offsets";

        public static bool MatchSelected(Object asset, bool resetPlayback)
        {
            var changed = false;
            var clips = TimelineEditor.selectedClips;

            for (var i = 0; i < clips.Length; i++)
            {
                var clip = clips[i];

                if (clip != null && clip.asset == asset)
                    changed |= MatchSource(clip, clip.GetParentTrack(), resetPlayback, true);
            }

            if (changed)
                TimelineEditor.Refresh(RefreshReason.ContentsModified | RefreshReason.SceneNeedsUpdate |
                                       RefreshReason.WindowNeedsRedraw);

            return changed;
        }

        // Copies the previous clip's authored positionOffset + eulerAnglesOffset onto every selected
        // RukhankaAnimationClip that matches the given asset. This is the authored-offset copy only;
        // sampling the previous clip's true root-end pose is a future upgrade.
        public static bool MatchOffsetsToPrevious(Object asset)
        {
            var changed = false;
            var clips = TimelineEditor.selectedClips;

            for (var i = 0; i < clips.Length; i++)
            {
                var clip = clips[i];

                if (clip == null || clip.asset != asset || clip.asset is not RukhankaAnimationClip target) continue;

                if (FindPreviousClip(clip)?.asset is not RukhankaAnimationClip source) continue;

                if (target.positionOffset == source.positionOffset &&
                    target.eulerAnglesOffset == source.eulerAnglesOffset) continue;

                Undo.RecordObject(target, OffsetUndoName);
                target.positionOffset = source.positionOffset;
                target.eulerAnglesOffset = source.eulerAnglesOffset;
                EditorUtility.SetDirty(target);
                changed = true;
            }

            if (changed)
                TimelineEditor.Refresh(RefreshReason.ContentsModified | RefreshReason.SceneNeedsUpdate |
                                       RefreshReason.WindowNeedsRedraw);

            return changed;
        }

        private static TimelineClip FindPreviousClip(TimelineClip clip)
        {
            var track = clip.GetParentTrack();

            if (track == null) return null;

            TimelineClip previous = null;

            foreach (var other in track.GetClips())
            {
                if (other == clip || other.start >= clip.start) continue;

                if (previous == null || other.start > previous.start) previous = other;
            }

            return previous;
        }

        public static bool MatchSource(TimelineClip clip, TrackAsset track, bool resetPlayback, bool recordUndo)
        {
            if (clip == null || clip.asset is not RukhankaAnimationClip asset ||
                asset.animationClipHolder == null) return false;

            var animationClip = asset.animationClipHolder;
            var owner = track != null ? track : clip.GetParentTrack();

            if (recordUndo && owner != null) Undo.RegisterCompleteObjectUndo(owner, UndoName);

            var changed = ApplyClipFields(clip, animationClip, resetPlayback);

            if (changed && owner != null) EditorUtility.SetDirty(owner);

            return changed;
        }

        private static bool ApplyClipFields(TimelineClip clip, AnimationClip animationClip, bool resetPlayback)
        {
            var duration = Math.Max(MinDuration, animationClip.length);

            var changed = false;

            if (!Approximately(clip.duration, duration))
            {
                clip.duration = duration;
                changed = true;
            }

            if (resetPlayback) changed |= ResetPlayback(clip);

            if (clip.displayName != animationClip.name)
            {
                clip.displayName = animationClip.name;
                changed = true;
            }

            return changed;
        }

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