using UnityEditor;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;
using Component = UnityEngine.Component;
using Object = UnityEngine.Object;

namespace BovineLabs.Timeline.Animation.Editor
{
    // Scene-view offset handles for animation clips/tracks. Draws a Position/Rotation handle at the authored offset,
    // transformed into the bound Animator's world space, and writes the dragged delta back to positionOffset /
    // eulerAnglesOffset. This is an authoring aid only — the runtime bake is the final truth.
    internal static class OffsetSceneHandles
    {
        private const string UndoName = "Edit Animation Offset";

        // Resolves the Animator bound to a track: a direct Animator binding, or any Component (e.g. a
        // RigDefinitionAuthoring) that carries an Animator.
        public static Animator ResolveAnimator(PlayableDirector director, TrackAsset track)
        {
            if (director == null || track == null)
                return null;

            var binding = director.GetGenericBinding(track);
            return binding as Animator ?? (binding as Component)?.GetComponent<Animator>();
        }

        // Finds the output track whose clips reference the given clip asset.
        public static TrackAsset FindOwningTrack(PlayableDirector director, Object clipAsset)
        {
            if (director == null || clipAsset == null || director.playableAsset is not TimelineAsset timeline)
                return null;

            foreach (var track in timeline.GetOutputTracks())
            foreach (var clip in track.GetClips())
                if (clip.asset == clipAsset)
                    return track;

            return null;
        }

        // Draws the offset handle and writes back on change. serializedObject must expose "positionOffset" (Vector3)
        // and "eulerAnglesOffset" (Vector3). No-op when not editable (playing, no animator, missing props).
        public static void Draw(SerializedObject serializedObject, Object owner, Animator animator)
        {
            if (Application.isPlaying || animator == null || owner == null)
                return;

            serializedObject.Update();

            var posProp = serializedObject.FindProperty("positionOffset");
            var rotProp = serializedObject.FindProperty("eulerAnglesOffset");
            if (posProp == null || rotProp == null)
                return;

            var t = animator.transform;
            var worldPos = t.TransformPoint(posProp.vector3Value);
            var worldRot = t.rotation * Quaternion.Euler(rotProp.vector3Value);

            EditorGUI.BeginChangeCheck();
            var newWorldPos = Handles.PositionHandle(worldPos, worldRot);
            var newWorldRot = Handles.RotationHandle(worldRot, newWorldPos);

            if (!EditorGUI.EndChangeCheck())
                return;

            Undo.RecordObject(owner, UndoName);
            posProp.vector3Value = t.InverseTransformPoint(newWorldPos);
            rotProp.vector3Value = (Quaternion.Inverse(t.rotation) * newWorldRot).eulerAngles;
            serializedObject.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(owner);

            TimelineEditor.Refresh(RefreshReason.ContentsModified | RefreshReason.SceneNeedsUpdate);
        }

        // Convenience for clip editors: resolves the owning track's Animator from the inspected director and draws.
        public static void DrawForClip(SerializedObject serializedObject, Object clipAsset)
        {
            var director = TimelineEditor.inspectedDirector;
            if (director == null)
                return;

            var track = FindOwningTrack(director, clipAsset);
            var animator = ResolveAnimator(director, track);
            Draw(serializedObject, clipAsset, animator);
        }

        // Convenience for track editors: resolves the track's Animator from the inspected director and draws.
        public static void DrawForTrack(SerializedObject serializedObject, TrackAsset track)
        {
            var director = TimelineEditor.inspectedDirector;
            var animator = ResolveAnimator(director, track);
            Draw(serializedObject, track, animator);
        }
    }
}
