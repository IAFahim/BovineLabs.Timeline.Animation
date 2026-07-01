using BovineLabs.Timeline.Animation.Authoring;
using UnityEditor;
using UnityEngine;
using UnityEngine.Timeline;

namespace BovineLabs.Timeline.Animation.Editor
{
    // Custom editor for BlendTree2DTrack: draws the default inspector, adds pre-bake warning HelpBoxes (unsupported
    // offset mode; overlay layer with no mask), and keeps the scene-view offset handle for positionOffset / eulerAnglesOffset.
    [CustomEditor(typeof(BlendTree2DTrack))]
    [CanEditMultipleObjects]
    public class BlendTree2DTrackInspector : UnityEditor.Editor
    {
        private SerializedProperty m_ApplyAvatarMask;
        private SerializedProperty m_AvatarMask;
        private SerializedProperty m_LayerIndex;
        private SerializedProperty m_TrackOffset;

        private void OnEnable()
        {
            m_LayerIndex = serializedObject.FindProperty("LayerIndex");
            m_TrackOffset = serializedObject.FindProperty("trackOffset");
            m_AvatarMask = serializedObject.FindProperty("avatarMask");
            m_ApplyAvatarMask = serializedObject.FindProperty("applyAvatarMask");
        }

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            // D#4 (mirror of RukhankaAnimationTrackInspector): only ApplyTransformOffsets is honored in DOTS bake.
            if (m_TrackOffset != null && !m_TrackOffset.hasMultipleDifferentValues &&
                m_TrackOffset.enumValueIndex != (int)TrackOffset.ApplyTransformOffsets)
            {
                EditorGUILayout.HelpBox(
                    "Only 'Apply Transform Offsets' is supported in DOTS; other modes ignore offsets.",
                    MessageType.Warning);
            }

            // D#2: an overlay layer (>= 1) with no effective mask overrides the whole body over the layers below.
            if (m_LayerIndex != null && !m_LayerIndex.hasMultipleDifferentValues && m_LayerIndex.intValue >= 1 &&
                (!m_ApplyAvatarMask.boolValue || m_AvatarMask.objectReferenceValue == null))
            {
                EditorGUILayout.HelpBox(
                    "Layer >= 1 with no Avatar Mask overrides the WHOLE body over the layers below. Assign an Avatar " +
                    "Mask (and keep Apply Avatar Mask on) so this layer only affects its intended bones.",
                    MessageType.Warning);
            }
        }

        // Scene-view offset handle (authoring aid; runtime bake is final truth). Skipped when trackOffset ignores
        // offsets (non-ApplyTransformOffsets modes are dropped in DOTS bake).
        private void OnSceneGUI()
        {
            if (target is not BlendTree2DTrack track ||
                track.trackOffset != TrackOffset.ApplyTransformOffsets)
                return;

            OffsetSceneHandles.DrawForTrack(serializedObject, track);
        }
    }
}
