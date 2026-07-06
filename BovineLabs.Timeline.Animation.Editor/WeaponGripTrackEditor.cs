using BovineLabs.Timeline.Animation.Authoring;
using Rukhanka.Hybrid;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.Timeline;
using Object = UnityEngine.Object;

namespace BovineLabs.Timeline.Animation.Editor
{
    /// <summary>
    /// Surfaces the WeaponGrip track's binding contract at authoring time. The track binds the WEAPON GameObject, not
    /// the character — grip clips anchor the bound weapon onto the holder's rig, and state clips drive its lifecycle.
    /// Dragging the rigged character (which carries a Rukhanka <see cref="RigDefinitionAuthoring" />) onto the track is
    /// otherwise a silent no-op; this editor shows a track error instead.
    /// </summary>
    [CustomTimelineEditor(typeof(WeaponGripTrack))]
    public class WeaponGripTrackEditor : TrackEditor
    {
        public override TrackDrawOptions GetTrackOptions(TrackAsset track, Object binding)
        {
            var options = base.GetTrackOptions(track, binding);
            options.trackColor = new Color(0.95f, 0.60f, 0.15f);

            if (binding is GameObject go && go.GetComponentInChildren<RigDefinitionAuthoring>() != null)
            {
                options.errorText =
                    "Bind the weapon, not the character. This track anchors the bound weapon GameObject onto the " +
                    "holder's rig; the bound object carries a Rukhanka RigDefinitionAuthoring, so it is the character.";
            }

            return options;
        }
    }
}
