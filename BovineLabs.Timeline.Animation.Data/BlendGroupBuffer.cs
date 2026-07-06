using Rukhanka;
using Unity.Entities;
using Unity.Mathematics;
using Hash128 = Unity.Entities.Hash128;

namespace BovineLabs.Timeline.Animation
{
    [InternalBufferCapacity(0)]
    public struct BlendGroupEntry : IBufferElementData
    {
        public int LayerIndex;
        public Hash128 ClipHash;
        public float NormalizedTime;
        public float Weight;
        public Hash128 AvatarMaskHash;
        public AnimationBlendingMode BlendMode;
        public uint MotionId;

        public float3 PositionOffset;
        public quaternion RotationOffset;
        public bool RemoveStartOffset;
        public bool ApplyFootIK;

        // Per-timeline playback speed of the clip that produced this request (the source clip's TimeTransform.Scale).
        // Consumed by the unification system to scale the fallback clock and crossfade weight ramps so they slow with
        // per-timeline time-scale. Blend-tree gatherers do not thread this yet and leave it 0 (treated as 1).
        public float TimeScale;

        // Continuous-phase loop mode: when true this entry advances its own NormalizedTime by PhaseVelocity
        // (cycles/sec) each frame instead of reading the wrapping timeline localTime, so a looping clip never
        // snaps mid-cycle when the PlayableDirector wraps at the timeline duration. Default false = current behavior.
        public bool ContinuousLoop;
        public float PhaseVelocity;
    }

    [InternalBufferCapacity(0)]
    public struct SmoothBlendGroupEntry : IBufferElementData
    {
        public int LayerIndex;
        public Hash128 ClipHash;
        public float NormalizedTime;
        public float CurrentWeight;
        public float TargetWeight;
        public AnimationBlendingMode BlendMode;
        public Hash128 AvatarMaskHash;
        public uint MotionId;

        public float3 PositionOffset;
        public quaternion RotationOffset;
        public bool RemoveStartOffset;
        public bool ApplyFootIK;

        // Continuous-phase loop mode. PhaseSeeded tracks whether NormalizedTime has been initialized from the
        // first request; once seeded, the free-run advance owns NormalizedTime (never re-synced to the wrapping
        // localTime) unless scrubbing.
        public bool ContinuousLoop;
        public float PhaseVelocity;
        public bool PhaseSeeded;
    }

    public struct BlendGroupTimer : IComponentData
    {
        public float FallbackAccumulatedTime;
        public Hash128 PreviousFallbackClipHash;

        // Effective per-timeline playback speed of the actor's dominant (best-weight) active clip this frame, or 1
        // while no clips are active. Drives the fallback clock and crossfade ramps so idle animation slows with
        // per-timeline time-scale.
        public float TimeScale;
    }

    public struct FallbackBlend : IComponentData
    {
        public Hash128 ClipHash;
        public float BlendInSpeed;
        public float BlendOutSpeed;
        public FallbackPlaybackMode PlaybackMode;
        public int LayerIndex;
        public AnimationBlendingMode BlendMode;
        public Hash128 AvatarMaskHash;

        public float3 PositionOffset;
        public quaternion RotationOffset;
        public bool RemoveStartOffset;
        public bool ApplyFootIK;
    }

    public struct DefaultBlendGroupFallback : IComponentData
    {
        public BlobAssetReference<FallbackBlend> Value;
    }

    public struct TrackFallbackOverride : IComponentData
    {
        public Hash128 FallbackClipHash;

        // Sibling index of the source track, baked from the timeline. Used to break same-layer ties deterministically.
        public int TrackOrder;
        public float BlendInSpeed;
        public float BlendOutSpeed;
        public FallbackPlaybackMode PlaybackMode;
        public int LayerIndex;
        public AnimationBlendingMode BlendMode;
        public Hash128 AvatarMaskHash;

        public float3 PositionOffset;
        public quaternion RotationOffset;
        public bool RemoveStartOffset;
        public bool ApplyFootIK;
    }

    public enum FallbackPlaybackMode : byte
    {
        Loop = 0,
        Clamp = 1,
        Hold = 2
    }

    public struct AnimationDebugState : IComponentData
    {
        public int ActiveTrackCount;
        public int ActiveClipCount;
        public int FallbackTrackCount;
        public float FallbackWeight;
        public float BlendInSpeed;
        public float BlendOutSpeed;
        public FallbackPlaybackMode PlaybackMode;
    }
}