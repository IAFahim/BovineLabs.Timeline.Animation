#if !BL_DISABLE_OBJECT_DEFINITION
using System;
using System.Collections.Generic;
using System.Linq;
using BovineLabs.Timeline.Animation.Authoring;
using UnityEditor;
using UnityEngine;

namespace BovineLabs.Timeline.Animation.Editor
{
    [CustomEditor(typeof(WeaponGripClip))]
    [CanEditMultipleObjects]
    public class WeaponGripClipInspector : UnityEditor.Editor
    {
        private const string DefaultLabel = "(Weapon Default)";

        private SerializedProperty m_Grip;
        private string[] m_Values;
        private GUIContent[] m_Display;

        private void OnEnable()
        {
            m_Grip = serializedObject.FindProperty("grip");
            RefreshOptions();
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            var current = m_Grip.stringValue;
            var values = m_Values;
            var display = m_Display;

            var index = Array.IndexOf(values, current);
            if (index < 0)
            {
                // Keep unknown names visible instead of silently rewriting them.
                values = values.Append(current).ToArray();
                display = display.Append(new GUIContent($"{current} (not in any preset)")).ToArray();
                index = values.Length - 1;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.showMixedValue = m_Grip.hasMultipleDifferentValues;
                EditorGUI.BeginChangeCheck();
                var newIndex = EditorGUILayout.Popup(new GUIContent("Grip"), index, display);
                if (EditorGUI.EndChangeCheck())
                    m_Grip.stringValue = values[newIndex];
                EditorGUI.showMixedValue = false;

                if (GUILayout.Button("Refresh", GUILayout.Width(60)))
                    RefreshOptions();
            }

            if (m_Values.Length <= 1)
                EditorGUILayout.HelpBox(
                    "No grips found. Author grips on a Weapon Grip Preset asset (Create > BovineLabs > Timeline > Weapon Grip Preset) and add it to the WeaponGripSettings.",
                    MessageType.Info);

            serializedObject.ApplyModifiedProperties();
        }

        private void RefreshOptions()
        {
            var presetsByGrip = new Dictionary<string, List<string>>();

            foreach (var guid in AssetDatabase.FindAssets($"t:{nameof(WeaponGripPresetObject)}"))
            {
                var preset = AssetDatabase.LoadAssetAtPath<WeaponGripPresetObject>(AssetDatabase.GUIDToAssetPath(guid));
                if (preset == null || preset.grips == null)
                    continue;

                foreach (var grip in preset.grips)
                {
                    if (string.IsNullOrWhiteSpace(grip.name))
                        continue;

                    if (!presetsByGrip.TryGetValue(grip.name, out var owners))
                        presetsByGrip[grip.name] = owners = new List<string>();
                    owners.Add(preset.name);
                }
            }

            var names = presetsByGrip.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();

            m_Values = new[] { string.Empty }.Concat(names).ToArray();
            m_Display = new[] { new GUIContent(DefaultLabel) }
                .Concat(names.Select(n => new GUIContent($"{n}  —  {string.Join(", ", presetsByGrip[n])}")))
                .ToArray();
        }
    }
}
#endif
