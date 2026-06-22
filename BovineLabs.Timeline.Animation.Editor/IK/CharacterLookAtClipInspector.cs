using BovineLabs.Timeline.Animation.Authoring;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;

namespace BovineLabs.Timeline.Animation.Editor
{
    [CustomEditor(typeof(CharacterLookAtClip))]
    public class CharacterLookAtClipInspector : UnityEditor.Editor
    {
        private const string UxmlPath =
            "Packages/com.bovinelabs.timeline.animation/BovineLabs.Timeline.Animation.Editor/IK/CharacterLookAtClip.uxml";

        private const string UssPath =
            "Packages/com.bovinelabs.timeline.animation/BovineLabs.Timeline.Animation.Editor/IK/CharacterLookAt.uss";

        public override VisualElement CreateInspectorGUI()
        {
            var root = new VisualElement();

            var tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UxmlPath);
            if (tree == null)
            {
                root.Add(new HelpBox("CharacterLookAtClip.uxml could not be loaded.", HelpBoxMessageType.Error));
                return root;
            }

            tree.CloneTree(root);

            var style = AssetDatabase.LoadAssetAtPath<StyleSheet>(UssPath);
            if (style != null) root.styleSheets.Add(style);

            var sourceModeField = root.Q<PropertyField>("sourceMode");
            var linkedGroup = root.Q<VisualElement>("linkedGroup");
            var staticGroup = root.Q<VisualElement>("staticGroup");
            var offsetGroup = root.Q<VisualElement>("offsetGroup");
            var linkWarning = root.Q<HelpBox>("linkWarning");
            var minSlider = root.Q<Slider>("minAngleSlider");
            var maxSlider = root.Q<Slider>("maxAngleSlider");

            void RefreshVisibility()
            {
                var clip = target as CharacterLookAtClip;
                if (clip == null) return;

                SetVisible(linkedGroup, clip.sourceMode == PointSourceMode.LinkedTarget);
                SetVisible(staticGroup, clip.sourceMode == PointSourceMode.StaticWorld);
                SetVisible(offsetGroup, clip.sourceMode == PointSourceMode.OwnerOffset);
                SetVisible(linkWarning, clip.sourceMode == PointSourceMode.LinkedTarget && clip.lookTargetLink == null);
            }

            sourceModeField?.RegisterValueChangeCallback(_ => RefreshVisibility());

            var linkField = root.Q<PropertyField>("lookTargetLink");
            linkField?.RegisterValueChangeCallback(_ => RefreshVisibility());

            if (minSlider != null && maxSlider != null)
            {
                minSlider.RegisterValueChangedCallback(evt =>
                {
                    if (evt.newValue > maxSlider.value) maxSlider.value = evt.newValue;
                });

                maxSlider.RegisterValueChangedCallback(evt =>
                {
                    if (evt.newValue < minSlider.value) minSlider.value = evt.newValue;
                });
            }

            root.schedule.Execute(RefreshVisibility);

            return root;
        }

        private static void SetVisible(VisualElement element, bool visible)
        {
            if (element == null) return;

            element.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        }
    }
}