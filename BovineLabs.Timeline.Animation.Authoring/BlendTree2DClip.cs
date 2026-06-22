using BovineLabs.Core.Authoring.EntityCommands;
using BovineLabs.Timeline.Animation.Data.Builders;
using BovineLabs.Timeline.Authoring;
using BovineLabs.Timeline.EntityLinks.Authoring;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace BovineLabs.Timeline.Animation.Authoring
{
    public sealed class BlendTree2DClip : DOTSClip, ITimelineClipAsset
    {
        public float2 BlendParameter;
        public BlendDirectionReadKind ReadKind;
        public EntityLinkSchema ReadFrom;

        [Tooltip(
            "Speed (m/s) mapped to the outer edge of the blend space when ReadKind reads physics velocity. " +
            "Velocity is rotated into the character's facing and divided by this, so radius 0 = idle and radius 1 = this speed.")]
        [Min(0.001f)]
        public float maxSpeed = 5f;

        [Header("Clip Transform Offsets")] public Vector3 positionOffset = Vector3.zero;

        public Vector3 eulerAnglesOffset = Vector3.zero;

        [Space] [Tooltip("Removes the starting offset of the animation so it begins exactly at the track's offset.")]
        public bool removeStartOffset = true;

        public bool applyFootIK = true;

        public ClipCaps clipCaps => ClipCaps.All;

#if UNITY_EDITOR

        public override Playable CreatePlayable(PlayableGraph graph, GameObject owner)
        {
            if (!Application.isPlaying)
                return AnimationMixerPlayable.Create(graph);

            return base.CreatePlayable(graph, owner);
        }
#endif

        public override void Bake(Entity clipEntity, BakingContext context)
        {
            ushort readLinkKey = 0;
            if (ReadKind != BlendDirectionReadKind.ClipValue)
            {
                if (ReadFrom == null)
                {
                    Debug.LogError($"{nameof(BlendTree2DClip)} '{name}' needs {nameof(ReadFrom)}.");
                    base.Bake(clipEntity, context);
                    return;
                }

                if (!EntityLinkAuthoringUtility.TryGetKey(ReadFrom, out var key))
                {
                    Debug.LogError(
                        $"{nameof(BlendTree2DClip)} '{name}' could not resolve key for '{ReadFrom.name}'.");
                    base.Bake(clipEntity, context);
                    return;
                }

                readLinkKey = key;
            }

            var builder = new BlendTree2DBuilder
            {
                BlendParameter = BlendParameter,
                ReadKind = ReadKind,
                ReadLinkKey = readLinkKey,
                MaxSpeed = maxSpeed,
                PositionOffset = positionOffset,
                RotationOffset = Quaternion.Euler(eulerAnglesOffset),
                RemoveStartOffset = removeStartOffset,
                ApplyFootIK = applyFootIK
            };
            var commands = new BakerCommands(context.Baker, clipEntity);
            builder.ApplyTo(ref commands);

            base.Bake(clipEntity, context);
        }
    }
}