using System;
using System.ComponentModel;
using BovineLabs.Timeline.Authoring;
using UnityEngine;
using UnityEngine.Timeline;

namespace BovineLabs.Timeline.Animation.Authoring
{
    /// <summary>
    /// Bind the weapon GameObject; grip clips select which authored grip the weapon anchors to and state clips
    /// fire lifecycle edges (equip/reattach/drop/pickup).
    /// </summary>
    [Serializable]
    [TrackClipType(typeof(WeaponGripClip))]
#if !BL_DISABLE_OBJECT_DEFINITION
    [TrackClipType(typeof(WeaponStateClip))]
#endif
    [TrackColor(0.95f, 0.60f, 0.15f)]
    [TrackBindingType(typeof(GameObject))]
    [DisplayName("BovineLabs/Animation/Weapon Grip")]
    public class WeaponGripTrack : DOTSTrack
    {
    }
}
