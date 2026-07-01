// <copyright file="TimelineAnimationValidator.cs" company="BovineLabs">
//     Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Timeline.Animation.Editor
{
    using System;
    using System.Collections.Generic;
    using BovineLabs.Timeline.Animation.Authoring;
    using Rukhanka;
    using UnityEditor;
    using UnityEditor.Timeline;
    using UnityEngine;
    using UnityEngine.Playables;
    using UnityEngine.Timeline;
    using Object = UnityEngine.Object;

    /// <summary>
    /// Designer-facing validator for the Rukhanka/Blend-Tree animation timeline tracks. Walks every PlayableDirector in
    /// the loaded scenes plus every TimelineAsset in the project and reports authoring foot-guns (loop-snap risk,
    /// unmasked overlay layers, additive-without-reference-pose, unsupported offset modes, controller/fallback blob
    /// duplication) with one-click fixes where a safe automatic fix exists. Editor-only, non-destructive until a Fix
    /// button is pressed.
    /// </summary>
    public static class TimelineAnimationValidator
    {
        private const float LoopEpsilon = 1e-4f;

        [MenuItem("BovineLabs/Animation/Validate Timelines")]
        private static void Open()
        {
            var window = EditorWindow.GetWindow<TimelineAnimationValidatorWindow>();
            window.titleContent = new GUIContent("Animation Validator");
            window.Rescan();
            window.Show();
        }

        /// <summary>A single validation result. <see cref="Fix"/> null = report-only.</summary>
        public sealed class Finding
        {
            public MessageType Severity;
            public string Message;
            public Object Context;
            public Action Fix;
            public string FixLabel = "Fix";
            public Action Fix2;
            public string Fix2Label;
        }

        /// <summary>Runs every detection and returns the collected findings.</summary>
        public static List<Finding> Scan()
        {
            var findings = new List<Finding>();
            var timelines = new HashSet<TimelineAsset>();

            // PlayableDirectors in loaded scenes.
            var directors = Object.FindObjectsByType<PlayableDirector>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var director in directors)
            {
                if (director.playableAsset is TimelineAsset timeline)
                {
                    timelines.Add(timeline);
                }
            }

            // Every TimelineAsset in the project.
            foreach (var guid in AssetDatabase.FindAssets("t:TimelineAsset"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var timeline = AssetDatabase.LoadAssetAtPath<TimelineAsset>(path);
                if (timeline != null)
                {
                    timelines.Add(timeline);
                }
            }

            foreach (var timeline in timelines)
            {
                ScanTimeline(timeline, findings);
            }

            // TimelineAnimationStateAuthoring components in loaded scenes (D#3 foot-gun, D#5 dup-blob).
            var states = Object.FindObjectsByType<TimelineAnimationStateAuthoring>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var state in states)
            {
                ScanState(state, findings);
            }

            return findings;
        }

        private static void ScanTimeline(TimelineAsset timeline, List<Finding> findings)
        {
            foreach (var track in timeline.GetOutputTracks())
            {
                switch (track)
                {
                    case RukhankaAnimationTrack rukhanka:
                        ScanRukhankaTrack(rukhanka, findings);
                        break;
                    case BlendTree2DTrack blendTree:
                        ScanBlendTree2DTrack(blendTree, findings);
                        break;
                }
            }
        }

        private static void ScanRukhankaTrack(RukhankaAnimationTrack track, List<Finding> findings)
        {
            // D#2: overlay layer with no mask.
            if (track.LayerIndex >= 1 && (track.avatarMask == null || !track.applyAvatarMask))
            {
                findings.Add(new Finding
                {
                    Severity = MessageType.Warning,
                    Message = $"[D2 Overlay Layer, No Mask] Layer >= 1 track '{track.name}' has no Avatar Mask — it overrides the whole body.",
                    Context = track,
                });
            }

            // D#4: unsupported offset mode.
            AddOffsetModeFinding(track, track.trackOffset, findings);

            foreach (var timelineClip in track.GetClips())
            {
                if (timelineClip.asset is not RukhankaAnimationClip clip || clip.animationClipHolder == null)
                {
                    continue;
                }

                // D#1: loop-snap risk. A clip already in continuous-loop mode is seam-proof at any duration, so skip it.
                var looping = !clip.continuousLoop &&
                              (clip.animationClipHolder.isLooping ||
                               timelineClip.postExtrapolationMode == TimelineClip.ClipExtrapolation.Loop);
                if (looping)
                {
                    var cycle = clip.animationClipHolder.length / (float)Mathf.Max(1e-6f, (float)timelineClip.timeScale);
                    AddLoopSnapFinding(timelineClip, clip, cycle, findings);
                }

                // D#3: additive track with a clip that has no reference pose anywhere.
                if (track.BlendMode == AnimationBlendingMode.Additive)
                {
                    var settings = AnimationUtility.GetAnimationClipSettings(clip.animationClipHolder);
                    if (clip.additiveReferencePoseClip == null && settings.additiveReferencePoseClip == null)
                    {
                        var captured = clip;
                        findings.Add(new Finding
                        {
                            Severity = MessageType.Error,
                            Message = $"[D3 Additive Without Reference Pose] Additive track '{track.name}' clip '{clip.animationClipHolder.name}' has no additive reference pose (neither on the clip asset nor in its import settings) — the pose will be garbage. Fix sets the reference pose to the clip itself at time 0.",
                            Context = clip,
                            Fix = () => SetAdditiveReferencePose(captured),
                            FixLabel = "Set Ref Pose",
                        });
                    }
                }
            }
        }

        private static void ScanBlendTree2DTrack(BlendTree2DTrack track, List<Finding> findings)
        {
            // D#2: overlay layer with no mask.
            if (track.LayerIndex >= 1 && (track.avatarMask == null || !track.applyAvatarMask))
            {
                findings.Add(new Finding
                {
                    Severity = MessageType.Warning,
                    Message = $"[D2 Overlay Layer, No Mask] Layer >= 1 track '{track.name}' has no Avatar Mask — it overrides the whole body.",
                    Context = track,
                });
            }

            // D#4: unsupported offset mode.
            AddOffsetModeFinding(track, track.trackOffset, findings);

            // D#1: loop-snap risk. Blend Tree 2D clips have no continuousLoop field, so only duration-snap is offered.
            // The cycle length is approximated from the first motion clip (all blend motions are meant to be same-length
            // locomotion loops).
            var referenceClip = FirstMotionClip(track);
            if (referenceClip != null)
            {
                foreach (var timelineClip in track.GetClips())
                {
                    if (timelineClip.asset is not BlendTree2DClip)
                    {
                        continue;
                    }

                    var looping = referenceClip.isLooping ||
                                  timelineClip.postExtrapolationMode == TimelineClip.ClipExtrapolation.Loop;
                    if (!looping)
                    {
                        continue;
                    }

                    var cycle = referenceClip.length / (float)Mathf.Max(1e-6f, (float)timelineClip.timeScale);
                    if (!IsWholeMultiple(timelineClip.duration, cycle))
                    {
                        var captured = timelineClip;
                        var capturedCycle = cycle;
                        findings.Add(new Finding
                        {
                            Severity = MessageType.Warning,
                            Message = $"[D1 Loop-Snap Risk] Blend Tree 2D clip on track '{track.name}' has duration {timelineClip.duration:0.###}s that is not a whole multiple of its ~{cycle:0.###}s cycle — the loop will snap at the seam. Blend Tree clips have no continuous-loop option; the fix snaps the clip duration to a whole number of cycles.",
                            Context = referenceClip,
                            Fix = () => SnapDuration(captured, capturedCycle),
                            FixLabel = "Snap Duration",
                        });
                    }
                }
            }
        }

        private static void ScanState(TimelineAnimationStateAuthoring state, List<Finding> findings)
        {
            // D#3 foot-gun: additive fallback on layer 0 adds over the bind pose.
            if (state.fallbackBlendMode == AnimationBlendingMode.Additive &&
                state.fallbackLayerIndex == 0)
            {
                findings.Add(new Finding
                {
                    Severity = MessageType.Warning,
                    Message = $"[D3 Additive Fallback On Layer 0] '{state.name}' has fallbackBlendMode = Additive on fallbackLayerIndex 0 — additive on the base layer adds over the bind pose (foot-gun). Put the additive overlay on layer >= 1.",
                    Context = state,
                });
            }

            // D#5: controller + fallback dup-blob.
            if (state.fallbackAnimationClip != null)
            {
                var animator = state.GetComponent<Animator>();
                var controller = animator != null ? animator.runtimeAnimatorController : null;
                if (controller != null && Array.IndexOf(controller.animationClips, state.fallbackAnimationClip) >= 0)
                {
                    var capturedAnimator = animator;
                    var capturedState = state;
                    findings.Add(new Finding
                    {
                        Severity = MessageType.Warning,
                        Message = $"[D5 Controller + Fallback Dup-Blob] '{state.name}' has a fallbackAnimationClip ('{state.fallbackAnimationClip.name}') that is also inside the Animator's Runtime Animator Controller — the clip bakes as a duplicate blob asset. Clear one of the two.",
                        Context = state,
                        Fix = () => ClearController(capturedAnimator),
                        FixLabel = "Clear Controller",
                        Fix2 = () => ClearFallbackClip(capturedState),
                        Fix2Label = "Clear Fallback",
                    });
                }
            }
        }

        private static void AddOffsetModeFinding(TrackAsset track, TrackOffset offset, List<Finding> findings)
        {
            if (offset == TrackOffset.ApplyTransformOffsets)
            {
                return;
            }

            var captured = track;
            findings.Add(new Finding
            {
                Severity = MessageType.Warning,
                Message = $"[D4 Unsupported Offset Mode] Track '{track.name}' uses Track Offset mode '{offset}', which DOTS ignores (only Apply Transform Offsets is honored). Fix sets it to Apply Transform Offsets.",
                Context = track,
                Fix = () => SetApplyTransformOffsets(captured),
                FixLabel = "Set ApplyTransformOffsets",
            });
        }

        private static void AddLoopSnapFinding(TimelineClip timelineClip, RukhankaAnimationClip clip, float cycle, List<Finding> findings)
        {
            if (cycle <= LoopEpsilon || IsWholeMultiple(timelineClip.duration, cycle))
            {
                return;
            }

            var capturedTimelineClip = timelineClip;
            var capturedClip = clip;
            var capturedCycle = cycle;
            findings.Add(new Finding
            {
                Severity = MessageType.Warning,
                Message = $"[D1 Loop-Snap Risk] Looping clip '{clip.animationClipHolder.name}' on '{timelineClip.GetParentTrack()?.name}' has duration {timelineClip.duration:0.###}s that is not a whole multiple of its ~{cycle:0.###}s cycle — the loop snaps at the seam. Primary fix enables Continuous Loop (seam-proof at any duration; requires re-bake). Alternative: snap the clip duration to a whole number of cycles.",
                Context = clip,
                Fix = () => EnableContinuousLoop(capturedClip),
                FixLabel = "Enable Continuous Loop",
                Fix2 = () => SnapDuration(capturedTimelineClip, capturedCycle),
                Fix2Label = "Snap Duration",
            });
        }

        private static AnimationClip FirstMotionClip(BlendTree2DTrack track)
        {
            if (track.Motions == null)
            {
                return null;
            }

            foreach (var motion in track.Motions)
            {
                if (motion?.clip != null)
                {
                    return motion.clip;
                }
            }

            return null;
        }

        private static bool IsWholeMultiple(double duration, float cycle)
        {
            if (cycle <= LoopEpsilon)
            {
                return true;
            }

            var ratio = duration / cycle;
            return Mathf.Abs((float)(ratio - Math.Round(ratio))) <= LoopEpsilon;
        }

        // ---- Fixes ----
        private static void EnableContinuousLoop(RukhankaAnimationClip clip)
        {
            if (clip == null)
            {
                return;
            }

            var so = new SerializedObject(clip);
            var prop = so.FindProperty("continuousLoop");
            if (prop != null)
            {
                prop.boolValue = true;
                so.ApplyModifiedProperties();
                EditorUtility.SetDirty(clip);
                AssetDatabase.SaveAssets();
                TimelineEditor.Refresh(RefreshReason.ContentsModified);
            }
        }

        private static void SnapDuration(TimelineClip timelineClip, float cycle)
        {
            if (timelineClip == null || cycle <= LoopEpsilon)
            {
                return;
            }

            var track = timelineClip.GetParentTrack();
            if (track != null)
            {
                Undo.RecordObject(track, "Snap Clip Duration");
            }

            var cycles = Math.Max(1, (long)Math.Round(timelineClip.duration / cycle));
            timelineClip.duration = cycles * cycle;

            if (track != null)
            {
                EditorUtility.SetDirty(track);
            }

            AssetDatabase.SaveAssets();
            TimelineEditor.Refresh(RefreshReason.ContentsModified);
        }

        private static void SetAdditiveReferencePose(RukhankaAnimationClip clip)
        {
            if (clip == null)
            {
                return;
            }

            var so = new SerializedObject(clip);
            var refClip = so.FindProperty("additiveReferencePoseClip");
            var refTime = so.FindProperty("additiveReferencePoseTime");
            if (refClip != null)
            {
                refClip.objectReferenceValue = clip.animationClipHolder;
            }

            if (refTime != null)
            {
                refTime.floatValue = 0f;
            }

            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(clip);
            AssetDatabase.SaveAssets();
        }

        private static void SetApplyTransformOffsets(TrackAsset track)
        {
            if (track == null)
            {
                return;
            }

            var so = new SerializedObject(track);
            var prop = so.FindProperty("trackOffset");
            if (prop != null)
            {
                prop.enumValueIndex = (int)TrackOffset.ApplyTransformOffsets;
                so.ApplyModifiedProperties();
                EditorUtility.SetDirty(track);
                AssetDatabase.SaveAssets();
                TimelineEditor.Refresh(RefreshReason.ContentsModified);
            }
        }

        private static void ClearController(Animator animator)
        {
            if (animator == null)
            {
                return;
            }

            Undo.RecordObject(animator, "Clear Runtime Animator Controller");
            animator.runtimeAnimatorController = null;
            EditorUtility.SetDirty(animator);
        }

        private static void ClearFallbackClip(TimelineAnimationStateAuthoring state)
        {
            if (state == null)
            {
                return;
            }

            var so = new SerializedObject(state);
            var prop = so.FindProperty("fallbackAnimationClip");
            if (prop != null)
            {
                prop.objectReferenceValue = null;
                so.ApplyModifiedProperties();
                EditorUtility.SetDirty(state);
            }
        }

        private sealed class TimelineAnimationValidatorWindow : EditorWindow
        {
            private List<Finding> findings = new();
            private Vector2 scroll;

            public void Rescan()
            {
                findings = Scan();
                Repaint();
            }

            private void OnGUI()
            {
                using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
                {
                    if (GUILayout.Button("Rescan", EditorStyles.toolbarButton, GUILayout.Width(80)))
                    {
                        Rescan();
                    }

                    GUILayout.FlexibleSpace();
                    GUILayout.Label($"{findings.Count} finding(s)", EditorStyles.miniLabel);
                }

                if (findings.Count == 0)
                {
                    EditorGUILayout.HelpBox("No issues found in loaded scenes or project timelines.", MessageType.Info);
                    return;
                }

                scroll = EditorGUILayout.BeginScrollView(scroll);

                for (var i = 0; i < findings.Count; i++)
                {
                    var finding = findings[i];

                    using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                    {
                        EditorGUILayout.HelpBox(finding.Message, finding.Severity);

                        using (new EditorGUILayout.HorizontalScope())
                        {
                            using (new EditorGUI.DisabledScope(finding.Context == null))
                            {
                                if (GUILayout.Button("Ping", GUILayout.Width(60)))
                                {
                                    EditorGUIUtility.PingObject(finding.Context);
                                    Selection.activeObject = finding.Context;
                                }
                            }

                            GUILayout.FlexibleSpace();

                            if (finding.Fix != null && GUILayout.Button(finding.FixLabel, GUILayout.MinWidth(120)))
                            {
                                finding.Fix();
                                Rescan();
                                GUIUtility.ExitGUI();
                            }

                            if (finding.Fix2 != null &&
                                GUILayout.Button(finding.Fix2Label ?? "Fix", GUILayout.MinWidth(120)))
                            {
                                finding.Fix2();
                                Rescan();
                                GUIUtility.ExitGUI();
                            }
                        }
                    }
                }

                EditorGUILayout.EndScrollView();
            }
        }
    }
}
