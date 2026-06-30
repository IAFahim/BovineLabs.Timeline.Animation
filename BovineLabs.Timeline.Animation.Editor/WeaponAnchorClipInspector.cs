using BovineLabs.Timeline.Animation.Authoring;
using UnityEditor;
using UnityEngine;

namespace BovineLabs.Timeline.Animation.Editor
{
    [CustomEditor(typeof(WeaponAnchorClip))]
    [CanEditMultipleObjects]
    public class WeaponAnchorClipInspector : UnityEditor.Editor
    {
        private SerializedProperty m_Bone;
        private SerializedProperty m_LocalPosition;
        private SerializedProperty m_LocalRotationEuler;

        private void OnEnable()
        {
            m_Bone = serializedObject.FindProperty("bone");
            m_LocalPosition = serializedObject.FindProperty("localPosition");
            m_LocalRotationEuler = serializedObject.FindProperty("localRotationEuler");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.PropertyField(m_Bone, new GUIContent("Bone"));

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Local Offset From Bone", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(m_LocalPosition, new GUIContent("Position"));
            EditorGUILayout.PropertyField(m_LocalRotationEuler, new GUIContent("Rotation"));
            EditorGUI.indentLevel--;

            serializedObject.ApplyModifiedProperties();
        }
    }
}
