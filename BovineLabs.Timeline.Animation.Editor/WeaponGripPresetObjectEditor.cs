#if !BL_DISABLE_OBJECT_DEFINITION
using System.Linq;
using BovineLabs.Timeline.Animation.Authoring;
using UnityEditor;
using UnityEngine;

namespace BovineLabs.Timeline.Animation.Editor
{
    /// <summary>
    /// Adds scene-view handles for authoring grips: assign a preview rig from the open scene, pick a grip, and move
    /// its position/rotation handle at the target bone. Offsets are written back into the preset asset.
    /// </summary>
    [CustomEditor(typeof(WeaponGripPresetObject))]
    public class WeaponGripPresetObjectEditor : UnityEditor.Editor
    {
        private static Animator s_PreviewRig;

        private int m_SelectedGrip;

        private void OnEnable() => SceneView.duringSceneGui += OnSceneGUI;

        private void OnDisable() => SceneView.duringSceneGui -= OnSceneGUI;

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var preset = (WeaponGripPresetObject)target;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Scene Preview", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            s_PreviewRig = (Animator)EditorGUILayout.ObjectField("Preview Rig", s_PreviewRig, typeof(Animator), true);

            if (preset.grips is { Length: > 0 })
            {
                var names = preset.grips
                    .Select((g, i) => string.IsNullOrWhiteSpace(g.name) ? $"Grip {i}" : g.name)
                    .ToArray();
                m_SelectedGrip = EditorGUILayout.Popup("Edit Grip", Mathf.Clamp(m_SelectedGrip, 0, names.Length - 1), names);
            }

            if (EditorGUI.EndChangeCheck())
                SceneView.RepaintAll();

            if (s_PreviewRig == null)
                EditorGUILayout.HelpBox(
                    "Assign a rig from the open scene to position grips with handles in the Scene view.",
                    MessageType.Info);
            else if (FindBone(s_PreviewRig, CurrentGrip()?.bone) == null)
                EditorGUILayout.HelpBox(
                    $"Bone '{CurrentGrip()?.bone}' was not found under '{s_PreviewRig.name}'.",
                    MessageType.Warning);
        }

        private WeaponGripPresetObject.GripAuthoring CurrentGrip()
        {
            var preset = (WeaponGripPresetObject)target;
            if (preset.grips == null || preset.grips.Length == 0)
                return null;
            return preset.grips[Mathf.Clamp(m_SelectedGrip, 0, preset.grips.Length - 1)];
        }

        private void OnSceneGUI(SceneView view)
        {
            if (s_PreviewRig == null)
                return;

            var grip = CurrentGrip();
            if (grip == null)
                return;

            var bone = FindBone(s_PreviewRig, grip.bone);
            if (bone == null)
                return;

            var worldPosition = bone.TransformPoint(grip.localPosition);
            var worldRotation = bone.rotation * Quaternion.Euler(grip.localRotationEuler);

            Handles.Label(worldPosition, $"{target.name} / {grip.name}");

            EditorGUI.BeginChangeCheck();
            worldPosition = Handles.PositionHandle(worldPosition, worldRotation);
            worldRotation = Handles.RotationHandle(worldRotation, worldPosition);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(target, "Edit Weapon Grip");
                grip.localPosition = bone.InverseTransformPoint(worldPosition);
                grip.localRotationEuler = (Quaternion.Inverse(bone.rotation) * worldRotation).eulerAngles;
                EditorUtility.SetDirty(target);
            }
        }

        private static Transform FindBone(Animator rig, string bone)
        {
            if (string.IsNullOrWhiteSpace(bone))
                return null;

            var all = rig.GetComponentsInChildren<Transform>(true);
            return all.FirstOrDefault(t => t.name == bone)
                   ?? all.FirstOrDefault(t => t.name.EndsWith(bone, System.StringComparison.Ordinal));
        }
    }
}
#endif
