using Unity.Entities;

namespace BovineLabs.Timeline.Animation
{
    public struct AfterImageTrackData : IComponentData
    {
        public Entity Prefab;
    }

    public struct AfterImageClipData : IComponentData
    {
        public Entity SpawnedEntity;
    }
}