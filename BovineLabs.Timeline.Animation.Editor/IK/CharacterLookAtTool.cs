using BovineLabs.Timeline.Animation.Authoring;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEngine;

namespace BovineLabs.Timeline.Animation.Editor
{
    [EditorTool("Look At Point", typeof(CharacterLookAtRigAuthoring))]
    public class CharacterLookAtTool : EditorTool
    {
        private static readonly Color ConeColor = new(0.2f, 0.6f, 1f, 0.12f);
        private static readonly Color ConeOutlineColor = new(0.2f, 0.6f, 1f, 0.7f);
        private static readonly Color InRangeColor = new(0.25f, 0.9f, 0.35f);
        private static readonly Color ClampedColor = new(0.95f, 0.3f, 0.25f);

        public override void OnToolGUI(EditorWindow window)
        {
            foreach (var obj in targets)
            {
                if (obj is CharacterLookAtRigAuthoring rig && rig != null)
                {
                    DrawRig(rig);
                }
            }
        }

        private static void DrawRig(CharacterLookAtRigAuthoring rig)
        {
            var head = rig.headBone;
            if (head == null)
            {
                return;
            }

            var origin = head.position;
            var forward = rig.forwardVector.sqrMagnitude > 1e-6f
                ? head.TransformDirection(rig.forwardVector.normalized)
                : head.forward;

            DrawAimCone(rig, origin, forward, head.up);

            if (rig.lookAtTarget != null)
            {
                DrawTargetLink(rig, origin, forward);
                DrawTargetHandle(rig);
            }
        }

        private static void DrawAimCone(CharacterLookAtRigAuthoring rig, Vector3 origin, Vector3 forward, Vector3 up)
        {
            var min = Mathf.Min(rig.angleLimitMin, rig.angleLimitMax);
            var max = Mathf.Max(rig.angleLimitMin, rig.angleLimitMax);
            const float radius = 1.5f;

            var right = Vector3.Cross(up, forward);
            if (right.sqrMagnitude < 1e-6f)
            {
                right = Vector3.Cross(Vector3.up, forward);
            }

            var fromDir = Quaternion.AngleAxis(min, up) * forward;
            var span = max - min;

            Handles.color = ConeColor;
            Handles.DrawSolidArc(origin, up, fromDir, span, radius);

            Handles.color = ConeOutlineColor;
            Handles.DrawWireArc(origin, up, fromDir, span, radius);

            var minDir = Quaternion.AngleAxis(min, up) * forward;
            var maxDir = Quaternion.AngleAxis(max, up) * forward;
            Handles.DrawLine(origin, origin + (minDir * radius));
            Handles.DrawLine(origin, origin + (maxDir * radius));
        }

        private static void DrawTargetLink(CharacterLookAtRigAuthoring rig, Vector3 origin, Vector3 forward)
        {
            var toTarget = rig.lookAtTarget.position - origin;
            if (toTarget.sqrMagnitude < 1e-6f)
            {
                return;
            }

            var halfAngle = Mathf.Max(Mathf.Abs(rig.angleLimitMin), Mathf.Abs(rig.angleLimitMax));
            var angle = Vector3.Angle(forward, toTarget.normalized);

            Handles.color = angle <= halfAngle ? InRangeColor : ClampedColor;
            Handles.DrawLine(origin, rig.lookAtTarget.position);
        }

        private static void DrawTargetHandle(CharacterLookAtRigAuthoring rig)
        {
            var target = rig.lookAtTarget;

            Handles.color = InRangeColor;
            var size = HandleUtility.GetHandleSize(target.position) * 0.08f;
            Handles.SphereHandleCap(0, target.position, Quaternion.identity, size, EventType.Repaint);

            EditorGUI.BeginChangeCheck();
            var newPos = Handles.PositionHandle(target.position, target.rotation);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(target, "Move Look At Target");
                target.position = newPos;
            }
        }
    }
}
