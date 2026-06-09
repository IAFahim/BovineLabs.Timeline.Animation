using System;
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

        [Header("Exit / Fallback Override (Optional)")]
        [Tooltip(
            "Idle/fallback clip this track latches when it is the dominant active track. Lets a stance track (e.g. crouch) own the idle so movement falls back to its idle, not the default standing idle. Highest LayerIndex wins among simultaneously active overrides; the latch persists until another override track takes over.")]
        public AnimationClip ExitIdleClip;

        [Tooltip("Time in seconds to blend into this fallback clip.")] [Min(0.001f)]
        public float BlendInDuration = 0.25f;

        [Tooltip("Time in seconds to blend out of this fallback clip.")] [Min(0.001f)]
        public float BlendOutDuration = 0.25f;

        [Tooltip("How the fallback animation wraps.")]
        public FallbackPlaybackMode FallbackPlaybackMode = FallbackPlaybackMode.Loop;

#if UNITY_EDITOR
        /// <summary>
        ///     In edit mode, create a native AnimationMixerPlayable and connect it
        ///     to an AnimationPlayableOutput targeting the bound Animator.
        ///     DOTSTrack doesn't produce AnimationPlayableOutput automatically,
        ///     so we must create it manually for editor preview.
        /// </summary>
        public override Playable CreateTrackMixer(PlayableGraph graph, GameObject go, int inputCount)
        {
            if (!Application.isPlaying)
            {
                var mixer = AnimationMixerPlayable.Create(graph, inputCount);

                // Resolve the Animator from the binding (may be RigDefinitionAuthoring on same GO)
                var director = go.GetComponent<PlayableDirector>();
                var rawBinding = director != null ? director.GetGenericBinding(this) : null;
                var animator = rawBinding as Animator ?? (rawBinding as Component)?.GetComponent<Animator>();
                if (animator != null)
                {
                    animator.cullingMode = 0; // AlwaysAnimate

                    var output = AnimationPlayableOutput.Create(graph, name, animator);
                    output.SetSourcePlayable(mixer);
                    output.SetWeight(1.0f);
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
                Debug.LogWarning(
                    $"[RukhankaAnimationTrack] '{name}' has no RigDefinitionAuthoring binding — animation data will not be baked.");
                base.Bake(context);
                return;
            }

            var baker = context.Baker;
            var trackEntity = context.TrackEntity;

            // Handle Avatar Mask Baking
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

            // Bake Track Data
            baker.AddComponent(trackEntity, new RukhankaSingleTrackData
            {
                LayerIndex = LayerIndex,
                TrackPositionOffset = trackOffset == TrackOffset.ApplyTransformOffsets ? positionOffset : Vector3.zero,
                TrackRotationOffset = trackOffset == TrackOffset.ApplyTransformOffsets
                    ? Quaternion.Euler(eulerAnglesOffset)
                    : Quaternion.identity,
                ApplyAvatarMask = applyAvatarMask,
                AvatarMaskHash = avatarMaskHash
            });

            if (ExitIdleClip != null && ExitIdleClip.TryComputeHash(rigDef.GetAvatar(), out var exitIdleHash))
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
                    RotationOffset = trackOffset == TrackOffset.ApplyTransformOffsets
                        ? Quaternion.Euler(eulerAnglesOffset)
                        : Quaternion.identity,
                    RemoveStartOffset = true,
                    ApplyFootIK = true
                });

            // Bake clips
            var clipsToBake = GetClips()
                .Select(c => c.asset as RukhankaAnimationClip)
                .Where(h => h?.animationClipHolder != null)
                .Select(h => h.animationClipHolder)
                .ToHashSet();

            if (ExitIdleClip != null)
                clipsToBake.Add(ExitIdleClip);

            if (clipsToBake.Count > 0)
            {
                var bakedAnimations = new AnimationClipBaker().BakeAnimations(
                    baker, clipsToBake.ToArray(), rigDef.GetAvatar(), rigDef.gameObject);

                var e = baker.CreateAdditionalEntity(TransformUsageFlags.None, false, name + "_AnimationAssets");
                var buffer = baker.AddBuffer<NewBlobAssetDatabaseRecord<AnimationClipBlob>>(e);
                buffer.AddValidAnimations(bakedAnimations);

                if (bakedAnimations.IsCreated) bakedAnimations.Dispose();
            }

            base.Bake(context);
        }
    }
}