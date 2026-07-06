using BovineLabs.Testing;
using BovineLabs.Timeline.Data;
using NUnit.Framework;
using Rukhanka;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace BovineLabs.Timeline.Animation.Tests
{
    [TestFixture]
    public class CharacterLookAtMixerBlendTests
    {
        private const float Eps = 0.0001f;

        [Test]
        public void Blend_SingleFullWeightSlot_ReturnsAuthoredPointAndWeight()
        {
            var p = new float3(2f, 3f, 4f);
            var m = new MixData<CharacterLookAtData>
            {
                Weights = new float4(1f, 0f, 0f, 0f),
                Value1 = Point(p, 1f)
            };

            var blended = JobHelpers.Blend<CharacterLookAtData, CharacterLookAtMixer>(ref m, default);

            AssertApprox(p, blended.LookPoint);
            Assert.AreEqual(1f, blended.Weight, Eps);
        }

        [Test]
        public void Blend_HalfClipWeight_KeepsPoint_NotPulledToOrigin()
        {
            var p = new float3(5f, 6f, 7f);
            var m = new MixData<CharacterLookAtData>
            {
                Weights = new float4(0.5f, 0f, 0f, 0f),
                Value1 = Point(p, 1f)
            };

            var blended = JobHelpers.Blend<CharacterLookAtData, CharacterLookAtMixer>(ref m, default);

            AssertApprox(p, blended.LookPoint);
            Assert.AreEqual(0.5f, blended.Weight, Eps);
        }

        [Test]
        public void Blend_TwoEqualSlots_ReturnsMidpoint()
        {
            var a = new float3(0f, 0f, 0f);
            var b = new float3(10f, 0f, 0f);
            var m = new MixData<CharacterLookAtData>
            {
                Weights = new float4(0.5f, 0.5f, 0f, 0f),
                Value1 = Point(a, 1f),
                Value2 = Point(b, 1f)
            };

            var blended = JobHelpers.Blend<CharacterLookAtData, CharacterLookAtMixer>(ref m, default);

            AssertApprox(new float3(5f, 0f, 0f), blended.LookPoint);
        }

        [Test]
        public void Blend_UnequalMass_CentroidBiasedTowardHeavierPoint()
        {
            var a = new float3(0f, 0f, 0f);
            var b = new float3(10f, 0f, 0f);
            var m = new MixData<CharacterLookAtData>
            {
                Weights = new float4(0.5f, 0.5f, 0f, 0f),
                Value1 = Point(a, 0.25f),
                Value2 = Point(b, 0.75f)
            };

            var blended = JobHelpers.Blend<CharacterLookAtData, CharacterLookAtMixer>(ref m, default);

            AssertApprox(new float3(7.5f, 0f, 0f), blended.LookPoint);
            Assert.Greater(blended.LookPoint.x, 5f, "centroid must be biased toward the heavier point B");
        }

        [Test]
        public void Blend_ZeroWeightGarbageSlot_ContributesNothing()
        {
            var garbage = new float3(1000f, 1000f, 1000f);
            var b = new float3(3f, 4f, 5f);
            var m = new MixData<CharacterLookAtData>
            {
                Weights = new float4(0.5f, 0.5f, 0f, 0f),
                Value1 = Point(garbage, 0f),
                Value2 = Point(b, 1f)
            };

            var blended = JobHelpers.Blend<CharacterLookAtData, CharacterLookAtMixer>(ref m, default);

            AssertApprox(b, blended.LookPoint);
        }

        [Test]
        public void Blend_Additive_PassesActiveCentroidThroughMasslessDefault()
        {
            var a = new float3(0f, 0f, 0f);
            var b = new float3(10f, 0f, 0f);
            var m = new MixData<CharacterLookAtData>
            {
                Weights = new float4(1f, 1f, 0f, 0f),
                Value1 = Point(a, 1f),
                Value2 = Point(b, 1f),
                Additive = true
            };

            var blended = JobHelpers.Blend<CharacterLookAtData, CharacterLookAtMixer>(ref m, default);

            AssertApprox(new float3(5f, 0f, 0f), blended.LookPoint);
            Assert.AreEqual(1f, blended.Weight, Eps);
        }

        private static CharacterLookAtData Point(float3 lookPoint, float weight)
        {
            return new CharacterLookAtData
            {
                LookPoint = lookPoint,
                Weight = weight,
                AngleLimits = new float2(-1f, 1f),
                SourceMode = PointSourceMode.StaticWorld,
                StaticOrOffsetPoint = lookPoint
            };
        }

        private static void AssertApprox(float3 expected, float3 actual)
        {
            Assert.AreEqual(expected.x, actual.x, Eps);
            Assert.AreEqual(expected.y, actual.y, Eps);
            Assert.AreEqual(expected.z, actual.z, Eps);
        }
    }

    [TestFixture]
    public class CharacterLookAtMixerLerpTests
    {
        private const float Eps = 0.0001f;

        [Test]
        public void Lerp_EqualMass_ReturnsMidpoint()
        {
            var a = new CharacterLookAtData { LookPoint = new float3(0f, 0f, 0f), Weight = 1f };
            var b = new CharacterLookAtData { LookPoint = new float3(4f, 0f, 0f), Weight = 1f };

            var result = new CharacterLookAtMixer().Lerp(a, b, 0.5f);

            Assert.AreEqual(2f, result.LookPoint.x, Eps);
            Assert.AreEqual(1f, result.Weight, Eps);
        }

        [Test]
        public void Lerp_ZeroMassFirstOperand_ReturnsSecondPoint()
        {
            var a = new CharacterLookAtData { LookPoint = new float3(1000f, 1000f, 1000f), Weight = 0f };
            var b = new CharacterLookAtData { LookPoint = new float3(4f, 5f, 6f), Weight = 1f };

            var result = new CharacterLookAtMixer().Lerp(a, b, 0.5f);

            Assert.AreEqual(b.LookPoint.x, result.LookPoint.x, Eps);
            Assert.AreEqual(b.LookPoint.y, result.LookPoint.y, Eps);
            Assert.AreEqual(b.LookPoint.z, result.LookPoint.z, Eps);
        }

        [Test]
        public void Lerp_ZeroMassFirstOperand_ReturnsSecondPoint_AtSmallS()
        {
            var a = new CharacterLookAtData { LookPoint = new float3(1000f, 0f, 0f), Weight = 0f };
            var b = new CharacterLookAtData { LookPoint = new float3(4f, 0f, 0f), Weight = 1f };

            var result = new CharacterLookAtMixer().Lerp(a, b, 0.1f);

            Assert.AreEqual(b.LookPoint.x, result.LookPoint.x, Eps);
        }

        [Test]
        public void Lerp_AngleLimits_BlendComponentwise()
        {
            var a = new CharacterLookAtData { Weight = 1f, AngleLimits = new float2(-10f, 10f) };
            var b = new CharacterLookAtData { Weight = 1f, AngleLimits = new float2(-30f, 50f) };

            var result = new CharacterLookAtMixer().Lerp(a, b, 0.5f);

            Assert.AreEqual(-20f, result.AngleLimits.x, Eps);
            Assert.AreEqual(30f, result.AngleLimits.y, Eps);
        }

        [Test]
        public void Add_SumsWeightAndCentroidPoint()
        {
            var a = new CharacterLookAtData { LookPoint = new float3(0f, 0f, 0f), Weight = 0.25f };
            var b = new CharacterLookAtData { LookPoint = new float3(8f, 0f, 0f), Weight = 0.75f };

            var result = new CharacterLookAtMixer().Add(a, b);

            Assert.AreEqual(1f, result.Weight, Eps);
            Assert.AreEqual(6f, result.LookPoint.x, Eps);
        }
    }

    public class CharacterLookAtTrackSystemTests : ECSTestsFixture
    {
        [Test]
        public void StaticWorldClip_WritesLookPointToTarget_AndSetsAimWeight()
        {
            var system = World.CreateSystem<CharacterLookAtTrackSystem>();

            var lookPoint = new float3(7f, 8f, 9f);

            var target = Manager.CreateEntity(typeof(LocalTransform));
            Manager.SetComponentData(target, LocalTransform.Identity);

            var aim = Manager.CreateEntity(typeof(AimIKComponent), typeof(LocalToWorld));
            Manager.SetComponentData(aim, new AimIKComponent { weight = 0f });

            var animator = Manager.CreateEntity(typeof(CharacterLookAtTarget), typeof(LocalToWorld));
            Manager.SetComponentData(animator, new CharacterLookAtTarget { TargetEntity = target, AimIKEntity = aim });
            Manager.SetComponentData(animator, new LocalToWorld { Value = float4x4.identity });

            CreateActiveClip(animator, new CharacterLookAtData
            {
                LookPoint = float3.zero,
                Weight = 1f,
                AngleLimits = new float2(-45f, 45f),
                SourceMode = PointSourceMode.StaticWorld,
                StaticOrOffsetPoint = lookPoint
            });

            system.Update(WorldUnmanaged);
            Manager.CompleteAllTrackedJobs();

            var writtenTransform = Manager.GetComponentData<LocalTransform>(target);
            Assert.AreEqual(lookPoint.x, writtenTransform.Position.x, 0.0001f);
            Assert.AreEqual(lookPoint.y, writtenTransform.Position.y, 0.0001f);
            Assert.AreEqual(lookPoint.z, writtenTransform.Position.z, 0.0001f);

            var writtenAim = Manager.GetComponentData<AimIKComponent>(aim);
            Assert.AreEqual(1f, writtenAim.weight, 0.0001f);
            Assert.AreEqual(-45f, writtenAim.angleLimits.x, 0.0001f);
            Assert.AreEqual(45f, writtenAim.angleLimits.y, 0.0001f);

            World.DestroySystem(system);
        }

        [Test]
        public void OwnerOffsetClip_ResolvesPointInOwnerWorldSpace()
        {
            var system = World.CreateSystem<CharacterLookAtTrackSystem>();

            var ownerOffset = new float3(1f, 0f, 0f);
            var ownerTranslation = new float3(10f, 20f, 30f);

            var target = Manager.CreateEntity(typeof(LocalTransform));
            Manager.SetComponentData(target, LocalTransform.Identity);

            var aim = Manager.CreateEntity(typeof(AimIKComponent), typeof(LocalToWorld));
            Manager.SetComponentData(aim, new AimIKComponent { weight = 0f });

            var animator = Manager.CreateEntity(typeof(CharacterLookAtTarget), typeof(LocalToWorld));
            Manager.SetComponentData(animator, new CharacterLookAtTarget { TargetEntity = target, AimIKEntity = aim });
            Manager.SetComponentData(animator, new LocalToWorld { Value = float4x4.Translate(ownerTranslation) });

            CreateActiveClip(animator, new CharacterLookAtData
            {
                Weight = 1f,
                AngleLimits = new float2(-30f, 30f),
                SourceMode = PointSourceMode.OwnerOffset,
                StaticOrOffsetPoint = ownerOffset
            });

            system.Update(WorldUnmanaged);
            Manager.CompleteAllTrackedJobs();

            var writtenTransform = Manager.GetComponentData<LocalTransform>(target);
            var expected = ownerTranslation + ownerOffset;
            Assert.AreEqual(expected.x, writtenTransform.Position.x, 0.0001f);
            Assert.AreEqual(expected.y, writtenTransform.Position.y, 0.0001f);
            Assert.AreEqual(expected.z, writtenTransform.Position.z, 0.0001f);

            World.DestroySystem(system);
        }

        [Test]
        public void HalfWeightClip_SaturatedAimWeightStaysBelowOne()
        {
            var system = World.CreateSystem<CharacterLookAtTrackSystem>();

            var target = Manager.CreateEntity(typeof(LocalTransform));
            Manager.SetComponentData(target, LocalTransform.Identity);

            var aim = Manager.CreateEntity(typeof(AimIKComponent), typeof(LocalToWorld));
            Manager.SetComponentData(aim, new AimIKComponent { weight = 0f });

            var animator = Manager.CreateEntity(typeof(CharacterLookAtTarget), typeof(LocalToWorld));
            Manager.SetComponentData(animator, new CharacterLookAtTarget { TargetEntity = target, AimIKEntity = aim });
            Manager.SetComponentData(animator, new LocalToWorld { Value = float4x4.identity });

            var lookPoint = new float3(2f, 0f, 0f);
            var clip = CreateActiveClip(animator, new CharacterLookAtData
            {
                Weight = 0.5f,
                SourceMode = PointSourceMode.StaticWorld,
                StaticOrOffsetPoint = lookPoint
            });
            Manager.AddComponentData(clip, new ClipWeight { Value = 1f });

            system.Update(WorldUnmanaged);
            Manager.CompleteAllTrackedJobs();

            var writtenAim = Manager.GetComponentData<AimIKComponent>(aim);
            Assert.AreEqual(0.5f, writtenAim.weight, 0.0001f);

            var writtenTransform = Manager.GetComponentData<LocalTransform>(target);
            Assert.AreEqual(lookPoint.x, writtenTransform.Position.x, 0.0001f);

            World.DestroySystem(system);
        }

        [Test]
        public void TwoOverlappingClips_BlendAngleLimits_NotSlotOneOnly()
        {
            var system = World.CreateSystem<CharacterLookAtTrackSystem>();

            var target = Manager.CreateEntity(typeof(LocalTransform));
            Manager.SetComponentData(target, LocalTransform.Identity);

            var aim = Manager.CreateEntity(typeof(AimIKComponent), typeof(LocalToWorld));
            Manager.SetComponentData(aim, new AimIKComponent { weight = 0f });

            var animator = Manager.CreateEntity(typeof(CharacterLookAtTarget), typeof(LocalToWorld));
            Manager.SetComponentData(animator, new CharacterLookAtTarget { TargetEntity = target, AimIKEntity = aim });
            Manager.SetComponentData(animator, new LocalToWorld { Value = float4x4.identity });

            var lookPoint = new float3(3f, 0f, 0f);

            var clipA = CreateActiveClip(animator, new CharacterLookAtData
            {
                Weight = 1f,
                AngleLimits = new float2(-80f, 80f),
                SourceMode = PointSourceMode.StaticWorld,
                StaticOrOffsetPoint = lookPoint
            });
            Manager.AddComponentData(clipA, new ClipWeight { Value = 0.5f });

            var clipB = CreateActiveClip(animator, new CharacterLookAtData
            {
                Weight = 1f,
                AngleLimits = new float2(-10f, 10f),
                SourceMode = PointSourceMode.StaticWorld,
                StaticOrOffsetPoint = lookPoint
            });
            Manager.AddComponentData(clipB, new ClipWeight { Value = 0.5f });

            system.Update(WorldUnmanaged);
            Manager.CompleteAllTrackedJobs();

            // 50/50 blend of (-80,80) and (-10,10) => (-45,45). If the write used slot 1's raw limits instead of the
            // blended value, this would read one clip's limits (-80/-10 or 80/10), not the midpoint.
            var writtenAim = Manager.GetComponentData<AimIKComponent>(aim);
            Assert.AreEqual(-45f, writtenAim.angleLimits.x, 0.0001f);
            Assert.AreEqual(45f, writtenAim.angleLimits.y, 0.0001f);

            World.DestroySystem(system);
        }

        private Entity CreateActiveClip(Entity animator, CharacterLookAtData authored)
        {
            var clip = Manager.CreateEntity(
                typeof(CharacterLookAtAnimated),
                typeof(TrackBinding),
                typeof(ClipActive),
                typeof(TimelineActive));

            Manager.SetComponentData(clip, new CharacterLookAtAnimated { AuthoredData = authored });
            Manager.SetComponentData(clip, new TrackBinding { Value = animator });
            Manager.SetComponentEnabled<ClipActive>(clip, true);
            Manager.SetComponentEnabled<TimelineActive>(clip, true);
            return clip;
        }
    }
}