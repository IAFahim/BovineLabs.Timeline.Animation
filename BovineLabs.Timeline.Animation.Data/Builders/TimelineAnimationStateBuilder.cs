using BovineLabs.Core.EntityCommands;
using Rukhanka;
using Unity.Entities;
using Unity.Mathematics;
using Hash128 = Unity.Entities.Hash128;

namespace BovineLabs.Timeline.Animation.Data.Builders
{
    public readonly struct TimelineAnimationStateBuilder
    {
        private readonly Hash128 _fallbackClipHash;
        private readonly float _blendInSpeed;
        private readonly float _blendOutSpeed;
        private readonly BlobAssetReference<AnimationClipBlob> _fallbackBlob;
        private readonly Hash128 _fallbackBlobHash;
        private readonly FallbackPlaybackMode _playbackMode;

        private readonly float3 _positionOffset;
        private readonly quaternion _rotationOffset;
        private readonly bool _removeStartOffset;
        private readonly bool _applyFootIK;

        // Fallback blend mode + layer. Defaults (Override / 0) reproduce the previous hardcoded behavior exactly, so
        // existing bakes are unchanged. Additive layers the fallback on top of lower layers (breathing/lean overlay).
        private readonly AnimationBlendingMode _blendMode;
        private readonly int _layerIndex;

        public TimelineAnimationStateBuilder(
            Hash128 fallbackClipHash, float blendInSpeed, float blendOutSpeed,
            BlobAssetReference<AnimationClipBlob> fallbackBlob, Hash128 fallbackBlobHash,
            FallbackPlaybackMode playbackMode, float3 positionOffset, quaternion rotationOffset,
            bool removeStartOffset, bool applyFootIK, AnimationBlendingMode blendMode, int layerIndex)
        {
            _fallbackClipHash = fallbackClipHash;
            _blendInSpeed = blendInSpeed;
            _blendOutSpeed = blendOutSpeed;
            _fallbackBlob = fallbackBlob;
            _fallbackBlobHash = fallbackBlobHash;
            _playbackMode = playbackMode;
            _positionOffset = positionOffset;
            _rotationOffset = rotationOffset;
            _removeStartOffset = removeStartOffset;
            _applyFootIK = applyFootIK;
            _blendMode = blendMode;
            _layerIndex = layerIndex;
        }

        public TimelineAnimationStateBuilder WithFallback(
            Hash128 clipHash,
            float blendInDuration,
            float blendOutDuration,
            FallbackPlaybackMode mode = FallbackPlaybackMode.Loop)
        {
            return new TimelineAnimationStateBuilder(
                clipHash, DurationToSpeed(blendInDuration), DurationToSpeed(blendOutDuration),
                _fallbackBlob, _fallbackBlobHash, mode, _positionOffset, _rotationOffset, _removeStartOffset,
                _applyFootIK, _blendMode, _layerIndex);
        }

        // Opens up the fallback blend so an Additive fallback (e.g. breathing/lean overlay) can ride on top of a base
        // Override fallback. Default Override/0 keeps the historical behavior. Additive on layer 0 adds over the bind
        // pose (foot-gun) — put an Additive overlay on layer >= 1.
        public TimelineAnimationStateBuilder WithFallbackBlend(AnimationBlendingMode blendMode, int layerIndex)
        {
            return new TimelineAnimationStateBuilder(
                _fallbackClipHash, _blendInSpeed, _blendOutSpeed,
                _fallbackBlob, _fallbackBlobHash, _playbackMode, _positionOffset, _rotationOffset, _removeStartOffset,
                _applyFootIK, blendMode, layerIndex);
        }

        // A duration of 0 (or below the floor) means "no global smoothing" - encoded as speed 0, the
        // instant-snap sentinel IntegrateWeights expects. Only a strictly positive duration smooths.
        // Delegates to the shared BlendLayerMath.DurationToSpeed sentinel so every path snaps identically.
        private static float DurationToSpeed(float duration)
        {
            return BlendLayerMath.DurationToSpeed(duration);
        }

        public TimelineAnimationStateBuilder WithFallbackOffsets(float3 pos, quaternion rot, bool removeStart,
            bool footIK)
        {
            return new TimelineAnimationStateBuilder(
                _fallbackClipHash, _blendInSpeed, _blendOutSpeed,
                _fallbackBlob, _fallbackBlobHash, _playbackMode, pos, rot, removeStart, footIK, _blendMode,
                _layerIndex);
        }

        public TimelineAnimationStateBuilder WithFallbackBlob(
            BlobAssetReference<AnimationClipBlob> blob,
            Hash128 hash)
        {
            return new TimelineAnimationStateBuilder(
                _fallbackClipHash, _blendInSpeed, _blendOutSpeed,
                blob, hash, _playbackMode, _positionOffset, _rotationOffset, _removeStartOffset, _applyFootIK,
                _blendMode, _layerIndex);
        }

        public FallbackBlend BuildFallbackBlend()
        {
            return new FallbackBlend
            {
                ClipHash = _fallbackClipHash,
                BlendInSpeed = _blendInSpeed,
                BlendOutSpeed = _blendOutSpeed,
                PlaybackMode = _playbackMode,
                LayerIndex = _layerIndex,
                BlendMode = _blendMode,
                AvatarMaskHash = default,
                PositionOffset = _positionOffset,
                RotationOffset = _rotationOffset,
                RemoveStartOffset = _removeStartOffset,
                ApplyFootIK = _applyFootIK
            };
        }

        public void ApplyTo<T>(ref T builder)
            where T : struct, IEntityCommands
        {
            builder.AddComponent(new BlendGroupTimer { FallbackAccumulatedTime = 0f });

            builder.AddComponent(BuildFallbackBlend());

            if (_fallbackBlob.IsCreated)
            {
                var dbBuffer = builder.AddBuffer<NewBlobAssetDatabaseRecord<AnimationClipBlob>>();
                dbBuffer.Add(new NewBlobAssetDatabaseRecord<AnimationClipBlob>
                    { hash = _fallbackBlobHash, value = _fallbackBlob });
            }

            builder.AddBuffer<BlendGroupEntry>();
            builder.AddBuffer<SmoothBlendGroupEntry>();
            builder.AddBuffer<BlendTreePlaybackStateElement>();
            builder.AddBuffer<BlendTree1DPlaybackStateElement>();
            builder.AddBuffer<BlendTreeDirectPlaybackStateElement>();
        }
    }
}