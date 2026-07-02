using System;
using System.Collections.Generic;
using System.ComponentModel;
using BovineLabs.Core.PropertyDrawers;
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
    [TrackClipType(typeof(BlendTree2DClip))]
    [TrackColor(0.20f, 0.70f, 0.85f)]
    [TrackBindingType(typeof(Animator))]
    [DisplayName("BovineLabs/Animation/Blend Tree 2D")]
    public class BlendTree2DTrack : DOTSTrack
    {
        [Tooltip(
            "Blend tree algorithm: SimpleDirectional for 1D-like with a center, FreeformCartesian for 2D positions, FreeformDirectional for 2D with polar handling.")]
        public MotionBlob.Type BlendTreeType = MotionBlob.Type.BlendTree2DSimpleDirectional;

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

        [Tooltip("Time in seconds to blend into this fallback clip. 0 = instant cut.")] [Min(0f)]
        public float BlendInDuration = 0.25f;

        [Tooltip("Time in seconds to blend out of this fallback clip. 0 = instant cut.")] [Min(0f)]
        public float BlendOutDuration = 0.25f;

        [Tooltip("How the fallback animation wraps.")]
        public FallbackPlaybackMode FallbackPlaybackMode = FallbackPlaybackMode.Loop;

        [Tooltip(
            "Motion entries that define the blend tree. Each entry maps an animation clip to a 2D direction/position.")]
        public List<BlendTree2DMotionEntry> Motions = new();

#if UNITY_EDITOR

        public override Playable CreateTrackMixer(PlayableGraph graph, GameObject go, int inputCount)
        {
            if (!Application.isPlaying)
            {
                var mixer = AnimationMixerPlayable.Create(graph, inputCount);

                // Stamp each clip with a back-ref to this track so BlendTree2DClip.CreatePlayable can reach Motions
                // and preview the dominant (nearest-neighbor) motion instead of an empty bind pose.
                foreach (var c in GetClips())
                    if (c.asset is BlendTree2DClip btc)
                        btc.EditorPreviewTrack = this;

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
                    $"[BlendTree2DTrack] '{name}' uses Track Offset mode '{trackOffset}', which is not supported in DOTS — offsets are ignored. Use 'Apply Transform Offsets'.");
            }

            var director = context.Director;
            var rigDef = director.ResolveRigDefinition(this);

            if (rigDef == null)
            {
                Debug.LogWarning(
                    $"[BlendTree2DTrack] '{name}' has no RigDefinitionAuthoring binding — animation data will not be baked.");
                base.Bake(context);
                return;
            }

            var baker = context.Baker;
            var trackEntity = context.TrackEntity;
            var avatar = rigDef.GetAvatar();

            if (avatar == null)
            {
                Debug.LogWarning(
                    $"[BlendTree2DTrack] '{name}' rig '{rigDef.name}' has no Avatar — animation data will not be baked.");
                base.Bake(context);
                return;
            }

            Hash128 avatarMaskHash = default;
            if (applyAvatarMask && avatarMask != null)
            {
                var maskBaker = new AvatarMaskBaker();
                var maskBlob = maskBaker.CreateAvatarMaskBlob(baker, avatarMask, rigDef);
                avatarMaskHash = maskBlob.Value.hash;
                baker.AddBuffer<AvatarMaskBakingData>(trackEntity).Add(new AvatarMaskBakingData
                    { rigEntity = baker.GetEntity(rigDef, TransformUsageFlags.Dynamic), dataBlob = maskBlob });
            }

            baker.AddComponent(trackEntity, new BlendAnimationTree2DTrackData
            {
                BlendTreeType = BlendTreeType,
                LayerIndex = LayerIndex,
                TrackPositionOffset = trackOffset == TrackOffset.ApplyTransformOffsets ? positionOffset : Vector3.zero,
                TrackRotationOffset = trackOffset == TrackOffset.ApplyTransformOffsets
                    ? Quaternion.Euler(eulerAnglesOffset)
                    : Quaternion.identity,
                ApplyAvatarMask = applyAvatarMask,
                AvatarMaskHash = avatarMaskHash
            });

            var motionBuffer = baker.AddBuffer<BlendTree2DMotionData>(trackEntity);
            var clipsToBake = new List<AnimationClip>();
            var index = 0;

            foreach (var motion in Motions)
            {
                if (motion.clip == null) continue;
                motion.CalcDirection();
                motionBuffer.Add(new BlendTree2DMotionData
                {
                    AnimationHash = BakingUtils.ComputeAnimationHash(motion.clip, avatar),
                    BlendTree2DMotionElement = new ScriptedAnimator.BlendTree2DMotionElement
                        { pos = motion.directionCalc, motionIndex = index++ }
                });
                clipsToBake.Add(motion.clip);
            }

            if (ExitIdleClip != null)
            {
                baker.AddComponent(trackEntity, new TrackFallbackOverride
                {
                    FallbackClipHash = BakingUtils.ComputeAnimationHash(ExitIdleClip, avatar),
                    TrackOrder = FallbackTrackOrder.Compute(this),
                    BlendInSpeed = BlendLayerMath.DurationToSpeed(BlendInDuration),
                    BlendOutSpeed = BlendLayerMath.DurationToSpeed(BlendOutDuration),
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
                var e = baker.CreateAdditionalEntity(TransformUsageFlags.None, false, name + "_BlendTreeAssets");
                var dbBuffer = baker.AddBuffer<NewBlobAssetDatabaseRecord<AnimationClipBlob>>(e);
                dbBuffer.AddValidAnimations(bakedAnimations);

                if (bakedAnimations.IsCreated) bakedAnimations.Dispose();
            }

            base.Bake(context);
        }

        [Serializable]
        public class BlendTree2DMotionEntry
        {
            [Tooltip("Animation clip for this motion entry.")]
            public AnimationClip clip;

            [Tooltip("Direction angle in degrees. 0 = forward, 90 = right, -90 = left, 180 = backward.")]
            [Range(-180, 180)]
            public float degreeCalc;

            [Tooltip("Distance from origin in the blend space. Controls how far this motion extends.")]
            public float rangeCalc = 1;

            [Tooltip("Computed direction vector (auto-calculated from degree and range).")] [InspectorReadOnly]
            public Vector2 directionCalc;

            internal Vector2 CalcDirection()
            {
                var radians = degreeCalc * Mathf.Deg2Rad;
                directionCalc = new Vector2(Mathf.Sin(radians) * rangeCalc, Mathf.Cos(radians) * rangeCalc);
                return directionCalc;
            }
        }
    }
}