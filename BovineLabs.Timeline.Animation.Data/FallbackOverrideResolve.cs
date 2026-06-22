using Rukhanka;
using Unity.Mathematics;
using Hash128 = Unity.Entities.Hash128;

namespace BovineLabs.Timeline.Animation
{
    public static class FallbackOverrideResolve
    {
        public static bool DominantOverride(in TrackFallbackOverride candidate, in TrackFallbackOverride current)
        {
            if (candidate.LayerIndex != current.LayerIndex)
                return candidate.LayerIndex > current.LayerIndex;

            return candidate.FallbackClipHash.CompareTo(current.FallbackClipHash) > 0;
        }

        public static bool Matches(in FallbackBlend f, in TrackFallbackOverride o)
        {
            return MatchesBlend(
                in f, o.FallbackClipHash, o.BlendInSpeed, o.BlendOutSpeed, o.PlaybackMode, o.LayerIndex,
                o.BlendMode, o.AvatarMaskHash, o.PositionOffset, o.RotationOffset, o.RemoveStartOffset,
                o.ApplyFootIK);
        }

        public static bool Matches(in FallbackBlend f, in FallbackBlend d)
        {
            return MatchesBlend(
                in f, d.ClipHash, d.BlendInSpeed, d.BlendOutSpeed, d.PlaybackMode, d.LayerIndex,
                d.BlendMode, d.AvatarMaskHash, d.PositionOffset, d.RotationOffset, d.RemoveStartOffset,
                d.ApplyFootIK);
        }

        private static bool MatchesBlend(
            in FallbackBlend f, Hash128 clipHash, float blendInSpeed, float blendOutSpeed,
            FallbackPlaybackMode playbackMode, int layerIndex, AnimationBlendingMode blendMode,
            Hash128 avatarMaskHash, float3 positionOffset, quaternion rotationOffset,
            bool removeStartOffset, bool applyFootIK)
        {
            return f.ClipHash == clipHash
                   && f.BlendInSpeed == blendInSpeed
                   && f.BlendOutSpeed == blendOutSpeed
                   && f.PlaybackMode == playbackMode
                   && f.LayerIndex == layerIndex
                   && f.BlendMode == blendMode
                   && f.AvatarMaskHash == avatarMaskHash
                   && f.PositionOffset.Equals(positionOffset)
                   && f.RotationOffset.Equals(rotationOffset)
                   && f.RemoveStartOffset == removeStartOffset
                   && f.ApplyFootIK == applyFootIK;
        }
    }
}
