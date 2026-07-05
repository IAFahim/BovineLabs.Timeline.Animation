using System;
using System.ComponentModel;
using BovineLabs.Timeline.Authoring;
using UnityEngine;
using UnityEngine.Timeline;

namespace BovineLabs.Timeline.Animation.Authoring
{
    /// <summary>
    /// Drop a <see cref="RagdollClip"/> on this track (bound to a Rukhanka rig's Animator) to ragdoll the
    /// character for the clip's duration. Requires the rig to have been made ragdoll-ready by the Ragdoll
    /// generator (Tools/BovineLabs/Ragdoll). No per-track config — the whole rig is the target.
    /// </summary>
    [Serializable]
    [TrackClipType(typeof(RagdollClip))]
    [TrackBindingType(typeof(Animator))]
    [TrackColor(0.85f, 0.2f, 0.2f)]
    [DisplayName("BovineLabs/Animation/Ragdoll")]
    public class RagdollTrack : DOTSTrack
    {
    }
}
