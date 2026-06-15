using BovineLabs.Core.EntityCommands;
using Unity.Mathematics;
using UnityEngine.Timeline;
using Hash128 = Unity.Entities.Hash128;

namespace BovineLabs.Timeline.Animation.Data.Builders
{
    public struct RukhankaAnimationBuilder
    {
        public Hash128 ClipHash;
        public TimelineClip.ClipExtrapolation PreExtrapolation;
        public TimelineClip.ClipExtrapolation PostExtrapolation;
        public float3 PositionOffset;
        public quaternion RotationOffset;
        public bool RemoveStartOffset;
        public bool ApplyFootIK;

        public void ApplyTo<T>(ref T builder)
            where T : struct, IEntityCommands
        {
            builder.AddComponent(new RukhankaSingleClipData
            {
                ClipHash = ClipHash,
                PreExtrapolation = PreExtrapolation,
                PostExtrapolation = PostExtrapolation,
                PositionOffset = PositionOffset,
                RotationOffset = RotationOffset,
                RemoveStartOffset = RemoveStartOffset,
                ApplyFootIK = ApplyFootIK
            });
        }
    }
}