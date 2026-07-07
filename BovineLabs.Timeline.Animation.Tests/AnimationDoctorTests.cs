// <copyright file="AnimationDoctorTests.cs" company="BovineLabs">
//     Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Timeline.Animation.Tests
{
    using System.Collections.Generic;
    using BovineLabs.Timeline.Animation.Editor;
    using NUnit.Framework;
    using Unity.Mathematics;
    using Hash128 = Unity.Entities.Hash128;

    /// <summary>
    /// Pure checklist coverage for <see cref="AnimationDoctor"/>. Each test builds an actor snapshot by hand and asserts
    /// which silent-failure codes the diagnosis raises — no editor window or live ECS world required.
    /// </summary>
    [TestFixture]
    public class AnimationDoctorTests
    {
        private static AnimationDoctor.ActorDiagnostic HealthyActor()
        {
            var d = new AnimationDoctor.ActorDiagnostic
            {
                ActorName = "Hero",
                HasBlendGroupTimer = true,
                HasSmoothBuffer = true,
                HasRigDefinition = true,
                RigEnabled = true,
                ApplyRootMotion = false,
                RigBoneCount = 10,
                HasAtpBuffer = true,
                AtpCount = 1,
            };

            d.Entries.Add(new AnimationDoctor.DoctorEntry
            {
                LayerIndex = 0,
                ClipHash = new Hash128(1u, 0u, 0u, 0u),
                CurrentWeight = 0.8f,
                TargetWeight = 1f,
                RotationOffset = quaternion.identity,
                HashFound = true,
                MaskIncludedBoneCount = -1,
                MaskResolved = true,
            });

            d.Requests.Add(new AnimationDoctor.DoctorRequest
            {
                Label = "Single clip on 'Attack'",
                ClipHash = new Hash128(1u, 0u, 0u, 0u),
                Weight = 1f,
                HasTrackData = true,
                HashFound = true,
            });

            return d;
        }

        private static bool Has(List<AnimationDoctor.DoctorFinding> findings, DoctorCode code)
        {
            foreach (var f in findings)
            {
                if (f.Code == code)
                {
                    return true;
                }
            }

            return false;
        }

        [Test]
        public void HealthyActor_ProducesNoFindings()
        {
            var findings = AnimationDoctor.Diagnose(HealthyActor());
            Assert.IsEmpty(findings, "A well-configured actor should raise no silent-failure findings.");
        }

        [Test]
        public void NotAnimationActor_WhenNoBlendState()
        {
            var d = new AnimationDoctor.ActorDiagnostic { ActorName = "Prop" };
            var findings = AnimationDoctor.Diagnose(d);
            Assert.IsTrue(Has(findings, DoctorCode.NotAnimationActor));
        }

        [Test]
        public void MissingRig_WhenActorHasNoRigDefinition()
        {
            var d = HealthyActor();
            d.HasRigDefinition = false;
            var findings = AnimationDoctor.Diagnose(d);
            Assert.IsTrue(Has(findings, DoctorCode.MissingRig));
        }

        [Test]
        public void RigDisabled_WhenRigComponentDisabled()
        {
            var d = HealthyActor();
            d.RigEnabled = false;
            var findings = AnimationDoctor.Diagnose(d);
            Assert.IsTrue(Has(findings, DoctorCode.RigDisabled));
        }

        [Test]
        public void GpuTag_WhenGpuAndPackageComponents()
        {
            var d = HealthyActor();
            d.HasGpuTag = true;
            var findings = AnimationDoctor.Diagnose(d);
            Assert.IsTrue(Has(findings, DoctorCode.GpuTag));
        }

        [Test]
        public void Culled_WhenCullTagEnabled()
        {
            var d = HealthyActor();
            d.IsCulled = true;
            var findings = AnimationDoctor.Diagnose(d);
            Assert.IsTrue(Has(findings, DoctorCode.RigCulled));
        }

        [Test]
        public void MissingHash_WhenSingleClipHashNotFound()
        {
            var d = HealthyActor();
            d.Requests[0] = new AnimationDoctor.DoctorRequest
            {
                Label = "Single clip on 'Attack'",
                HasTrackData = true,
                HashFound = false,
                Weight = 1f,
            };

            var findings = AnimationDoctor.Diagnose(d);
            Assert.IsTrue(Has(findings, DoctorCode.MissingHash));
        }

        [Test]
        public void MissingHash_WhenBlendTreeMotionHashesMissing()
        {
            var d = HealthyActor();
            d.Requests.Add(new AnimationDoctor.DoctorRequest
            {
                Label = "Blend Tree 2D on 'Locomotion'",
                IsBlendTree = true,
                HasTrackData = true,
                Weight = 1f,
                MissingMotionHashes = 2,
            });

            var findings = AnimationDoctor.Diagnose(d);
            Assert.IsTrue(Has(findings, DoctorCode.MissingHash));
        }

        [Test]
        public void MissingTrackData_WhenClipTrackNotBaked()
        {
            var d = HealthyActor();
            d.Requests[0] = new AnimationDoctor.DoctorRequest
            {
                Label = "Single clip on 'Attack'",
                HasTrackData = false,
                HashFound = true,
                Weight = 1f,
            };

            var findings = AnimationDoctor.Diagnose(d);
            Assert.IsTrue(Has(findings, DoctorCode.MissingTrackData));
        }

        [Test]
        public void BlendTreeNoMotions_WhenEmptyTree()
        {
            var d = HealthyActor();
            d.Requests.Add(new AnimationDoctor.DoctorRequest
            {
                Label = "Blend Tree 1D on 'Speed'",
                IsBlendTree = true,
                HasTrackData = true,
                MotionsEmpty = true,
                Weight = 1f,
            });

            var findings = AnimationDoctor.Diagnose(d);
            Assert.IsTrue(Has(findings, DoctorCode.BlendTreeNoMotions));
        }

        [Test]
        public void ZeroClipWeight_WhenRequestWeightZero()
        {
            var d = HealthyActor();
            d.Requests[0] = new AnimationDoctor.DoctorRequest
            {
                Label = "Single clip on 'Attack'",
                HasTrackData = true,
                HashFound = true,
                Weight = 0f,
            };

            var findings = AnimationDoctor.Diagnose(d);
            Assert.IsTrue(Has(findings, DoctorCode.ZeroClipWeight));
        }

        [Test]
        public void OffsetsWithoutRootMotion_WhenEntryHasOffsetAndNoRootMotion()
        {
            var d = HealthyActor();
            d.ApplyRootMotion = false;
            var e = d.Entries[0];
            e.PositionOffset = new float3(0f, 0f, 2f);
            d.Entries[0] = e;

            var findings = AnimationDoctor.Diagnose(d);
            Assert.IsTrue(Has(findings, DoctorCode.OffsetsWithoutRootMotion));
        }

        [Test]
        public void OffsetsWithoutRootMotion_NotFlagged_WhenRootMotionOn()
        {
            var d = HealthyActor();
            d.ApplyRootMotion = true;
            var e = d.Entries[0];
            e.PositionOffset = new float3(0f, 0f, 2f);
            d.Entries[0] = e;

            var findings = AnimationDoctor.Diagnose(d);
            Assert.IsFalse(Has(findings, DoctorCode.OffsetsWithoutRootMotion));
        }

        [Test]
        public void OffsetsWithoutRootMotion_CoversFallbackOffsets()
        {
            var d = HealthyActor();
            d.ApplyRootMotion = false;
            d.FallbackPositionOffset = new float3(1f, 0f, 0f);

            var findings = AnimationDoctor.Diagnose(d);
            Assert.IsTrue(Has(findings, DoctorCode.OffsetsWithoutRootMotion));
        }

        [Test]
        public void LayerWeightOrphan_WhenOverrideTargetsUnusedLayer()
        {
            var d = HealthyActor();
            d.LayerOverrides.Add(new AnimationDoctor.DoctorLayerOverride { LayerIndex = 3, Multiplier = 1f });

            var findings = AnimationDoctor.Diagnose(d);
            Assert.IsTrue(Has(findings, DoctorCode.LayerWeightOrphan));
        }

        [Test]
        public void LayerWeightZero_WhenLayerFadedToZero()
        {
            var d = HealthyActor();

            // Layer 0 exists (the healthy entry uses it), so this is not an orphan — it is a fade-to-zero.
            d.LayerOverrides.Add(new AnimationDoctor.DoctorLayerOverride { LayerIndex = 0, Multiplier = 0f });

            var findings = AnimationDoctor.Diagnose(d);
            Assert.IsTrue(Has(findings, DoctorCode.LayerWeightZero));
            Assert.IsFalse(Has(findings, DoctorCode.LayerWeightOrphan));
        }

        [Test]
        public void MaskExcludesAllBones_WhenMaskIncludesZero()
        {
            var d = HealthyActor();
            var e = d.Entries[0];
            e.AvatarMaskHash = new Hash128(9u, 0u, 0u, 0u);
            e.MaskResolved = true;
            e.MaskIncludedBoneCount = 0;
            d.Entries[0] = e;

            var findings = AnimationDoctor.Diagnose(d);
            Assert.IsTrue(Has(findings, DoctorCode.MaskExcludesAllBones));
        }

        [Test]
        public void MaskBlobMissing_WhenMaskHashUnresolved()
        {
            var d = HealthyActor();
            var e = d.Entries[0];
            e.AvatarMaskHash = new Hash128(9u, 0u, 0u, 0u);
            e.MaskResolved = false;
            e.MaskIncludedBoneCount = -1;
            d.Entries[0] = e;

            var findings = AnimationDoctor.Diagnose(d);
            Assert.IsTrue(Has(findings, DoctorCode.MaskBlobMissing));
        }

        [Test]
        public void ZeroEffectiveWeight_WhenAllEntriesNearZero()
        {
            var d = HealthyActor();
            var e = d.Entries[0];
            e.CurrentWeight = 0f;
            d.Entries[0] = e;

            var findings = AnimationDoctor.Diagnose(d);
            Assert.IsTrue(Has(findings, DoctorCode.ZeroEffectiveWeight));
        }

        [Test]
        public void NoActiveClips_WhenIdle()
        {
            var d = new AnimationDoctor.ActorDiagnostic
            {
                ActorName = "Idler",
                HasBlendGroupTimer = true,
                HasSmoothBuffer = true,
                HasRigDefinition = true,
                RigEnabled = true,
                RigBoneCount = 10,
            };

            var findings = AnimationDoctor.Diagnose(d);
            Assert.IsTrue(Has(findings, DoctorCode.NoActiveClips));
        }

        [Test]
        public void HasPositionOffset_DistinguishesZeroFromReal()
        {
            Assert.IsFalse(AnimationDoctor.HasPositionOffset(float3.zero));
            Assert.IsTrue(AnimationDoctor.HasPositionOffset(new float3(0f, 0f, 0.5f)));
        }

        [Test]
        public void HasRotationOffset_TreatsIdentityAndZeroAsNoOffset()
        {
            Assert.IsFalse(AnimationDoctor.HasRotationOffset(quaternion.identity));
            Assert.IsFalse(AnimationDoctor.HasRotationOffset(new quaternion(0f, 0f, 0f, 0f)));
            Assert.IsTrue(AnimationDoctor.HasRotationOffset(quaternion.Euler(0f, 1.2f, 0f)));
        }
    }
}
