using BovineLabs.Timeline.Animation.Authoring;
using BovineLabs.Timeline.Animation.Data;
using UnityEditor;
using UnityEngine;

namespace BovineLabs.Timeline.Animation.Editor
{
    [CustomEditor(typeof(BlendTree2DClip))]
    [CanEditMultipleObjects]
    public class BlendTree2DClipInspector : UnityEditor.Editor
    {
        private SerializedProperty m_BlendParameter;
        private SerializedProperty m_ReadKind;
        private SerializedProperty m_ReadFrom;
        private SerializedProperty m_MaxSpeed;
        private SerializedProperty m_PositionOffset;
        private SerializedProperty m_EulerAnglesOffset;
        private SerializedProperty m_RemoveStartOffset;
        private SerializedProperty m_ApplyFootIK;

        private void OnEnable()
        {
            m_BlendParameter = serializedObject.FindProperty("BlendParameter");
            m_ReadKind = serializedObject.FindProperty("ReadKind");
            m_ReadFrom = serializedObject.FindProperty("ReadFrom");
            m_MaxSpeed = serializedObject.FindProperty("maxSpeed");
            m_PositionOffset = serializedObject.FindProperty("positionOffset");
            m_EulerAnglesOffset = serializedObject.FindProperty("eulerAnglesOffset");
            m_RemoveStartOffset = serializedObject.FindProperty("removeStartOffset");
            m_ApplyFootIK = serializedObject.FindProperty("applyFootIK");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.PropertyField(m_ReadKind, new GUIContent("Read Kind"));

            var readKind = (BlendDirectionReadKind)m_ReadKind.enumValueIndex;
            var fromClip = !m_ReadKind.hasMultipleDifferentValues && readKind == BlendDirectionReadKind.ClipValue;
            var fromVelocity = !m_ReadKind.hasMultipleDifferentValues &&
                               readKind == BlendDirectionReadKind.PhysicsLinearVelocityNormalized;

            if (fromClip)
                EditorGUILayout.PropertyField(m_BlendParameter, new GUIContent("Blend Parameter"));

            if (!fromClip)
                EditorGUILayout.PropertyField(m_ReadFrom, new GUIContent("Read From"));

            if (fromVelocity)
                EditorGUILayout.PropertyField(m_MaxSpeed, new GUIContent("Max Speed"));

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
            EditorGUILayout.HelpBox(
                "Edit-mode preview shows the single dominant motion at the sample point, not the full blend — trust runtime for the blended result.",
                MessageType.Info);

            serializedObject.ApplyModifiedProperties();
        }

        // Scene-view offset handle (authoring aid; runtime bake is final truth). Gated to edit mode + a resolvable
        // bound Animator inside OffsetSceneHandles.
        private void OnSceneGUI()
        {
            OffsetSceneHandles.DrawForClip(serializedObject, target);
        }
    }
}
