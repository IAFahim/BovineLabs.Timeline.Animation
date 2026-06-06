using BovineLabs.Core.EntityCommands;
using Unity.Entities;

namespace BovineLabs.Timeline.Animation.Data.Builders
{
    public struct AfterImageBuilder
    {
        public void ApplyTo<T>(ref T builder)
            where T : struct, IEntityCommands
        {
            builder.AddComponent(new AfterImageClipData { SpawnedEntity = Entity.Null });
        }
    }
}