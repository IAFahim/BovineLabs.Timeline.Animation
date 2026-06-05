using Unity.Collections;
using Unity.Entities;
using Hash128 = Unity.Entities.Hash128;

namespace BovineLabs.Timeline.Animation
{
    public static class MotionId
    {
        public const uint Fallback = 0xFFFFFFFF;

        public static uint Compute(Entity track, int layerIndex, Hash128 clipHash)
        {
            var h = new xxHash3.StreamingState(true, 0x1337);
            h.Update(track.Index);
            h.Update(track.Version);
            h.Update(layerIndex);
            h.Update(clipHash.Value.x);
            h.Update(clipHash.Value.y);
            h.Update(clipHash.Value.z);
            h.Update(clipHash.Value.w);
            return h.DigestHash64().x;
        }
    }
}