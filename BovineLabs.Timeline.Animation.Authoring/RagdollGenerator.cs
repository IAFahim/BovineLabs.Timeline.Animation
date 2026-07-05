#if UNITY_EDITOR
using System.Collections.Generic;
using Rukhanka.Hybrid;
using Unity.Mathematics;
using Unity.Physics.Authoring;
using UnityEditor;
using UnityEngine;

namespace BovineLabs.Timeline.Animation.Authoring
{
    /// <summary>
    /// One-click DOTS ragdoll generator for a humanoid Rukhanka rig. Ported from the Rukhanka Ragdoll sample and
    /// extended to wire the runtime bridge: it stamps an 11-body physics skeleton (capsules + cone/hinge joints,
    /// auto-sized from the rig's real bone lengths), then adds a <see cref="RagdollBodyAuthoring"/> to each body,
    /// an <c>OverrideTransformIKAuthoring</c> to each bone (target = its body), and one
    /// <see cref="RagdollAuthoring"/> to the rig root. Bake + drop a RagdollClip and the character ragdolls.
    /// Run it with the rig's prefab open (so the Animator resolves live bones), then save.
    /// </summary>
    public static class RagdollGenerator
    {
        private enum JointKind
        {
            None,
            Ball,
            Hinge,
        }

        private struct BoneSpec
        {
            public HumanBodyBones Bone;
            public HumanBodyBones[] DirChildren; // first existing bone gives capsule direction + length
            public HumanBodyBones Parent;        // parent ragdoll bone; (HumanBodyBones)(-1) = root, no joint
            public JointKind Joint;
            public float RadiusScale;            // radius = boneLen * scale, clamped
            public float Mass;
            public float ConeAngle;              // ball: cone + perpendicular half-angle (deg)
            public float TwistRange;             // ball: +/- twist (deg)
            public float HingeMin, HingeMax;     // hinge: angle limits (deg)
        }

        private const HumanBodyBones Root = (HumanBodyBones)(-1);

        private static readonly BoneSpec[] Specs =
        {
            new() { Bone = HumanBodyBones.Hips, DirChildren = new[] { HumanBodyBones.Spine, HumanBodyBones.Chest }, Parent = Root, Joint = JointKind.None, RadiusScale = 0.6f, Mass = 8f },
            new() { Bone = HumanBodyBones.Spine, DirChildren = new[] { HumanBodyBones.Chest, HumanBodyBones.UpperChest, HumanBodyBones.Neck, HumanBodyBones.Head }, Parent = HumanBodyBones.Hips, Joint = JointKind.Ball, RadiusScale = 0.5f, Mass = 10f, ConeAngle = 25f, TwistRange = 15f },
            new() { Bone = HumanBodyBones.Head, DirChildren = null, Parent = HumanBodyBones.Spine, Joint = JointKind.Ball, RadiusScale = 0.5f, Mass = 5f, ConeAngle = 25f, TwistRange = 25f },

            new() { Bone = HumanBodyBones.LeftUpperArm, DirChildren = new[] { HumanBodyBones.LeftLowerArm }, Parent = HumanBodyBones.Spine, Joint = JointKind.Ball, RadiusScale = 0.28f, Mass = 2.5f, ConeAngle = 60f, TwistRange = 45f },
            new() { Bone = HumanBodyBones.LeftLowerArm, DirChildren = new[] { HumanBodyBones.LeftHand }, Parent = HumanBodyBones.LeftUpperArm, Joint = JointKind.Hinge, RadiusScale = 0.25f, Mass = 1.5f, HingeMin = 0f, HingeMax = 150f },
            new() { Bone = HumanBodyBones.RightUpperArm, DirChildren = new[] { HumanBodyBones.RightLowerArm }, Parent = HumanBodyBones.Spine, Joint = JointKind.Ball, RadiusScale = 0.28f, Mass = 2.5f, ConeAngle = 60f, TwistRange = 45f },
            new() { Bone = HumanBodyBones.RightLowerArm, DirChildren = new[] { HumanBodyBones.RightHand }, Parent = HumanBodyBones.RightUpperArm, Joint = JointKind.Hinge, RadiusScale = 0.25f, Mass = 1.5f, HingeMin = 0f, HingeMax = 150f },

            new() { Bone = HumanBodyBones.LeftUpperLeg, DirChildren = new[] { HumanBodyBones.LeftLowerLeg }, Parent = HumanBodyBones.Hips, Joint = JointKind.Ball, RadiusScale = 0.3f, Mass = 7f, ConeAngle = 45f, TwistRange = 20f },
            new() { Bone = HumanBodyBones.LeftLowerLeg, DirChildren = new[] { HumanBodyBones.LeftFoot }, Parent = HumanBodyBones.LeftUpperLeg, Joint = JointKind.Hinge, RadiusScale = 0.28f, Mass = 4f, HingeMin = -150f, HingeMax = 0f },
            new() { Bone = HumanBodyBones.RightUpperLeg, DirChildren = new[] { HumanBodyBones.RightLowerLeg }, Parent = HumanBodyBones.Hips, Joint = JointKind.Ball, RadiusScale = 0.3f, Mass = 7f, ConeAngle = 45f, TwistRange = 20f },
            new() { Bone = HumanBodyBones.RightLowerLeg, DirChildren = new[] { HumanBodyBones.RightFoot }, Parent = HumanBodyBones.RightUpperLeg, Joint = JointKind.Hinge, RadiusScale = 0.28f, Mass = 4f, HingeMin = -150f, HingeMax = 0f },
        };

