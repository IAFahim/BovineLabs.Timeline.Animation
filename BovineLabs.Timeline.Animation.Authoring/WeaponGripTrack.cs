using System;
using System.ComponentModel;
using BovineLabs.Timeline.Authoring;
using UnityEngine;
using UnityEngine.Timeline;

namespace BovineLabs.Timeline.Animation.Authoring
{
    /// <summary> Bind the weapon GameObject; clips select which authored grip the weapon anchors to. </summary>
    [Serializable]
    [TrackClipType(typeof(WeaponGripClip))]
    [TrackColor(0.95f, 0.60f, 0.15f)]
    [TrackBindingType(typeof(GameObject))]
    [DisplayName("BovineLabs/Animation/Weapon Grip")]
    public class WeaponGripTrack : DOTSTrack
    {
    }
}
