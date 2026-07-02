using BovineLabs.Core.Authoring.EntityCommands;
using BovineLabs.Essence.Authoring;
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

        [Header("Max Speed Stat (optional)")]
        [Tooltip("If set, maxSpeed is multiplied by this stat's value (e.g. a MovementSpeed stat). Point this and the " +
                 "movement force at the same stat so the achievable speed and the blend normalization scale together — " +
                 "then buffs/slows rescale both and the blend never desyncs. Leave empty to use the constant maxSpeed.")]
        public StatSchemaObject maxSpeedStat;

        [Tooltip("Entity link that resolves the entity carrying the Max Speed Stat (e.g. Essence Link). Empty = read it " +
                 "from the bound entity itself.")]
        public EntityLinkSchema maxSpeedReadFrom;

        [Header("Clip Transform Offsets")] public Vector3 positionOffset = Vector3.zero;

        public Vector3 eulerAnglesOffset = Vector3.zero;

        [Space] [Tooltip("Removes the starting offset of the animation so it begins exactly at the track's offset.")]
        public bool removeStartOffset = true;

        public bool applyFootIK = true;

        public ClipCaps clipCaps => ClipCaps.All;

#if UNITY_EDITOR
        // Back-reference stamped by BlendTree2DTrack.CreateTrackMixer so the edit-mode preview can reach the track's
        // Motions (a clip's PlayableAsset otherwise has no clean handle on its owning track). Not serialized.
        [System.NonSerialized] internal BlendTree2DTrack EditorPreviewTrack;

        public override Playable CreatePlayable(PlayableGraph graph, GameObject owner)
        {
            if (!Application.isPlaying)
            {
                var motions = EditorPreviewTrack?.Motions;

                // No track back-ref or no motions: fall back to the empty mixer (bind pose), exactly as before.
                if (motions == null || motions.Count == 0)
                    return AnimationMixerPlayable.Create(graph);

                // Nearest-neighbor approximation, NOT the runtime weighted blend: pick the single motion whose 2D
                // position is closest (Euclidean) to the sample point and preview just that clip. The sample point is
                // the authored BlendParameter for ClipValue; for velocity/link-driven kinds there is no live value in
                // edit mode, so we sample the origin (idle). Trust runtime for the true blended pose.
                var sample = ReadKind == BlendDirectionReadKind.ClipValue ? BlendParameter : float2.zero;

                AnimationClip dominant = null;
                var bestDistanceSq = float.MaxValue;

                foreach (var motion in motions)
                {
                    if (motion?.clip == null)
                        continue;

                    var pos = motion.CalcDirection();
                    var d = new float2(pos.x, pos.y) - sample;
                    var distSq = math.lengthsq(d);

                    if (distSq < bestDistanceSq)
                    {
                        bestDistanceSq = distSq;
                        dominant = motion.clip;
                    }
                }

                if (dominant == null)
                    return AnimationMixerPlayable.Create(graph);

                var asset = CreateInstance<AnimationPlayableAsset>();
                asset.clip = dominant;
                asset.applyFootIK = applyFootIK;
                asset.removeStartOffset = removeStartOffset;
                asset.position = positionOffset;
                asset.rotation = Quaternion.Euler(eulerAnglesOffset);
                return asset.CreatePlayable(graph, owner);
            }

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

            ushort maxSpeedStatLinkKey = 0;
            if (maxSpeedReadFrom != null)
            {
                EntityLinkAuthoringUtility.TryGetKey(maxSpeedReadFrom, out maxSpeedStatLinkKey);
            }

            var builder = new BlendTree2DBuilder
            {
                BlendParameter = BlendParameter,
                ReadKind = ReadKind,
                ReadLinkKey = readLinkKey,
                MaxSpeed = maxSpeed,
                MaxSpeedStat = maxSpeedStat != null ? maxSpeedStat.Key : default,
                MaxSpeedStatLinkKey = maxSpeedStatLinkKey,
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