        [MenuItem("Tools/BovineLabs/Ragdoll/Build On Selected")]
        private static void BuildOnSelected()
        {
            var go = Selection.activeGameObject;
            if (go == null)
            {
                Debug.LogError("[Ragdoll] Select a GameObject with a Humanoid Animator first (open the rig prefab).");
                return;
            }

            var anm = go.GetComponentInChildren<Animator>();
            if (anm == null || !anm.isHuman)
            {
                Debug.LogError("[Ragdoll] Selection has no Humanoid Animator.");
                return;
            }

            BuildRagdoll(anm);
        }

        public static GameObject BuildRagdoll(Animator anm)
        {
            CleanExisting(anm);

            var root = new GameObject("Ragdoll");
            Undo.RegisterCreatedObjectUndo(root, "Build Ragdoll");
            Undo.SetTransformParent(root.transform, anm.transform, "Build Ragdoll");
            root.transform.localPosition = Vector3.zero;
            root.transform.localRotation = Quaternion.identity;
            root.transform.localScale = Vector3.one;

            var bodies = new Dictionary<HumanBodyBones, PhysicsBodyAuthoring>();

            // Pass 1 — bodies + capsules + the runtime wiring (RagdollBodyAuthoring on body, OverrideTransformIK on bone).
            foreach (var s in Specs)
            {
                var boneT = anm.GetBoneTransform(s.Bone);
                if (boneT == null)
                {
                    continue;
                }

                var childT = FirstBone(anm, s.DirChildren);
                float3 start = boneT.position;
                float3 dir;
                float len;
                if (childT != null)
                {
                    dir = (float3)childT.position - start;
                    len = math.length(dir);
                }
                else
                {
                    dir = boneT.up; // head fallback: a short capsule up the bone's own axis
                    len = 0.18f;
                }

                if (len < 1e-3f)
                {
                    len = 0.1f;
                    dir = new float3(0f, 1f, 0f);
                }

                var ndir = math.normalize(dir);
                float3 mid = start + (ndir * (len * 0.5f));

                var bodyGO = new GameObject(s.Bone.ToString());
                Undo.RegisterCreatedObjectUndo(bodyGO, "Build Ragdoll");
                Undo.SetTransformParent(bodyGO.transform, root.transform, "Build Ragdoll");
                bodyGO.transform.position = mid;
                bodyGO.transform.rotation = Quaternion.LookRotation((Vector3)ndir, StableUp(ndir));
                bodyGO.transform.localScale = Vector3.one;

                var body = bodyGO.AddComponent<PhysicsBodyAuthoring>();
                // PhysicsBodyAuthoring defaults to Dynamic (BodyMotionType.Dynamic == 0), so it bakes a real
                // (finite-mass) PhysicsMass — required so the runtime can flip it. It starts inert at runtime via
                // RagdollBodyAuthoring (PhysicsMassOverride{IsKinematic=1} + Disabled).
                body.Mass = s.Mass;
                body.LinearDamping = 0.05f;
                body.AngularDamping = 0.05f;

                var radius = math.clamp(len * s.RadiusScale, 0.03f, 0.15f);
                var shape = bodyGO.AddComponent<PhysicsShapeAuthoring>();
                shape.SetCapsule(new CapsuleGeometryAuthoring
                {
                    Orientation = quaternion.identity, // capsule runs along local +Z, which we aligned to the bone
                    Center = float3.zero,
                    Height = math.max(len, radius * 2.1f),
                    Radius = radius,
                });

                // No self-collision: ragdoll capsules overlap at joints. Dedicated category (31 = Debug/Test),
                // excluded from itself. Collide ONLY with the solid world — Ground(0), Barrier(2), Prop(8) — NOT
                // the character's own gameplay volumes (Character/Hitbox/Hurtbox/Trigger/CameraBlocker), which the
                // corpse sits inside and would be violently ejected from. This is the fix for the ragdoll "explosion".
                shape.BelongsTo = new PhysicsCategoryTags { Category31 = true };
                shape.CollidesWith = new PhysicsCategoryTags { Category00 = true, Category02 = true, Category08 = true };

                // Runtime wiring: link this body to its rig + bone, and make the bone follow this body when ragdolling.
                var bodyAuth = bodyGO.AddComponent<RagdollBodyAuthoring>();
                bodyAuth.rigRoot = anm.transform;
                bodyAuth.bone = boneT;

                var ik = boneT.gameObject.GetComponent<OverrideTransformIKAuthoring>();
                if (ik == null)
                {
                    ik = Undo.AddComponent<OverrideTransformIKAuthoring>(boneT.gameObject);
                }

                ik.target = bodyGO.transform;
                ik.positionWeight = 1f;
                ik.rotationWeight = 1f;

                bodies[s.Bone] = body;
            }

            // Pass 2 — joints (need both bodies to exist).
            foreach (var s in Specs)
            {
                if (s.Joint == JointKind.None)
                {
                    continue;
                }

                if (!bodies.TryGetValue(s.Bone, out var body) || !bodies.TryGetValue(s.Parent, out var parentBody))
                {
                    continue;
                }

                var bodyGO = body.gameObject;
                var boneT = anm.GetBoneTransform(s.Bone);
                float3 anchorLocal = bodyGO.transform.InverseTransformPoint(boneT.position); // proximal joint point

                if (s.Joint == JointKind.Ball)
                {
                    var j = Undo.AddComponent<RagdollJoint>(bodyGO);
                    j.ConnectedBody = parentBody;
                    j.PositionLocal = anchorLocal;
                    j.AutoSetConnected = true;
                    j.TwistAxisLocal = new float3(0f, 0f, 1f);        // along the bone
                    j.PerpendicularAxisLocal = new float3(1f, 0f, 0f);
                    j.MaxConeAngle = s.ConeAngle;
                    j.MinPerpendicularAngle = -s.ConeAngle;
                    j.MaxPerpendicularAngle = s.ConeAngle;
                    j.MinTwistAngle = -s.TwistRange;
                    j.MaxTwistAngle = s.TwistRange;
                }
                else
                {
                    var j = Undo.AddComponent<LimitedHingeJoint>(bodyGO);
                    j.ConnectedBody = parentBody;
                    j.PositionLocal = anchorLocal;
                    j.AutoSetConnected = true;
                    j.HingeAxisLocal = new float3(1f, 0f, 0f);        // bend around local X
                    j.PerpendicularAxisLocal = new float3(0f, 0f, 1f);
                    j.MinAngle = s.HingeMin;
                    j.MaxAngle = s.HingeMax;
                }
            }

            // The switch lives on the rig root (the Animator / RigDefinitionAuthoring GameObject).
            if (anm.GetComponent<RagdollAuthoring>() == null)
            {
                Undo.AddComponent<RagdollAuthoring>(anm.gameObject);
            }

            Selection.activeGameObject = root;
            Debug.Log($"[Ragdoll] Built {bodies.Count} bodies + runtime wiring on '{anm.name}'. Save the prefab.");
            return root;
        }

