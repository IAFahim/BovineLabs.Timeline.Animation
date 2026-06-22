using BovineLabs.Timeline.Animation.Authoring;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.Timeline;

namespace BovineLabs.Timeline.Animation.Editor
{
    [CustomTimelineEditor(typeof(CharacterLookAtClip))]
    public class CharacterLookAtClipEditor : ClipEditor
    {
        private static readonly Color WarningHighlight = new(0.9f, 0.55f, 0.1f);

        public override ClipDrawOptions GetClipOptions(TimelineClip clip)
        {
            var options = base.GetClipOptions(clip);

            if (clip.asset is not CharacterLookAtClip asset) return options;

            if (asset.sourceMode == PointSourceMode.LinkedTarget && asset.lookTargetLink == null)
            {
                options.errorText =
                    "Linked Target mode requires a Look Target Link. Assign an EntityLinkSchema or switch source mode.";
                options.highlightColor = WarningHighlight;
            }

            return options;
        }
    }
}