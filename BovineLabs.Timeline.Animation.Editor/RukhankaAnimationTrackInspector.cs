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

            if (m_TrackOffset.enumValueIndex == (int)TrackOffset.ApplyTransformOffsets)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(m_PositionOffset, new GUIContent("Position"));
                EditorGUILayout.PropertyField(m_EulerAnglesOffset, new GUIContent("Rotation"));
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Avatar Mask", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(m_ApplyAvatarMask);

            if (m_ApplyAvatarMask.boolValue || m_ApplyAvatarMask.hasMultipleDifferentValues)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(m_AvatarMask);
                EditorGUI.indentLevel--;
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
    }
}