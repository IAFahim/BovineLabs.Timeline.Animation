using System;
using System.ComponentModel;
using BovineLabs.Timeline.Authoring;
using UnityEngine;
using UnityEngine.Timeline;

namespace BovineLabs.Timeline.Animation.Authoring
{
    [Serializable]
    [TrackClipType(typeof(CharacterLookAtClip))]
    [TrackBindingType(typeof(Animator))]
    [TrackColor(0.95f, 0.7f, 0.2f)]
    [DisplayName("BovineLabs/Animation/Look At")]
    public class CharacterLookAtTrack : DOTSTrack
    {
    }
}