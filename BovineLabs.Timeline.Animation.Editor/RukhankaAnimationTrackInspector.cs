using BovineLabs.Timeline.Animation.Authoring;
using UnityEditor;
using UnityEngine;
using UnityEngine.Timeline;

namespace BovineLabs.Timeline.Animation.Editor
{
    [CustomEditor(typeof(RukhankaAnimationTrack))]
    [CanEditMultipleObjects]
    public class RukhankaAnimationTrackInspector : UnityEditor.Editor
    {
        private SerializedProperty m_ApplyAvatarMask;
        private SerializedProperty m_AvatarMask;
        private SerializedProperty m_BlendInDuration;
        private SerializedProperty m_BlendOutDuration;
        private SerializedProperty m_EulerAnglesOffset;
        private SerializedProperty m_ExitIdleClip;
        private SerializedProperty m_FallbackPlaybackMode;
        private SerializedProperty m_LayerIndex;
        private SerializedProperty m_PositionOffset;
        private SerializedProperty m_TrackOffset;

        private void OnEnable()
        {
            m_LayerIndex = serializedObject.FindProperty("LayerIndex");
            m_TrackOffset = serializedObject.FindProperty("trackOffset");
            m_PositionOffset = serializedObject.FindProperty("positionOffset");
            m_EulerAnglesOffset = serializedObject.FindProperty("eulerAnglesOffset");
            m_AvatarMask = serializedObject.FindProperty("avatarMask");
            m_ApplyAvatarMask = serializedObject.FindProperty("applyAvatarMask");
            m_ExitIdleClip = serializedObject.FindProperty("ExitIdleClip");
            m_BlendInDuration = serializedObject.FindProperty("BlendInDuration");
            m_BlendOutDuration = serializedObject.FindProperty("BlendOutDuration");
            m_FallbackPlaybackMode = serializedObject.FindProperty("FallbackPlaybackMode");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.PropertyField(m_LayerIndex);
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Track Offsets", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(m_TrackOffset);

            if (m_TrackOffset.enumValueIndex != (int)TrackOffset.ApplyTransformOffsets &&
                !m_TrackOffset.hasMultipleDifferentValues)
            {
                EditorGUILayout.HelpBox(
                    "Only 'Apply Transform Offsets' is supported in DOTS; other modes ignore offsets.",
                    MessageType.Warning);
            }

            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(m_PositionOffset, new GUIContent("Position"));
            EditorGUILayout.PropertyField(m_EulerAnglesOffset, new GUIContent("Rotation"));
            EditorGUI.indentLevel--;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Avatar Mask", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(m_ApplyAvatarMask);

            if (m_ApplyAvatarMask.boolValue || m_ApplyAvatarMask.hasMultipleDifferentValues)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(m_AvatarMask);
                EditorGUI.indentLevel--;
            }

            // D#2: an overlay layer (>= 1) with no effective mask overrides the whole body over the layers below.
            if (!m_LayerIndex.hasMultipleDifferentValues && m_LayerIndex.intValue >= 1 &&
                (!m_ApplyAvatarMask.boolValue || m_AvatarMask.objectReferenceValue == null))
            {
                EditorGUILayout.HelpBox(
                    "Layer >= 1 with no Avatar Mask overrides the WHOLE body over the layers below. Assign an Avatar " +
                    "Mask (and keep Apply Avatar Mask on) so this layer only affects its intended bones.",
                    MessageType.Warning);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Exit / Fallback Override", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(m_ExitIdleClip);

            if (m_ExitIdleClip.objectReferenceValue != null || m_ExitIdleClip.hasMultipleDifferentValues)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(m_BlendInDuration);
                EditorGUILayout.PropertyField(m_BlendOutDuration);
                EditorGUILayout.PropertyField(m_FallbackPlaybackMode);
                EditorGUI.indentLevel--;
            }

            serializedObject.ApplyModifiedProperties();
        }

        // Scene-view offset handle (authoring aid; runtime bake is final truth). Skipped when trackOffset ignores
        // offsets (non-ApplyTransformOffsets modes are dropped in DOTS bake).
        private void OnSceneGUI()
        {
            if (target is not RukhankaAnimationTrack track ||
                track.trackOffset != TrackOffset.ApplyTransformOffsets)
                return;

            OffsetSceneHandles.DrawForTrack(serializedObject, track);
        }
    }
}