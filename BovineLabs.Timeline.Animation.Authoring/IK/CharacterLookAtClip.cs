using BovineLabs.Core.Authoring.EntityCommands;
using BovineLabs.Reaction.Data.Core;
using BovineLabs.Timeline.Animation.Data.Builders;
using BovineLabs.Timeline.Authoring;
using BovineLabs.Timeline.EntityLinks.Authoring;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Timeline;

namespace BovineLabs.Timeline.Animation.Authoring
{
    public class CharacterLookAtClip : DOTSClip, ITimelineClipAsset
    {
        [Header("Source")]
        [Tooltip(
            "Where the look-at point comes from. LinkedTarget resolves an EntityLink; StaticWorld uses a fixed world point; OwnerOffset offsets the bound character.")]
        public PointSourceMode sourceMode = PointSourceMode.LinkedTarget;

        [Tooltip("Which target slot to read the link root from when resolving LinkedTarget.")]
        public Target readRootFrom = Target.Self;

        [Tooltip("Link to the entity to look at (LinkedTarget mode).")]
        public EntityLinkSchema lookTargetLink;

        [Header("Static / Offset")] [Tooltip("World point to look at (StaticWorld mode).")]
        public Vector3 staticWorldPoint;

        [Tooltip("Local offset from the bound character to look at (OwnerOffset mode).")]
        public Vector3 ownerLocalOffset;

        [Header("Influence")] [Range(0f, 1f)] public float weight = 1f;

        [Header("Angle Limits (degrees)")] public float angleLimitMin = -80f;

        public float angleLimitMax = 80f;

        public override double duration => 1;
        public ClipCaps clipCaps => ClipCaps.Blending;

        public override void Bake(Entity clipEntity, BakingContext context)
        {
            var commands = new BakerCommands(context.Baker, clipEntity);

            ushort key = 0;
            if (lookTargetLink != null && EntityLinkAuthoringUtility.TryGetKey(lookTargetLink, out var k))
                key = k;

            if (sourceMode == PointSourceMode.LinkedTarget && key == 0)
                Debug.LogError(
                    $"{nameof(CharacterLookAtClip)} '{name}' uses LinkedTarget but lookTargetLink is unassigned or unresolved; the look-at will do nothing.",
                    this);

            float3 staticOrOffset = sourceMode switch
            {
                PointSourceMode.StaticWorld => staticWorldPoint,
                PointSourceMode.OwnerOffset => ownerLocalOffset,
                _ => float3.zero
            };

            var builder = new CharacterLookAtBuilder
            {
                AuthoredData = new CharacterLookAtData
                {
                    LookPoint = float3.zero,
                    Weight = weight,
                    AngleLimits = math.radians(new float2(angleLimitMin, angleLimitMax)),
                    SourceMode = sourceMode,
                    TargetLinkKey = key,
                    ReadRootFrom = readRootFrom,
                    StaticOrOffsetPoint = staticOrOffset
                }
            };
            builder.ApplyTo(ref commands);

            base.Bake(clipEntity, context);
        }
    }
}