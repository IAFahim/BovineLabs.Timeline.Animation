using BovineLabs.Core.EntityCommands;
using Rukhanka;
using Unity.Entities;
using Unity.Mathematics;
using Hash128 = Unity.Entities.Hash128;

namespace BovineLabs.Timeline.Animation.Data.Builders
{
    public readonly struct TimelineAnimationStateBuilder
    {
        private const float MinDuration = 0.001f;

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

        public TimelineAnimationStateBuilder(
            Hash128 fallbackClipHash, float blendInSpeed, float blendOutSpeed,
            BlobAssetReference<AnimationClipBlob> fallbackBlob, Hash128 fallbackBlobHash,
            FallbackPlaybackMode playbackMode, float3 positionOffset, quaternion rotationOffset,
            bool removeStartOffset, bool applyFootIK)
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
        }

        public TimelineAnimationStateBuilder WithFallback(
            Hash128 clipHash,
            float blendInDuration,
            float blendOutDuration,
            FallbackPlaybackMode mode = FallbackPlaybackMode.Loop)
        {
            return new TimelineAnimationStateBuilder(
                clipHash, 1f / math.max(MinDuration, blendInDuration), 1f / math.max(MinDuration, blendOutDuration),
                _fallbackBlob, _fallbackBlobHash, mode, _positionOffset, _rotationOffset, _removeStartOffset, _applyFootIK);
        }

        public TimelineAnimationStateBuilder WithFallbackOffsets(float3 pos, quaternion rot, bool removeStart,
            bool footIK)
        {
            return new TimelineAnimationStateBuilder(
                _fallbackClipHash, _blendInSpeed, _blendOutSpeed,
                _fallbackBlob, _fallbackBlobHash, _playbackMode, pos, rot, removeStart, footIK);
        }

        public TimelineAnimationStateBuilder WithFallbackBlob(
            BlobAssetReference<AnimationClipBlob> blob,
            Hash128 hash)
        {
            return new TimelineAnimationStateBuilder(
                _fallbackClipHash, _blendInSpeed, _blendOutSpeed,
                blob, hash, _playbackMode, _positionOffset, _rotationOffset, _removeStartOffset, _applyFootIK);
        }

        public void ApplyTo<T>(ref T builder)
            where T : struct, IEntityCommands
        {
            builder.AddComponent(new BlendGroupTimer { FallbackAccumulatedTime = 0f });

            var activeFallback = new FallbackBlend
            {
                ClipHash = _fallbackClipHash,
                BlendInSpeed = _blendInSpeed,
                BlendOutSpeed = _blendOutSpeed,
                PlaybackMode = _playbackMode,
                LayerIndex = 0,
                BlendMode = AnimationBlendingMode.Override,
                AvatarMaskHash = default,
                PositionOffset = _positionOffset,
                RotationOffset = _rotationOffset,
                RemoveStartOffset = _removeStartOffset,
                ApplyFootIK = _applyFootIK
            };

            builder.AddComponent(activeFallback);

            builder.AddComponent(new DefaultBlendGroupFallback
            {
                ClipHash = _fallbackClipHash,
                BlendInSpeed = _blendInSpeed,
                BlendOutSpeed = _blendOutSpeed,
                PlaybackMode = _playbackMode,
                LayerIndex = 0,
                BlendMode = AnimationBlendingMode.Override,
                AvatarMaskHash = default,
                PositionOffset = _positionOffset,
                RotationOffset = _rotationOffset,
                RemoveStartOffset = _removeStartOffset,
                ApplyFootIK = _applyFootIK
            });

            if (_fallbackBlob.IsCreated)
            {
                var dbBuffer = builder.AddBuffer<NewBlobAssetDatabaseRecord<AnimationClipBlob>>();
                dbBuffer.Add(new NewBlobAssetDatabaseRecord<AnimationClipBlob>
                    { hash = _fallbackBlobHash, value = _fallbackBlob });
            }

            builder.AddBuffer<BlendGroupEntry>();
            builder.AddBuffer<SmoothBlendGroupEntry>();
            builder.AddBuffer<BlendTreePlaybackStateElement>();
        }
    }
}
