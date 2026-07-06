using System.Collections.Generic;
using BovineLabs.Testing;
using NUnit.Framework;
using Rukhanka;
using Unity.Collections;
using Unity.Core;
using Unity.Entities;
using Unity.Mathematics;
using Hash128 = Unity.Entities.Hash128;

namespace BovineLabs.Timeline.Animation.Tests
{
    public class UnificationSystemTests : ECSTestsFixture
    {
        private const float DeltaTime = 1f / 60f;

        private static readonly Hash128 LoopedClip = new(1u, 2u, 3u, 4u);
        private static readonly Hash128 ClampedClip = new(5u, 6u, 7u, 8u);
        private static readonly Hash128 FallbackClip = new(9u, 10u, 11u, 12u);
        private static readonly Hash128 UnknownClip = new(90u, 91u, 92u, 93u);

        private readonly List<BlobAssetReference<AnimationClipBlob>> clipBlobs = new();

        private NativeHashMap<Hash128, BlobAssetReference<AnimationClipBlob>> animations;
        private NativeHashMap<Hash128, BlobAssetReference<AvatarMaskBlob>> avatarMasks;
        private double elapsed;

        [SetUp]
        public override void Setup()
        {
            base.Setup();

            this.animations = new NativeHashMap<Hash128, BlobAssetReference<AnimationClipBlob>>(8, Allocator.Persistent);
            this.avatarMasks = new NativeHashMap<Hash128, BlobAssetReference<AvatarMaskBlob>>(8, Allocator.Persistent);

            this.animations.Add(LoopedClip, this.Clip(LoopedClip, 1f, true));
            this.animations.Add(ClampedClip, this.Clip(ClampedClip, 1f, false));
            this.animations.Add(FallbackClip, this.Clip(FallbackClip, 1f, true));

            var db = this.Manager.CreateEntity(typeof(BlobDatabaseSingleton));
            this.Manager.SetComponentData(db, new BlobDatabaseSingleton
            {
                animations = this.animations,
                avatarMasks = this.avatarMasks,
            });
        }

        [TearDown]
        public override void TearDown()
        {
            base.TearDown();

            foreach (var blob in this.clipBlobs)
                if (blob.IsCreated)
                    blob.Dispose();
            this.clipBlobs.Clear();

            if (this.animations.IsCreated) this.animations.Dispose();
            if (this.avatarMasks.IsCreated) this.avatarMasks.Dispose();
        }

        // ---- Pure math helpers (item 5 fade-out advance, item 6 continuous seeding) ----
        // These bypass the Application.isPlaying scrub gate, which pins DeltaTime to 0 in EditMode.

        [Test]
        public void AdvanceNormalizedTime_Looped_Wraps()
        {
            var t = TimelineAnimationUnificationSystem.AdvanceNormalizedTime(0.9f, 0.2f, true);
            Assert.AreEqual(0.1f, t, 1e-5f, "a looped clip past 1 must wrap via frac");
        }

        [Test]
        public void AdvanceNormalizedTime_Clamped_PinsAtOne()
        {
            var pinned = TimelineAnimationUnificationSystem.AdvanceNormalizedTime(0.9f, 0.2f, false);
            Assert.AreEqual(1f, pinned, "a clamped clip must pin at 1, never wrap");

            var midway = TimelineAnimationUnificationSystem.AdvanceNormalizedTime(0.5f, 0.25f, false);
            Assert.AreEqual(0.75f, midway, 1e-5f, "a clamped clip below 1 advances linearly");
        }

        [Test]
        public void ShouldResyncPhase_ContinuousPlayingSeeded_IgnoresRequest()
        {
            // Continuous loop, playing, already seeded -> owns its free-running phase, ignores the request time.
            Assert.IsFalse(TimelineAnimationUnificationSystem.ShouldResyncPhase(true, false, true));
            // Continuous loop, playing, not yet seeded -> seeds once.
            Assert.IsTrue(TimelineAnimationUnificationSystem.ShouldResyncPhase(true, false, false));
            // Scrubbing re-syncs even a seeded continuous entry so a scrub lands exactly.
            Assert.IsTrue(TimelineAnimationUnificationSystem.ShouldResyncPhase(true, true, true));
            // Non-continuous entries always track the request.
            Assert.IsTrue(TimelineAnimationUnificationSystem.ShouldResyncPhase(false, false, true));
        }

