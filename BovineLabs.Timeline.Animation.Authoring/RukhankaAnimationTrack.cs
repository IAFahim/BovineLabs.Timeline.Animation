using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using BovineLabs.Timeline.Authoring;
using Rukhanka;
using Rukhanka.Hybrid;
using Unity.Entities;
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
        [Tooltip("Layer index. 0 = base (full body). Put each masked region on its own layer >= 1 so it overrides only its masked bones over the layers below. Two clips that should play on different body parts at full strength (e.g. upper body + lower body, or left arm + right arm) must be on different layers, each with its own Avatar Mask.")]
        public int LayerIndex;

        [Header("Track Offsets")]
        [Tooltip("How track offsets are applied. In DOTS, ApplyTransformOffsets is the standard deterministic approach.")]
        public TrackOffset trackOffset = TrackOffset.ApplyTransformOffsets;

        public Vector3 positionOffset = Vector3.zero;

        public Vector3 eulerAnglesOffset = Vector3.zero;

        [Header("Avatar Mask")]
        public AvatarMask avatarMask;

        public bool applyAvatarMask = true;

        [Header("Exit / Fallback Override (Optional)")]
        [Tooltip("Idle/fallback clip this track latches when it is the dominant active track. Lets a stance track own the idle so movement falls back to its idle, not the default idle. Highest LayerIndex wins among simultaneously active overrides; the latch persists until another override track takes over.")]
        public AnimationClip ExitIdleClip;

        [Tooltip("Time in seconds to blend into this fallback clip.")]
        [Min(0.001f)]
        public float BlendInDuration = 0.25f;

        [Tooltip("Time in seconds to blend out of this fallback clip.")]
        [Min(0.001f)]
        public float BlendOutDuration = 0.25f;

        [Tooltip("How the fallback animation wraps.")]
        public FallbackPlaybackMode FallbackPlaybackMode = FallbackPlaybackMode.Loop;

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
            var rigDef = context.Director.ResolveRigDefinition(this);

            if (rigDef == null)
            {
                Debug.LogWarning($"[RukhankaAnimationTrack] '{name}' has no RigDefinitionAuthoring binding — animation data will not be baked.");
                base.Bake(context);
                return;
            }

            var baker = context.Baker;
            var trackEntity = context.TrackEntity;
            Hash128 avatarMaskHash = default;

            if (applyAvatarMask && avatarMask != null)
            {
                var maskBaker = new AvatarMaskBaker();
                var maskBlob = maskBaker.CreateAvatarMaskBlob(baker, avatarMask, rigDef);
                avatarMaskHash = maskBlob.Value.hash;
                var maskData = new AvatarMaskBakingData
                {
                    rigEntity = baker.GetEntity(rigDef, TransformUsageFlags.Dynamic),
                    dataBlob = maskBlob
                };

                baker.AddBuffer<AvatarMaskBakingData>(trackEntity).Add(maskData);
            }

            baker.AddComponent(trackEntity, new RukhankaSingleTrackData
            {
                LayerIndex = LayerIndex,
                TrackPositionOffset = trackOffset == TrackOffset.ApplyTransformOffsets ? positionOffset : Vector3.zero,
                TrackRotationOffset = trackOffset == TrackOffset.ApplyTransformOffsets ? Quaternion.Euler(eulerAnglesOffset) : Quaternion.identity,
                ApplyAvatarMask = applyAvatarMask,
                AvatarMaskHash = avatarMaskHash
            });

            if (ExitIdleClip != null && ExitIdleClip.TryComputeHash(rigDef.GetAvatar(), out var exitIdleHash))
            {
                baker.AddComponent(trackEntity, new TrackFallbackOverride
                {
                    FallbackClipHash = exitIdleHash,
                    BlendInSpeed = 1f / Mathf.Max(0.001f, BlendInDuration),
                    BlendOutSpeed = 1f / Mathf.Max(0.001f, BlendOutDuration),
                    PlaybackMode = FallbackPlaybackMode,
                    LayerIndex = LayerIndex,
                    BlendMode = AnimationBlendingMode.Override,
                    AvatarMaskHash = avatarMaskHash,
                    PositionOffset = trackOffset == TrackOffset.ApplyTransformOffsets ? positionOffset : Vector3.zero,
                    RotationOffset = trackOffset == TrackOffset.ApplyTransformOffsets ? Quaternion.Euler(eulerAnglesOffset) : Quaternion.identity,
                    RemoveStartOffset = true,
                    ApplyFootIK = true
                });
            }

            var clipComponents = GetClips()
                .Select(c => c.asset as RukhankaAnimationClip)
                .Where(h => h?.animationClipHolder != null)
                .ToList();

            // Foot IK is baked into the clip blob, so a clip's applyFootIK flag selects which blob variant to
            // bake/reference (see ComputeAnimationHash overload). Group by flag; the same clip asset used both
            // with and without foot IK is baked in BOTH variants (different hashes), so do not merge the sets.
            var footIkOnClips = clipComponents.Where(h => h.applyFootIK).Select(h => h.animationClipHolder).ToHashSet();
            var footIkOffClips = clipComponents.Where(h => !h.applyFootIK).Select(h => h.animationClipHolder).ToHashSet();

            // ExitIdle fallback is baked foot-IK on (TrackFallbackOverride.ApplyFootIK = true above).
            if (ExitIdleClip != null)
            {
                footIkOnClips.Add(ExitIdleClip);
            }

            if (footIkOnClips.Count > 0 || footIkOffClips.Count > 0)
            {
                var e = baker.CreateAdditionalEntity(TransformUsageFlags.None, false, name + "_AnimationAssets");
                var buffer = baker.AddBuffer<NewBlobAssetDatabaseRecord<AnimationClipBlob>>(e);

                BakeClipVariant(footIkOnClips, true);
                BakeClipVariant(footIkOffClips, false);

                void BakeClipVariant(HashSet<AnimationClip> clips, bool applyFootIK)
                {
                    if (clips.Count == 0)
                    {
                        return;
                    }

                    var baked = new AnimationClipBaker().BakeAnimations(
                        baker, clips.ToArray(), rigDef.GetAvatar(), rigDef.gameObject, applyFootIK);
                    buffer.AddValidAnimations(baked);

                    if (baked.IsCreated)
                    {
                        baked.Dispose();
                    }
                }
            }

            base.Bake(context);
        }
    }
}
