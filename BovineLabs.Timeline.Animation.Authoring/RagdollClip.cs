using BovineLabs.Core.Authoring.EntityCommands;
using BovineLabs.Timeline.Authoring;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Timeline;

namespace BovineLabs.Timeline.Animation.Authoring
{
    /// <summary>
    /// While active, ragdolls the bound rig. When it ends, the rig returns to animation — unless
    /// <see cref="stayRagdolled"/> is set, which latches the ragdoll on permanently (e.g. a death).
    /// </summary>
    public sealed class RagdollClip : DOTSClip, ITimelineClipAsset
    {
        [Tooltip("Leave the character ragdolled after this clip ends instead of returning to animation.")]
        public bool stayRagdolled;

        public override double duration => 1;

        public ClipCaps clipCaps => ClipCaps.None;

        public override void Bake(Entity clipEntity, BakingContext context)
        {
            var commands = new BakerCommands(context.Baker, clipEntity);
            commands.AddComponent(new RagdollClipTag { Latch = stayRagdolled });

            base.Bake(clipEntity, context);
        }
    }
}
