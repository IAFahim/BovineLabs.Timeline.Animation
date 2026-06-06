using System;
using System.ComponentModel;
using BovineLabs.Core.Authoring.EntityCommands;
using BovineLabs.Timeline.Animation.Data.Builders;
using BovineLabs.Timeline.Authoring;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace BovineLabs.Timeline.Animation.Authoring
{
    [Serializable]
    [TrackClipType(typeof(AfterImageClip))]
    [TrackColor(0.85f, 0.20f, 0.70f)]
    [TrackBindingType(typeof(Animator))]
    [DisplayName("BovineLabs/Animation/After Image")]
    public class AfterImageTrack : DOTSTrack
    {
        [Tooltip("Prefab with RigDefinitionAuthoring sharing the same Avatar as the source rig.")]
        public GameObject afterImagePrefab;

#if UNITY_EDITOR
        public override Playable CreateTrackMixer(PlayableGraph graph, GameObject go, int inputCount)
        {
            if (!Application.isPlaying)
                return AnimationMixerPlayable.Create(graph, inputCount);

            return base.CreateTrackMixer(graph, go, inputCount);
        }
#endif

        protected override void Bake(BakingContext context)
        {
            if (afterImagePrefab == null)
            {
                Debug.LogWarning(
                    $"[AfterImageTrack] '{name}' has no afterImagePrefab assigned — after images will not spawn.");
                base.Bake(context);
                return;
            }

            var builder = new AfterImageTrackBuilder
            {
                Prefab = context.Baker.GetEntity(afterImagePrefab, TransformUsageFlags.Dynamic)
            };
            var commands = new BakerCommands(context.Baker, context.TrackEntity);
            builder.ApplyTo(ref commands);

            base.Bake(context);
        }
    }
}
