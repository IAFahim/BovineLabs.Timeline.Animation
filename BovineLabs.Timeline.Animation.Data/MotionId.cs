using Unity.Collections;
using Unity.Entities;
using Hash128 = Unity.Entities.Hash128;

namespace BovineLabs.Timeline.Animation
{
    public static class MotionId
    {
        public const uint Fallback = 0xFFFFFFFF;

        public static uint Compute(Entity track, int layerIndex, Hash128 clipHash, Entity instance)
        {
            var h = new xxHash3.StreamingState(true, 0x1337);
            h.Update(track.Index);
            h.Update(track.Version);
            h.Update(layerIndex);
            // Per-instance discriminator: two instances of the same clip on the same track+layer must
            // produce distinct ids (otherwise they collapse into one and crossfade against themselves).
            h.Update(instance.Index);
            h.Update(instance.Version);
            h.Update(clipHash.Value.x);
            h.Update(clipHash.Value.y);
            h.Update(clipHash.Value.z);
            h.Update(clipHash.Value.w);
            var id = h.DigestHash64().x;
            return id == Fallback ? Fallback - 1u : id;
        }

        // Blend-tree motions have no clip entity of their own; each motion slot is disambiguated by its
        // index in the track's motion buffer. Encoded as Entity { Index = motionIndex, Version = 0 } so the
        // hash matches the historic new Entity { Index = motionIndex } idiom the blend-tree systems used.
        public static uint ComputeForMotion(Entity track, int layerIndex, Hash128 clipHash, int motionIndex)
        {
            return Compute(track, layerIndex, clipHash, new Entity { Index = motionIndex });
        }
    }
}