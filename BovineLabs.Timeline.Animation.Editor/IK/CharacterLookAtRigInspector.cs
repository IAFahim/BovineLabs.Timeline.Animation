using System.Collections.Generic;
using BovineLabs.Timeline.Animation.Authoring;
using Rukhanka.Hybrid;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace BovineLabs.Timeline.Animation.Editor
{
    [CustomEditor(typeof(CharacterLookAtRigAuthoring))]
    public class CharacterLookAtRigInspector : UnityEditor.Editor
    {
        private const string UxmlPath =
            "Packages/com.bovinelabs.timeline.animation/BovineLabs.Timeline.Animation.Editor/IK/CharacterLookAtRig.uxml";

        private const string UssPath =
            "Packages/com.bovinelabs.timeline.animation/BovineLabs.Timeline.Animation.Editor/IK/CharacterLookAt.uss";

        public override VisualElement CreateInspectorGUI()
        {
            var root = new VisualElement();

            var tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UxmlPath);
            if (tree == null)
            {
                root.Add(new HelpBox("CharacterLookAtRig.uxml could not be loaded.", HelpBoxMessageType.Error));
                return root;
            }

            tree.CloneTree(root);

            var style = AssetDatabase.LoadAssetAtPath<StyleSheet>(UssPath);
            if (style != null) root.styleSheets.Add(style);

            BindAngleClamp(root);

            var createTargetToggle = root.Q<Toggle>("createTargetToggle");

            var autoDetect = root.Q<Button>("autoDetectButton");
            if (autoDetect != null) autoDetect.clicked += AutoDetectBones;

            var build = root.Q<Button>("buildButton");
            if (build != null) build.clicked += () => BuildRig(createTargetToggle is { value: true });

            root.schedule.Execute(() => RefreshValidation(root)).Every(250);

            return root;
        }

        private static void BindAngleClamp(VisualElement root)
        {
            var minSlider = root.Q<Slider>("minAngleSlider");
            var maxSlider = root.Q<Slider>("maxAngleSlider");
            if (minSlider == null || maxSlider == null) return;

            minSlider.RegisterValueChangedCallback(evt =>
            {
                if (evt.newValue > maxSlider.value) maxSlider.value = evt.newValue;
            });

            maxSlider.RegisterValueChangedCallback(evt =>
            {
                if (evt.newValue < minSlider.value) minSlider.value = evt.newValue;
            });
        }

        private void RefreshValidation(VisualElement root)
        {
            var rig = target as CharacterLookAtRigAuthoring;
            if (rig == null) return;

            var animator = ResolveHumanoidAnimator(rig);

            SetVisible(root.Q<HelpBox>("noAnimator"), animator == null);
            SetVisible(root.Q<HelpBox>("bonesUnset"), rig.neckBone == null || rig.headBone == null);
            SetVisible(root.Q<HelpBox>("forwardNotUnit"), !IsUnitLength(rig.forwardVector));
            SetVisible(root.Q<HelpBox>("targetUnset"), rig.lookAtTarget == null);
        }

        private void AutoDetectBones()
        {
            var rig = target as CharacterLookAtRigAuthoring;
            if (rig == null) return;

            var animator = ResolveHumanoidAnimator(rig);
            if (animator == null)
            {
                Debug.LogWarning("CharacterLookAtRig: no Humanoid Animator found to auto-detect bones from.", rig);
                return;
            }

            Undo.RecordObject(rig, "Auto-Detect Look-At Bones");
            rig.neckBone = animator.GetBoneTransform(HumanBodyBones.Neck);
            rig.headBone = animator.GetBoneTransform(HumanBodyBones.Head);
            EditorUtility.SetDirty(rig);
        }

        private void BuildRig(bool createTargetIfMissing)
        {
            var rig = target as CharacterLookAtRigAuthoring;
            if (rig == null) return;

            if (rig.headBone == null)
            {
                Debug.LogWarning("CharacterLookAtRig: Head bone must be assigned before building.", rig);
                return;
            }

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Build Look-At Rig");
            var group = Undo.GetCurrentGroup();

            if (rig.lookAtTarget == null && createTargetIfMissing)
            {
                var animator = ResolveHumanoidAnimator(rig);
                var rootTransform = animator != null ? animator.transform : rig.transform;

                var targetObject = new GameObject($"{rig.gameObject.name}_LookAtTarget");
                Undo.RegisterCreatedObjectUndo(targetObject, "Create Look-At Target");
                Undo.SetTransformParent(targetObject.transform, rootTransform, "Parent Look-At Target");
                targetObject.transform.localPosition = new Vector3(0f, 1.6f, 2f);

                Undo.RecordObject(rig, "Assign Look-At Target");
                rig.lookAtTarget = targetObject.transform;
                EditorUtility.SetDirty(rig);
            }

            var aimIK = rig.headBone.GetComponent<AimIKAuthoring>();
            if (aimIK == null) aimIK = Undo.AddComponent<AimIKAuthoring>(rig.headBone.gameObject);

            Undo.RecordObject(aimIK, "Configure Aim IK");
            aimIK.target = rig.lookAtTarget;
            aimIK.forwardVector = rig.forwardVector;
            aimIK.angleLimitMin = rig.angleLimitMin;
            aimIK.angleLimitMax = rig.angleLimitMax;

            var bones = new List<WeightedTransform>();
            if (rig.neckBone != null) bones.Add(new WeightedTransform { bone = rig.neckBone, weight = rig.neckWeight });

            bones.Add(new WeightedTransform { bone = rig.headBone, weight = rig.headWeight });
            aimIK.affectedBones = bones.ToArray();

            EditorUtility.SetDirty(aimIK);

            Undo.CollapseUndoOperations(group);
        }

        private static Animator ResolveHumanoidAnimator(CharacterLookAtRigAuthoring rig)
        {
            var animator = rig.GetComponentInParent<Animator>();
            if (animator == null || !animator.isHuman) return null;

            return animator;
        }

        private static bool IsUnitLength(Vector3 v)
        {
            return Mathf.Abs(v.sqrMagnitude - 1f) <= 0.01f;
        }

        private static void SetVisible(VisualElement element, bool visible)
        {
            if (element == null) return;

            element.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        }
    }
}