        // ---- System-driven behaviors (scrub-gate independent) ----

        [Test]
        public void ResolveTimeScale_DominantEntry_PropagatesToTimer()
        {
            var actor = this.CreateActor(default);
            this.Manager.GetBuffer<BlendGroupEntry>(actor).Add(
                MakeEntry(LoopedClip, 1f, 0.5f, motionId: 1u));

            this.Update(actor);

            Assert.AreEqual(0.5f, this.Manager.GetComponentData<BlendGroupTimer>(actor).TimeScale, 1e-5f,
                "the dominant clip's TimeScale must drive the fallback/ramp clock");
        }

        [Test]
        public void ResolveTimeScale_NoEntries_DefaultsToOne()
        {
            var actor = this.CreateActor(default);

            this.Update(actor);

            Assert.AreEqual(1f, this.Manager.GetComponentData<BlendGroupTimer>(actor).TimeScale,
                "pure fallback idle must run at real speed (scale 1)");
        }

        [Test]
        public void ResolveTimeScale_BlendTreeEntry_DefaultsToOne()
        {
            // Blend-tree gatherers leave TimeScale 0 until they thread scale; unification treats <=0 as 1.
            var actor = this.CreateActor(default);
            this.Manager.GetBuffer<BlendGroupEntry>(actor).Add(
                MakeEntry(LoopedClip, 1f, 0f, motionId: 1u));

            this.Update(actor);

            Assert.AreEqual(1f, this.Manager.GetComponentData<BlendGroupTimer>(actor).TimeScale,
                "an entry with TimeScale 0 (blend-tree) resolves to 1");
        }

        [Test]
        public void ContinuousLoop_SeedsPhaseOnce()
        {
            var actor = this.CreateActor(default);
            this.Manager.GetBuffer<BlendGroupEntry>(actor).Add(new BlendGroupEntry
            {
                LayerIndex = 0,
                ClipHash = LoopedClip,
                NormalizedTime = 0.3f,
                Weight = 1f,
                BlendMode = AnimationBlendingMode.Override,
                MotionId = 1u,
                ContinuousLoop = true,
                PhaseVelocity = 1f,
                TimeScale = 1f,
            });

            this.Update(actor);

            var smooth = this.Manager.GetBuffer<SmoothBlendGroupEntry>(actor);
            Assert.AreEqual(1, smooth.Length);
            Assert.IsTrue(smooth[0].PhaseSeeded, "a continuous-loop entry is seeded on first appearance");
            Assert.AreEqual(0.3f, smooth[0].NormalizedTime, 1e-5f, "the seed value comes from the request");
        }

        [Test]
        public void Culled_EmitsNothing_AndSnapsWeights()
        {
            var actor = this.CreateActor(default);
            this.Manager.AddComponent<CullAnimationsTag>(actor);
            this.Manager.SetComponentEnabled<CullAnimationsTag>(actor, true);

            // A mid-blend smooth entry and a fresh request that must both be frozen / discarded while culled.
            this.Manager.GetBuffer<SmoothBlendGroupEntry>(actor).Add(new SmoothBlendGroupEntry
            {
                ClipHash = LoopedClip,
                CurrentWeight = 0.2f,
                TargetWeight = 0.9f,
                BlendMode = AnimationBlendingMode.Override,
                MotionId = 1u,
            });
            this.Manager.GetBuffer<BlendGroupEntry>(actor).Add(MakeEntry(LoopedClip, 1f, 1f, motionId: 2u));

            this.Update(actor);

            Assert.AreEqual(0, this.Manager.GetBuffer<AnimationToProcessComponent>(actor).Length,
                "a culled rig emits no animations");

            var smooth = this.Manager.GetBuffer<SmoothBlendGroupEntry>(actor);
            Assert.AreEqual(1, smooth.Length, "culling must not reconcile in the new request");
            Assert.AreEqual(0.9f, smooth[0].CurrentWeight, 1e-5f,
                "culling snaps CurrentWeight to TargetWeight so un-cull resumes cleanly");
            Assert.AreEqual(0, this.Manager.GetBuffer<BlendGroupEntry>(actor).Length,
                "gathered requests are cleared even while culled");
        }

