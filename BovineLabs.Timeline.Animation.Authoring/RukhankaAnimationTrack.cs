using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using BovineLabs.Timeline.Authoring;
using Rukhanka;
using Rukhanka.Hybrid;
using Unity.Entities;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.Timeline;
using Component = UnityEngine.Component;
using Hash128 = Unity.Entities.Hash128;

namespace BovineLabs.Timeline.Animation.Authoring
{
    [Serializable]
    [TrackClipType(typeof(RukhankaAnimationClip))]
    [TrackBindingType(typeof(Animator))]
    [TrackColor(0.55f, 0.35f, 0.95f)]
    [DisplayName("BovineLabs/Animation/Rukhanka Clip")]
    public class RukhankaAnimationTrack : DOTSTrack
    {
        [Tooltip(
            "Layer index. 0 = base (full body). Put each masked region on its own layer >= 1 so it overrides only its masked bones over the layers below. Two clips that should play on different body parts at full strength (e.g. upper body + lower body, or left arm + right arm) must be on different layers, each with its own Avatar Mask.")]
        public int LayerIndex;

        [Header("Track Offsets")]
        [Tooltip(
            "How track offsets are applied. In DOTS, ApplyTransformOffsets is the standard deterministic approach.")]
        public TrackOffset trackOffset = TrackOffset.ApplyTransformOffsets;

        public Vector3 positionOffset = Vector3.zero;

        public Vector3 eulerAnglesOffset = Vector3.zero;

        [Header("Avatar Mask")] public AvatarMask avatarMask;

        public bool applyAvatarMask = true;

        [Header("Blending")]
        [Tooltip(
            "How this track's clips combine with the layers below. Override replaces the pose (standard). Additive layers this clip's motion on top of lower layers — use for recoil, breathing, or lean poses authored as additive clips.")]
        public AnimationBlendingMode BlendMode = AnimationBlendingMode.Override;

        [Header("Exit / Fallback Override (Optional)")]
        [Tooltip(
            "Idle/fallback clip this track latches when it is the dominant active track. Lets a stance track own the idle so movement falls back to its idle, not the default idle. Highest LayerIndex wins among simultaneously active overrides; the latch persists until another override track takes over.")]
        public AnimationClip ExitIdleClip;

        [Tooltip("Time in seconds to blend into this fallback clip. 0 = instant cut.")] [Min(0f)]
        public float BlendInDuration = 0.25f;

        [Tooltip("Time in seconds to blend out of this fallback clip. 0 = instant cut.")] [Min(0f)]
        public float BlendOutDuration = 0.25f;

        [Tooltip("How the fallback animation wraps.")]
        public FallbackPlaybackMode FallbackPlaybackMode = FallbackPlaybackMode.Loop;

        private Vector3 OffsetPosition =>
            trackOffset == TrackOffset.ApplyTransformOffsets ? positionOffset : Vector3.zero;

        private Quaternion OffsetRotation =>
            trackOffset == TrackOffset.ApplyTransformOffsets
                ? Quaternion.Euler(eulerAnglesOffset)
                : Quaternion.identity;

#if UNITY_EDITOR
        public override Playable CreateTrackMixer(PlayableGraph graph, GameObject go, int inputCount)
        {
            if (!Application.isPlaying)
            {
                var mixer = AnimationMixerPlayable.Create(graph, inputCount);
                var director = go != null ? go.GetComponent<PlayableDirector>() : null;
                var rawBinding = director != null ? director.GetGenericBinding(this) : null;
                var animator = rawBinding as Animator ?? (rawBinding as Component)?.GetComponent<Animator>();

                if (animator != null)
                {
                    var output = AnimationPlayableOutput.Create(graph, name, animator);
                    output.SetSourcePlayable(mixer);
                    output.SetWeight(1f);
                }

                return mixer;
            }

            return base.CreateTrackMixer(graph, go, inputCount);
        }
#endif

        protected override void Bake(BakingContext context)
        {
            if (trackOffset != TrackOffset.ApplyTransformOffsets)
            {
                Debug.LogWarning(
                    $"[RukhankaAnimationTrack] '{name}' uses Track Offset mode '{trackOffset}', which is not supported in DOTS — offsets are ignored. Use 'Apply Transform Offsets'.");
            }

            var rigDef = context.Director.ResolveRigDefinition(this);

            if (rigDef == null)
            {
                Debug.LogWarning(
                    $"[RukhankaAnimationTrack] '{name}' has no RigDefinitionAuthoring binding — animation data will not be baked.");
                base.Bake(context);
                return;
            }

            var avatar = rigDef.GetAvatar();

            if (avatar == null)
            {
                Debug.LogWarning(
                    $"[RukhankaAnimationTrack] '{name}' rig '{rigDef.name}' has no Avatar — animation data will not be baked.");
                base.Bake(context);
                return;
            }

            var baker = context.Baker;
            var trackEntity = context.TrackEntity;

            var avatarMaskHash = BakeAvatarMask(baker, trackEntity, rigDef);

            baker.AddComponent(trackEntity, new RukhankaSingleTrackData
            {
                LayerIndex = LayerIndex,
                TrackPositionOffset = OffsetPosition,
                TrackRotationOffset = OffsetRotation,
                ApplyAvatarMask = applyAvatarMask,
                AvatarMaskHash = avatarMaskHash,
                BlendMode = BlendMode
            });

            BakeFallbackOverride(baker, trackEntity, avatar, avatarMaskHash);

            var clipComponents = GetClips()
                .Select(c => c.asset as RukhankaAnimationClip)
                .Where(h => h?.animationClipHolder != null)
                .ToList();

            var footIkOnClips = clipComponents.Where(h => h.applyFootIK).Select(h => h.animationClipHolder).ToHashSet();
            var footIkOffClips = clipComponents.Where(h => !h.applyFootIK).Select(h => h.animationClipHolder)
                .ToHashSet();

            if (ExitIdleClip != null) footIkOnClips.Add(ExitIdleClip);

            if (footIkOnClips.Count > 0 || footIkOffClips.Count > 0)
            {
                var e = baker.CreateAdditionalEntity(TransformUsageFlags.None, false, name + "_AnimationAssets");
                var buffer = baker.AddBuffer<NewBlobAssetDatabaseRecord<AnimationClipBlob>>(e);

                // Rukhanka sources the additive reference pose from each clip's own AnimationClipSettings at bake time,
                // so honor a per-clip override by temporarily applying it around the bake. Only relevant for Additive
                // tracks; Override tracks never read additiveReferencePoseFrame, so leave settings untouched there.
                var refPoseByClip = BuildReferencePoseMap(clipComponents);

                BakeClipVariant(footIkOnClips, true);
                BakeClipVariant(footIkOffClips, false);

                void BakeClipVariant(HashSet<AnimationClip> clips, bool applyFootIK)
                {
                    if (clips.Count == 0) return;

                    var restores = ApplyReferencePoseOverrides(clips, refPoseByClip);
                    try
                    {
                        var baked = new AnimationClipBaker().BakeAnimations(
                            baker, clips.ToArray(), avatar, rigDef.gameObject, applyFootIK);
                        buffer.AddValidAnimations(baked);

                        if (baked.IsCreated) baked.Dispose();
                    }
                    finally
                    {
                        foreach (var (clip, settings) in restores)
                            AnimationUtility.SetAnimationClipSettings(clip, settings);
                    }
                }
            }

            base.Bake(context);
        }

