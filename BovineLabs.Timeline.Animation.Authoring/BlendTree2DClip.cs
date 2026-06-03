using BovineLabs.Timeline.Authoring;
using BovineLabs.Timeline.EntityLinks.Authoring;
using BovineLabs.Timeline.PlayerInputs.Authoring;
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

        [Header("Clip Transform Offsets")] public Vector3 positionOffset = Vector3.zero;

        public Vector3 eulerAnglesOffset = Vector3.zero;

        [Space] [Tooltip("Removes the starting offset of the animation so it begins exactly at the track's offset.")]
        public bool removeStartOffset = true;

        public bool applyFootIK = true;

        public ClipCaps clipCaps => ClipCaps.All;

#if UNITY_EDITOR
        /// <summary>
        ///     In edit mode, return an empty AnimationMixerPlayable as a dummy node.
        ///     BlendTree2D clips are driven by ECS at runtime — there is no single clip
        ///     to preview for the editor PlayableGraph. The track mixer still provides
        ///     track-level offsets, but clip contents are DOTS-only.
        /// </summary>
        public override Playable CreatePlayable(PlayableGraph graph, GameObject owner)
        {
            if (!Application.isPlaying)
                // Return an empty mixer — no animation data to preview per-clip for blend trees
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
                    return;
                }

                if (!EntityLinkAuthoringUtility.TryGetKey(ReadFrom, out var key))
                {
                    Debug.LogError(
                        $"{nameof(BlendTree2DClip)} '{name}' could not resolve key for '{ReadFrom.name}'.");
                    return;
                }

                readLinkKey = key;
            }

            context.Baker.AddComponent(clipEntity, new BlendTree2DDirectionClipData
            {
                Value = BlendParameter,
                ReadKind = ReadKind,
                ReadLinkKey = readLinkKey,
                ClipIn = (float)context.Clip.clipIn,
                TimeScale = (float)context.Clip.timeScale,
                PositionOffset = positionOffset,
                RotationOffset = Quaternion.Euler(eulerAnglesOffset),
                RemoveStartOffset = removeStartOffset,
                ApplyFootIK = applyFootIK
            });

            base.Bake(clipEntity, context);
        }
    }
}