        [Test]
        public void OverrideClip_Emitted_NoFallbackWhenBaseFull()
        {
            var actor = this.CreateActor(default);
            this.Manager.GetBuffer<BlendGroupEntry>(actor).Add(MakeEntry(LoopedClip, 1f, 1f, motionId: 1u));

            this.Update(actor);

            var atps = this.Manager.GetBuffer<AnimationToProcessComponent>(actor);
            Assert.AreEqual(1, atps.Length, "a full-weight override clip emits exactly one animation, no fallback");
            Assert.AreEqual(1u, atps[0].motionId);
            Assert.AreEqual(0, atps[0].layerIndex);
        }

        [Test]
        public void Fallback_Emitted_WhenNoOverrideClips()
        {
            var actor = this.CreateActor(FallbackClip);

            this.Update(actor);

            var atps = this.Manager.GetBuffer<AnimationToProcessComponent>(actor);
            Assert.AreEqual(1, atps.Length, "with no clips the fallback fills the base layer");
            Assert.AreEqual(MotionId.Fallback, atps[0].motionId);
        }

        [Test]
        public void MissingHash_Entry_EmitsNothing()
        {
            var actor = this.CreateActor(default);
            this.Manager.GetBuffer<BlendGroupEntry>(actor).Add(MakeEntry(UnknownClip, 1f, 1f, motionId: 1u));

            this.Update(actor);

            Assert.AreEqual(0, this.Manager.GetBuffer<AnimationToProcessComponent>(actor).Length,
                "a clip whose hash is absent from the blob database is skipped");
        }

        private void Update(Entity actor)
        {
            this.elapsed += DeltaTime;
            this.World.SetTime(new TimeData(this.elapsed, DeltaTime));

            var system = this.World.CreateSystem<TimelineAnimationUnificationSystem>();
            system.Update(this.WorldUnmanaged);
            this.Manager.CompleteAllTrackedJobs();
        }

        private Entity CreateActor(Hash128 fallbackClip)
        {
            var actor = this.Manager.CreateEntity();
            this.Manager.AddComponentData(actor, new BlendGroupTimer());
            this.Manager.AddComponentData(actor, new FallbackBlend
            {
                ClipHash = fallbackClip,
                BlendInSpeed = 0f,
                BlendOutSpeed = 0f,
                PlaybackMode = FallbackPlaybackMode.Loop,
                LayerIndex = 0,
                BlendMode = AnimationBlendingMode.Override,
                RotationOffset = quaternion.identity,
            });
            this.Manager.AddBuffer<BlendGroupEntry>(actor);
            this.Manager.AddBuffer<SmoothBlendGroupEntry>(actor);
            this.Manager.AddBuffer<AnimationToProcessComponent>(actor);
            return actor;
        }

        private static BlendGroupEntry MakeEntry(Hash128 clip, float weight, float timeScale, uint motionId)
        {
            return new BlendGroupEntry
            {
                LayerIndex = 0,
                ClipHash = clip,
                NormalizedTime = 0f,
                Weight = weight,
                BlendMode = AnimationBlendingMode.Override,
                MotionId = motionId,
                RotationOffset = quaternion.identity,
                TimeScale = timeScale,
            };
        }

        private BlobAssetReference<AnimationClipBlob> Clip(Hash128 hash, float length, bool looped)
        {
            var builder = new BlobBuilder(Allocator.Temp);
            ref var root = ref builder.ConstructRoot<AnimationClipBlob>();
            root.hash = hash;
            root.length = length;
            root.looped = looped;
            var blob = builder.CreateBlobAssetReference<AnimationClipBlob>(Allocator.Persistent);
            builder.Dispose();
            this.clipBlobs.Add(blob);
            return blob;
        }
    }
}