        // Maps each source AnimationClip to the clip component that wants a custom additive reference pose. Only built
        // for Additive tracks (Override never reads the reference-pose frame), so Override clips bake unchanged.
        private Dictionary<AnimationClip, RukhankaAnimationClip> BuildReferencePoseMap(
            List<RukhankaAnimationClip> clipComponents)
        {
            if (BlendMode != AnimationBlendingMode.Additive)
                return null;

            Dictionary<AnimationClip, RukhankaAnimationClip> map = null;

            foreach (var c in clipComponents)
            {
                if (c.additiveReferencePoseClip == null || c.animationClipHolder == null)
                    continue;

                map ??= new Dictionary<AnimationClip, RukhankaAnimationClip>();

                if (!map.TryAdd(c.animationClipHolder, c))
                {
                    Debug.LogWarning(
                        $"[RukhankaAnimationTrack] '{name}' has multiple clips using animation '{c.animationClipHolder.name}' " +
                        "with different additive reference poses; only the first is honored (Rukhanka bakes one blob per clip).");
                }
            }

            return map;
        }

        // Temporarily writes the chosen reference pose into each clip's AnimationClipSettings so Rukhanka's baker picks it
        // up, returning the originals so the caller can restore them after baking. Returns empty when nothing to override.
        private static List<(AnimationClip clip, AnimationClipSettings settings)> ApplyReferencePoseOverrides(
            HashSet<AnimationClip> clips, Dictionary<AnimationClip, RukhankaAnimationClip> refPoseByClip)
        {
            var restores = new List<(AnimationClip, AnimationClipSettings)>();

            if (refPoseByClip == null)
                return restores;

            foreach (var clip in clips)
            {
                if (!refPoseByClip.TryGetValue(clip, out var src))
                    continue;

                // GetAnimationClipSettings returns a fresh instance each call, so 'original' is unaffected by the edit.
                var original = AnimationUtility.GetAnimationClipSettings(clip);
                var overridden = AnimationUtility.GetAnimationClipSettings(clip);
                overridden.additiveReferencePoseClip = src.additiveReferencePoseClip;
                overridden.additiveReferencePoseTime = src.additiveReferencePoseTime;
                AnimationUtility.SetAnimationClipSettings(clip, overridden);

                restores.Add((clip, original));
            }

            return restores;
        }

        private Hash128 BakeAvatarMask(IBaker baker, Entity trackEntity, RigDefinitionAuthoring rigDef)
        {
            if (!applyAvatarMask || avatarMask == null) return default;

            var maskBaker = new AvatarMaskBaker();
            var maskBlob = maskBaker.CreateAvatarMaskBlob(baker, avatarMask, rigDef);
            var maskData = new AvatarMaskBakingData
            {
                rigEntity = baker.GetEntity(rigDef, TransformUsageFlags.Dynamic),
                dataBlob = maskBlob
            };

            baker.AddBuffer<AvatarMaskBakingData>(trackEntity).Add(maskData);

            return maskBlob.Value.hash;
        }

        private void BakeFallbackOverride(IBaker baker, Entity trackEntity, Avatar avatar, Hash128 avatarMaskHash)
        {
            if (ExitIdleClip == null || !ExitIdleClip.TryComputeHash(avatar, out var exitIdleHash)) return;

            baker.AddComponent(trackEntity, new TrackFallbackOverride
            {
                FallbackClipHash = exitIdleHash,
                TrackOrder = FallbackTrackOrder.Compute(this),
                BlendInSpeed = BlendLayerMath.DurationToSpeed(BlendInDuration),
                BlendOutSpeed = BlendLayerMath.DurationToSpeed(BlendOutDuration),
                PlaybackMode = FallbackPlaybackMode,
                LayerIndex = LayerIndex,
                BlendMode = BlendMode,
                AvatarMaskHash = avatarMaskHash,
                PositionOffset = OffsetPosition,
                RotationOffset = OffsetRotation,
                RemoveStartOffset = true,
                ApplyFootIK = true
            });
        }
    }
}