#if UNITY_EDITOR
using System;
using Unity.Physics.Authoring;
using UnityEditor;
using UnityEngine;

namespace BovineLabs.Timeline.Animation.Authoring
{
    public enum RagdollJointKind
    {
        None,
        Ball,
        Hinge,
    }

    [Serializable]
    public struct RagdollBoneSpec
    {
        public HumanBodyBones Bone;
        public HumanBodyBones[] DirChildren; // first existing bone gives capsule direction + length
        public HumanBodyBones Parent;        // parent ragdoll bone; (HumanBodyBones)(-1) = root, no joint
        public RagdollJointKind Joint;
        public float RadiusScale;            // radius = boneLen * scale, clamped
        public float Mass;
        public float ConeAngle;              // ball: cone + perpendicular half-angle (deg)
        public float TwistRange;             // ball: +/- twist (deg)
        public float HingeMin, HingeMax;     // hinge: angle limits (deg)
    }

    /// <summary>
    /// Project-tunable inputs for <see cref="RagdollGenerator"/>: the per-bone physics-body table plus the collision
    /// categories the generated capsules belong to and collide with. Create one from the asset menu to override the
    /// shipped defaults on a per-project basis; the generator uses the first asset it finds, else the built-in
    /// defaults (so behaviour is unchanged when no asset is present).
    /// </summary>
    [CreateAssetMenu(fileName = "RagdollGeneratorSettings", menuName = "BovineLabs/Timeline/Ragdoll Generator Settings")]
    public class RagdollGeneratorSettings : ScriptableObject
    {
        private const HumanBodyBones Root = (HumanBodyBones)(-1);

        [Tooltip("The collision categories the generated ragdoll capsules belong to. Default: a dedicated category " +
                 "(31) excluded from itself so the overlapping corpse capsules never self-collide.")]
        public PhysicsCategoryTags belongsTo = new() { Category31 = true };

        [Tooltip("What the generated capsules collide with. Default: the solid world only — Ground(0), Barrier(2), " +
                 "Prop(8) — NOT the character's own gameplay volumes, which the corpse sits inside.")]
        public PhysicsCategoryTags collidesWith = new() { Category00 = true, Category02 = true, Category08 = true };

        [Tooltip("Per-bone physics-body table: capsule sizing, mass, and joint limits for each humanoid bone.")]
        public RagdollBoneSpec[] bones = DefaultBones();

        public static RagdollGeneratorSettings FindOrDefault()
        {
            var guids = AssetDatabase.FindAssets("t:" + nameof(RagdollGeneratorSettings));
            if (guids.Length > 0)
            {
                var path = AssetDatabase.GUIDToAssetPath(guids[0]);
                var asset = AssetDatabase.LoadAssetAtPath<RagdollGeneratorSettings>(path);
                if (asset != null)
                {
                    return asset;
                }
            }

            return CreateInstance<RagdollGeneratorSettings>();
        }

        public static RagdollBoneSpec[] DefaultBones()
        {
            return new[]
            {
                new RagdollBoneSpec { Bone = HumanBodyBones.Hips, DirChildren = new[] { HumanBodyBones.Spine, HumanBodyBones.Chest }, Parent = Root, Joint = RagdollJointKind.None, RadiusScale = 0.6f, Mass = 8f },
                new RagdollBoneSpec { Bone = HumanBodyBones.Spine, DirChildren = new[] { HumanBodyBones.Chest, HumanBodyBones.UpperChest, HumanBodyBones.Neck, HumanBodyBones.Head }, Parent = HumanBodyBones.Hips, Joint = RagdollJointKind.Ball, RadiusScale = 0.5f, Mass = 10f, ConeAngle = 25f, TwistRange = 15f },
                new RagdollBoneSpec { Bone = HumanBodyBones.Head, DirChildren = null, Parent = HumanBodyBones.Spine, Joint = RagdollJointKind.Ball, RadiusScale = 0.5f, Mass = 5f, ConeAngle = 25f, TwistRange = 25f },

                new RagdollBoneSpec { Bone = HumanBodyBones.LeftUpperArm, DirChildren = new[] { HumanBodyBones.LeftLowerArm }, Parent = HumanBodyBones.Spine, Joint = RagdollJointKind.Ball, RadiusScale = 0.28f, Mass = 2.5f, ConeAngle = 60f, TwistRange = 45f },
                new RagdollBoneSpec { Bone = HumanBodyBones.LeftLowerArm, DirChildren = new[] { HumanBodyBones.LeftHand }, Parent = HumanBodyBones.LeftUpperArm, Joint = RagdollJointKind.Hinge, RadiusScale = 0.25f, Mass = 1.5f, HingeMin = 0f, HingeMax = 150f },
                new RagdollBoneSpec { Bone = HumanBodyBones.RightUpperArm, DirChildren = new[] { HumanBodyBones.RightLowerArm }, Parent = HumanBodyBones.Spine, Joint = RagdollJointKind.Ball, RadiusScale = 0.28f, Mass = 2.5f, ConeAngle = 60f, TwistRange = 45f },
                new RagdollBoneSpec { Bone = HumanBodyBones.RightLowerArm, DirChildren = new[] { HumanBodyBones.RightHand }, Parent = HumanBodyBones.RightUpperArm, Joint = RagdollJointKind.Hinge, RadiusScale = 0.25f, Mass = 1.5f, HingeMin = 0f, HingeMax = 150f },

                new RagdollBoneSpec { Bone = HumanBodyBones.LeftUpperLeg, DirChildren = new[] { HumanBodyBones.LeftLowerLeg }, Parent = HumanBodyBones.Hips, Joint = RagdollJointKind.Ball, RadiusScale = 0.3f, Mass = 7f, ConeAngle = 45f, TwistRange = 20f },
                new RagdollBoneSpec { Bone = HumanBodyBones.LeftLowerLeg, DirChildren = new[] { HumanBodyBones.LeftFoot }, Parent = HumanBodyBones.LeftUpperLeg, Joint = RagdollJointKind.Hinge, RadiusScale = 0.28f, Mass = 4f, HingeMin = -150f, HingeMax = 0f },
                new RagdollBoneSpec { Bone = HumanBodyBones.RightUpperLeg, DirChildren = new[] { HumanBodyBones.RightLowerLeg }, Parent = HumanBodyBones.Hips, Joint = RagdollJointKind.Ball, RadiusScale = 0.3f, Mass = 7f, ConeAngle = 45f, TwistRange = 20f },
                new RagdollBoneSpec { Bone = HumanBodyBones.RightLowerLeg, DirChildren = new[] { HumanBodyBones.RightFoot }, Parent = HumanBodyBones.RightUpperLeg, Joint = RagdollJointKind.Hinge, RadiusScale = 0.28f, Mass = 4f, HingeMin = -150f, HingeMax = 0f },
            };
        }
    }
}
#endif
