using BovineLabs.Core.EntityCommands;

namespace BovineLabs.Timeline.Animation.Data.Builders
{
    public struct CharacterLookAtBuilder
    {
        public CharacterLookAtData AuthoredData;

        public void ApplyTo<T>(ref T builder)
            where T : struct, IEntityCommands
        {
            builder.AddComponent(new CharacterLookAtAnimated { AuthoredData = AuthoredData });
        }
    }
}