        // Remove a previous build so the tool is re-runnable: the Ragdoll child (bodies+joints), the RagdollAuthoring
        // on the root, and the OverrideTransformIKAuthoring the tool added to each spec bone.
        private static void CleanExisting(Animator anm)
        {
            var existing = anm.transform.Find("Ragdoll");
            if (existing != null)
            {
                Undo.DestroyObjectImmediate(existing.gameObject);
            }

            var rootAuth = anm.GetComponent<RagdollAuthoring>();
            if (rootAuth != null)
            {
                Undo.DestroyObjectImmediate(rootAuth);
            }

            foreach (var s in Specs)
            {
                var boneT = anm.GetBoneTransform(s.Bone);
                var ik = boneT != null ? boneT.gameObject.GetComponent<OverrideTransformIKAuthoring>() : null;
                if (ik != null)
                {
                    Undo.DestroyObjectImmediate(ik);
                }
            }
        }

        private static Transform FirstBone(Animator a, HumanBodyBones[] candidates)
        {
            if (candidates == null)
            {
                return null;
            }

            foreach (var b in candidates)
            {
                var t = a.GetBoneTransform(b);
                if (t != null)
                {
                    return t;
                }
            }

            return null;
        }

        // LookRotation degenerates when the bone direction is parallel to the up reference (legs/spine are
        // vertical). Pick an up that is not parallel to the bone.
        private static Vector3 StableUp(float3 dir)
        {
            return math.abs(math.dot(dir, new float3(0f, 1f, 0f))) > 0.99f ? Vector3.forward : Vector3.up;
        }
    }
}
#endif
