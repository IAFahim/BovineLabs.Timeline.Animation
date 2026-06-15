using Unity.Entities;
using Unity.Mathematics;
using UnityEngine.Timeline;
using Hash128 = Unity.Entities.Hash128;

namespace BovineLabs.Timeline.Animation
{
    public struct RukhankaSingleClipData : IComponentData
    {
        public Hash128 ClipHash;
        public TimelineClip.ClipExtrapolation PreExtrapolation;
        public TimelineClip.ClipExtrapolation PostExtrapolation;

        public float3 PositionOffset;
        public quaternion RotationOffset;
        public bool RemoveStartOffset;
        public bool ApplyFootIK;
    }
}