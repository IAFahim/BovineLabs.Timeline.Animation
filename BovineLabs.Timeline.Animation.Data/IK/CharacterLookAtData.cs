using BovineLabs.Reaction.Data.Core;
using BovineLabs.Timeline.Data;
using BovineLabs.Timeline.EntityLinks.Data;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Properties;

namespace BovineLabs.Timeline.Animation
{
    public enum PointSourceMode : byte
    {
        LinkedTarget,
        StaticWorld,
        OwnerOffset
    }

    public struct CharacterLookAtData
    {
        public float3 LookPoint;
        public float Weight;
        public float2 AngleLimits;
        public PointSourceMode SourceMode;
        public EntityLinkRef Target;
        public float3 StaticOrOffsetPoint;
    }

    public struct CharacterLookAtAnimated : IAnimatedComponent<CharacterLookAtData>
    {
        public CharacterLookAtData AuthoredData;
        [CreateProperty] public CharacterLookAtData Value { get; set; }
    }

    public struct CharacterLookAtTarget : IComponentData
    {
        public Entity TargetEntity;
        public Entity AimIKEntity;
    }

    public readonly struct CharacterLookAtMixer : IMixer<CharacterLookAtData>
    {
        public CharacterLookAtData Lerp(in CharacterLookAtData a, in CharacterLookAtData b, in float s)
        {
            var wa = (1f - s) * a.Weight;
            var wb = s * b.Weight;
            var wsum = wa + wb;
            var point = wsum > math.EPSILON
                ? (a.LookPoint * wa + b.LookPoint * wb) / wsum
                : math.lerp(a.LookPoint, b.LookPoint, s);

            return new CharacterLookAtData
            {
                LookPoint = point,
                Weight = math.lerp(a.Weight, b.Weight, s),
                AngleLimits = math.lerp(a.AngleLimits, b.AngleLimits, s),
                SourceMode = PointSourceMode.StaticWorld,
                StaticOrOffsetPoint = point,
                Target = default
            };
        }

        public CharacterLookAtData Add(in CharacterLookAtData a, in CharacterLookAtData b)
        {
            var w = a.Weight + b.Weight;
            var point = w > math.EPSILON
                ? (a.LookPoint * a.Weight + b.LookPoint * b.Weight) / w
                : a.LookPoint;

            return new CharacterLookAtData
            {
                LookPoint = point,
                Weight = w,
                AngleLimits = a.AngleLimits,
                SourceMode = PointSourceMode.StaticWorld,
                StaticOrOffsetPoint = point,
                Target = default
            };
        }
    }
}