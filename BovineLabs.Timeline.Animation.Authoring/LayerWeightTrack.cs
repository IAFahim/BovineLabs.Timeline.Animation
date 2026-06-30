using System;
using System.ComponentModel;
using BovineLabs.Timeline.Authoring;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Timeline;

namespace BovineLabs.Timeline.Animation.Authoring
{
    [Serializable]
    [TrackClipType(typeof(LayerWeightClip))]
    [TrackBindingType(typeof(Animator))]
    [TrackColor(0.95f, 0.75f, 0.25f)]
    [DisplayName("BovineLabs/Animation/Layer Weight")]
    public class LayerWeightTrack : DOTSTrack
    {
        [Tooltip(
            "The animation layer this track fades. Match it to the LayerIndex of the Rukhanka Clip / Blend Tree track whose layer you want to animate (e.g. an additive upper-body layer >= 1). Each active clip's timeline ease drives this layer's overall weight; with no active clip the layer keeps its normal (un-overridden) weight.")]
        public int LayerIndex;

        protected override void Bake(BakingContext context)
        {
            var baker = context.Baker;
            var trackEntity = context.TrackEntity;

            baker.AddComponent(trackEntity, new LayerWeightTrackData { LayerIndex = LayerIndex });

            // The override buffer must live on the bound actor (where the layer-mixing buffers are). Resolve the
            // actor entity here and hand it to LayerWeightActorBakingSystem, which provisions the buffer once per
            // actor — a baking system so multiple LayerWeight tracks on one actor can't add the buffer twice.
            var rigDef = context.Director.ResolveRigDefinition(this);
            if (rigDef != null)
            {
                baker.AddComponent(trackEntity, new LayerWeightActorBakeRef
                {
                    Actor = baker.GetEntity(rigDef, TransformUsageFlags.None),
                });
            }
            else
            {
                Debug.LogWarning(
                    $"[LayerWeightTrack] '{name}' has no RigDefinitionAuthoring binding — layer-weight override will not be baked.");
            }

            base.Bake(context);
        }
    }
}
