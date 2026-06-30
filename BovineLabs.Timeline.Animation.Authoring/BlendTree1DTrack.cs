using System;
using System.Collections.Generic;
using System.ComponentModel;
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
    [TrackClipType(typeof(BlendTree1DClip))]
    [TrackColor(0.20f, 0.85f, 0.70f)]
    [TrackBindingType(typeof(Animator))]
    [DisplayName("BovineLabs/Animation/Blend Tree 1D")]
    public class BlendTree1DTrack : DOTSTrack
    {
        [Tooltip(
            "Layer index. 0 = base (full body). Put each masked region on its own layer >= 1 so it overrides only its masked bones over the layers below. Two clips that should play on different body parts at full strength (e.g. upper body + lower body, or left arm + right arm) must be on different layers, each with its own Avatar Mask.")]
        public int LayerIndex;

        [Header("Track Offsets")] public TrackOffset trackOffset = TrackOffset.ApplyTransformOffsets;

        public Vector3 positionOffset = Vector3.zero;
        public Vector3 eulerAnglesOffset = Vector3.zero;

        [Header("Avatar Mask")] public AvatarMask avatarMask;

        public bool applyAvatarMask = true;

        [Header("Exit / Fallback Override (Optional)")]
        [Tooltip(
            "Animation clip to play as fallback when no timeline clips are active on this track's target. Overrides the default fallback set on TimelineAnimationStateAuthoring.")]
        public AnimationClip ExitIdleClip;

        [Tooltip("Time in seconds to blend into this fallback clip.")] [Min(0.001f)]
        public float BlendInDuration = 0.25f;

        [Tooltip("Time in seconds to blend out of this fallback clip.")] [Min(0.001f)]
        public float BlendOutDuration = 0.25f;

        [Tooltip("How the fallback animation wraps.")]
        public FallbackPlaybackMode FallbackPlaybackMode = FallbackPlaybackMode.Loop;

        [Tooltip(
            "Motion entries that define the blend tree. Each entry maps an animation clip to a single scalar threshold (e.g. speed). Entries are baked sorted ascending by threshold.")]
        public List<BlendTree1DMotionEntry> Motions = new();

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
                    animator.cullingMode = 0;

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
            if (trackOffset != TrackOffset.ApplyTransformOffsets)
            {
                Debug.LogWarning(
                    $"[BlendTree1DTrack] '{name}' uses Track Offset mode '{trackOffset}', which is not supported in DOTS — offsets are ignored. Use 'Apply Transform Offsets'.");
            }

            var director = context.Director;
            var rigDef = director.ResolveRigDefinition(this);

            if (rigDef == null)
            {
                Debug.LogWarning(
                    $"[BlendTree1DTrack] '{name}' has no RigDefinitionAuthoring binding — animation data will not be baked.");
                base.Bake(context);
                return;
            }

            var baker = context.Baker;
            var trackEntity = context.TrackEntity;
            var avatar = rigDef.GetAvatar();

            Hash128 avatarMaskHash = default;
            if (applyAvatarMask && avatarMask != null)
            {
                var maskBaker = new AvatarMaskBaker();
                var maskBlob = maskBaker.CreateAvatarMaskBlob(baker, avatarMask, rigDef);
                avatarMaskHash = maskBlob.Value.hash;
                baker.AddBuffer<AvatarMaskBakingData>(trackEntity).Add(new AvatarMaskBakingData
                    { rigEntity = baker.GetEntity(rigDef, TransformUsageFlags.Dynamic), dataBlob = maskBlob });
            }

            baker.AddComponent(trackEntity, new BlendAnimationTree1DTrackData
            {
                LayerIndex = LayerIndex,
                TrackPositionOffset = trackOffset == TrackOffset.ApplyTransformOffsets ? positionOffset : Vector3.zero,
                TrackRotationOffset = trackOffset == TrackOffset.ApplyTransformOffsets
                    ? Quaternion.Euler(eulerAnglesOffset)
                    : Quaternion.identity,
                ApplyAvatarMask = applyAvatarMask,
                AvatarMaskHash = avatarMaskHash
            });

            // Rukhanka's ComputeBlendTree1D requires thresholds sorted ascending; bake them that way.
            var sortedMotions = new List<BlendTree1DMotionEntry>();
            foreach (var motion in Motions)
            {
                if (motion.clip == null) continue;
                sortedMotions.Add(motion);
            }

            sortedMotions.Sort((a, b) => a.threshold.CompareTo(b.threshold));

            var motionBuffer = baker.AddBuffer<BlendTree1DMotionData>(trackEntity);
            var clipsToBake = new List<AnimationClip>();
            var index = 0;

            foreach (var motion in sortedMotions)
            {
                motionBuffer.Add(new BlendTree1DMotionData
                {
                    AnimationHash = BakingUtils.ComputeAnimationHash(motion.clip, avatar),
                    Threshold = motion.threshold,
                    MotionIndex = index++
                });
                clipsToBake.Add(motion.clip);
            }

            if (ExitIdleClip != null)
            {
                baker.AddComponent(trackEntity, new TrackFallbackOverride
                {
                    FallbackClipHash = BakingUtils.ComputeAnimationHash(ExitIdleClip, avatar),
                    TrackOrder = FallbackTrackOrder.Compute(this),
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
                clipsToBake.Add(ExitIdleClip);
            }

            if (clipsToBake.Count > 0)
            {
                var bakedAnimations =
                    new AnimationClipBaker().BakeAnimations(baker, clipsToBake.ToArray(), avatar, rigDef.gameObject);
                var e = baker.CreateAdditionalEntity(TransformUsageFlags.None, false, name + "_BlendTree1DAssets");
                var dbBuffer = baker.AddBuffer<NewBlobAssetDatabaseRecord<AnimationClipBlob>>(e);
                dbBuffer.AddValidAnimations(bakedAnimations);

                if (bakedAnimations.IsCreated) bakedAnimations.Dispose();
            }

            base.Bake(context);
        }

        [Serializable]
        public class BlendTree1DMotionEntry
        {
            [Tooltip("Animation clip for this motion entry.")]
            public AnimationClip clip;

            [Tooltip("Scalar threshold at which this motion is fully active (e.g. 0 = idle, 1 = full speed).")]
            public float threshold;
        }
    }
}
