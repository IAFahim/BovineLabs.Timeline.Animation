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
        private SerializedProperty m_AdditiveReferencePoseClip;
        private SerializedProperty m_AdditiveReferencePoseTime;

        private void OnEnable()
        {
            m_AnimationClipHolder = serializedObject.FindProperty("animationClipHolder");
            m_PositionOffset = serializedObject.FindProperty("positionOffset");
            m_EulerAnglesOffset = serializedObject.FindProperty("eulerAnglesOffset");
            m_RemoveStartOffset = serializedObject.FindProperty("removeStartOffset");
            m_ApplyFootIK = serializedObject.FindProperty("applyFootIK");
            m_AdditiveReferencePoseClip = serializedObject.FindProperty("additiveReferencePoseClip");
            m_AdditiveReferencePoseTime = serializedObject.FindProperty("additiveReferencePoseTime");
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

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Additive Reference Pose", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(m_AdditiveReferencePoseClip, new GUIContent("Reference Pose Clip"));
            using (new EditorGUI.DisabledScope(m_AdditiveReferencePoseClip.objectReferenceValue == null))
            {
                EditorGUILayout.PropertyField(m_AdditiveReferencePoseTime, new GUIContent("Reference Pose Time"));
            }

            EditorGUILayout.HelpBox(
                "Only used when this clip's track Blend Mode = Additive. Leave the clip empty to keep current behavior.",
                MessageType.None);
            EditorGUI.indentLevel--;

            serializedObject.ApplyModifiedProperties();

            if (animationClipChanged) MatchSelectedClips(true);

            EditorGUILayout.Space();

            using (new EditorGUI.DisabledScope(targets.Length == 0))
            {
                if (GUILayout.Button("Match Timeline Clip Length")) MatchSelectedClips(true);
                if (GUILayout.Button("Match Offsets To Previous")) MatchOffsetsToPrevious();
            }
        }

        // Scene-view offset handle (authoring aid; runtime bake is final truth). Gated to edit mode + a resolvable
        // bound Animator inside OffsetSceneHandles.
        private void OnSceneGUI()
        {
            OffsetSceneHandles.DrawForClip(serializedObject, target);
        }

        private void MatchSelectedClips(bool resetPlayback)
        {
            for (var i = 0; i < targets.Length; i++)
                RukhankaAnimationClipTimeline.MatchSelected(targets[i], resetPlayback);
        }

        private void MatchOffsetsToPrevious()
        {
            for (var i = 0; i < targets.Length; i++)
                RukhankaAnimationClipTimeline.MatchOffsetsToPrevious(targets[i]);
        }
    }
}