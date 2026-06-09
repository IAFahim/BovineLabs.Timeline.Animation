using BovineLabs.Timeline.Animation.Authoring;
using UnityEditor;
using UnityEngine;

namespace BovineLabs.Timeline.Animation.Editor
{
    [CustomEditor(typeof(RukhankaAnimationClip))]
    [CanEditMultipleObjects]
    public class RukhankaAnimationClipInspector : UnityEditor.Editor
    {
        private SerializedProperty m_AnimationClipHolder;
        private SerializedProperty m_ApplyFootIK;
        private SerializedProperty m_EulerAnglesOffset;
        private SerializedProperty m_PositionOffset;
        private SerializedProperty m_RemoveStartOffset;

        private void OnEnable()
        {
            m_AnimationClipHolder = serializedObject.FindProperty("animationClipHolder");
            m_PositionOffset = serializedObject.FindProperty("positionOffset");
            m_EulerAnglesOffset = serializedObject.FindProperty("eulerAnglesOffset");
            m_RemoveStartOffset = serializedObject.FindProperty("removeStartOffset");
            m_ApplyFootIK = serializedObject.FindProperty("applyFootIK");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(m_AnimationClipHolder, new GUIContent("Animation Clip"));
            var animationClipChanged = EditorGUI.EndChangeCheck();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Clip Transform Offsets", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(m_PositionOffset, new GUIContent("Position"));
            EditorGUILayout.PropertyField(m_EulerAnglesOffset, new GUIContent("Rotation"));
            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(m_RemoveStartOffset, new GUIContent("Remove Start Offset"));
            EditorGUILayout.PropertyField(m_ApplyFootIK, new GUIContent("Foot IK"));
            EditorGUI.indentLevel--;

            serializedObject.ApplyModifiedProperties();

            if (animationClipChanged)
            {
                MatchSelectedClips(true);
            }

            EditorGUILayout.Space();

            using (new EditorGUI.DisabledScope(targets.Length == 0))
            {
                if (GUILayout.Button("Match Timeline Clip Length"))
                {
                    MatchSelectedClips(true);
                }
            }
        }

        private void MatchSelectedClips(bool resetPlayback)
        {
            for (var i = 0; i < targets.Length; i++)
            {
                RukhankaAnimationClipTimeline.MatchSelected(targets[i], resetPlayback);
            }
        }
    }
}
