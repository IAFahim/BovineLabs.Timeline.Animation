using BovineLabs.Testing;
using BovineLabs.Timeline.Data;
using NUnit.Framework;
using Rukhanka;
using Unity.Entities;
using Unity.Mathematics;
using Hash128 = Unity.Entities.Hash128;

namespace BovineLabs.Timeline.Animation.Tests
{
    public class TimelineFallbackOverrideSystemTests : ECSTestsFixture
    {
        private static readonly Hash128 DefaultClip = new Hash128(1u, 2u, 3u, 4u);
        private static readonly Hash128 OverrideClip = new Hash128(5u, 6u, 7u, 8u);

        [Test]
        public void LatchesTrackOverride_WhileClipActive()
        {
            var system = World.CreateSystem<TimelineFallbackOverrideSystem>();
            var actor = CreateActor();
            var track = CreateTrack(MakeOverride(OverrideClip, blendIn: 5f, blendOut: 7f, FallbackPlaybackMode.Clamp, layer: 1));
            CreateActiveClip(actor, track);

            system.Update(WorldUnmanaged);
            Manager.CompleteAllTrackedJobs();

            var fb = Manager.GetComponentData<FallbackBlend>(actor);
            Assert.AreEqual(OverrideClip, fb.ClipHash, "override clip should latch onto the actor while its clip is active");
            Assert.AreEqual(5f, fb.BlendInSpeed);
            Assert.AreEqual(7f, fb.BlendOutSpeed);
            Assert.AreEqual(FallbackPlaybackMode.Clamp, fb.PlaybackMode);
            Assert.AreEqual(1, fb.LayerIndex);

            World.DestroySystem(system);
        }

        // The regression: once a TrackFallbackOverride latched, the old code never restored the
        // baked DefaultBlendGroupFallback, leaving the actor stuck on a stale override forever.
        [Test]
        public void RestoresDefault_WhenClipNoLongerActive()
        {
            var system = World.CreateSystem<TimelineFallbackOverrideSystem>();
            var actor = CreateActor();
            var track = CreateTrack(MakeOverride(OverrideClip, blendIn: 5f, blendOut: 7f, FallbackPlaybackMode.Clamp, layer: 1));
            var clip = CreateActiveClip(actor, track);

            // Frame 1: clip active -> override latches.
            system.Update(WorldUnmanaged);
            Manager.CompleteAllTrackedJobs();
            Assert.AreEqual(OverrideClip, Manager.GetComponentData<FallbackBlend>(actor).ClipHash);

            // Frame 2: clip ends -> actor has no active override and must restore to default.
            Manager.SetComponentEnabled<ClipActive>(clip, false);
            system.Update(WorldUnmanaged);
            Manager.CompleteAllTrackedJobs();

            var fb = Manager.GetComponentData<FallbackBlend>(actor);
            Assert.AreEqual(DefaultClip, fb.ClipHash, "fallback must restore to the baked default once the override clip ends");
            Assert.AreEqual(2f, fb.BlendInSpeed);
            Assert.AreEqual(3f, fb.BlendOutSpeed);
            Assert.AreEqual(FallbackPlaybackMode.Loop, fb.PlaybackMode);
            Assert.AreEqual(0, fb.LayerIndex);

            World.DestroySystem(system);
        }

        // The early-out fix: latch must update when only the blend params change, not just the clip hash.
        [Test]
        public void Latch_AppliesBlendSpeedChange_OnSameClipHash()
        {
            var system = World.CreateSystem<TimelineFallbackOverrideSystem>();
            var actor = CreateActor();
            var track = CreateTrack(MakeOverride(OverrideClip, blendIn: 5f, blendOut: 7f, FallbackPlaybackMode.Clamp, layer: 1));
            CreateActiveClip(actor, track);

            system.Update(WorldUnmanaged);
            Manager.CompleteAllTrackedJobs();
            Assert.AreEqual(7f, Manager.GetComponentData<FallbackBlend>(actor).BlendOutSpeed);

            // Same clip hash, different blend-out speed — must still take effect.
            Manager.SetComponentData(track, MakeOverride(OverrideClip, blendIn: 5f, blendOut: 99f, FallbackPlaybackMode.Clamp, layer: 1));
            system.Update(WorldUnmanaged);
            Manager.CompleteAllTrackedJobs();

            Assert.AreEqual(99f, Manager.GetComponentData<FallbackBlend>(actor).BlendOutSpeed,
                "a blend-speed-only change on the same clip hash must update the latched fallback");

            World.DestroySystem(system);
        }

        private Entity CreateActor()
        {
            var actor = Manager.CreateEntity();
            var fallback = new FallbackBlend
            {
                ClipHash = DefaultClip,
                BlendInSpeed = 2f,
                BlendOutSpeed = 3f,
                PlaybackMode = FallbackPlaybackMode.Loop,
                LayerIndex = 0,
                BlendMode = AnimationBlendingMode.Override,
                RotationOffset = quaternion.identity,
            };
            Manager.AddComponentData(actor, fallback);
            Manager.AddComponentData(actor, new DefaultBlendGroupFallback
            {
                ClipHash = fallback.ClipHash,
                BlendInSpeed = fallback.BlendInSpeed,
                BlendOutSpeed = fallback.BlendOutSpeed,
                PlaybackMode = fallback.PlaybackMode,
                LayerIndex = fallback.LayerIndex,
                BlendMode = fallback.BlendMode,
                RotationOffset = quaternion.identity,
            });
            return actor;
        }

        private static TrackFallbackOverride MakeOverride(Hash128 clip, float blendIn, float blendOut,
            FallbackPlaybackMode mode, int layer)
        {
            return new TrackFallbackOverride
            {
                FallbackClipHash = clip,
                BlendInSpeed = blendIn,
                BlendOutSpeed = blendOut,
                PlaybackMode = mode,
                LayerIndex = layer,
                BlendMode = AnimationBlendingMode.Override,
                RotationOffset = quaternion.identity,
                RemoveStartOffset = true,
                ApplyFootIK = true,
            };
        }

        private Entity CreateTrack(TrackFallbackOverride over)
        {
            var track = Manager.CreateEntity();
            Manager.AddComponentData(track, over);
            return track;
        }

        private Entity CreateActiveClip(Entity actor, Entity track)
        {
            var clip = Manager.CreateEntity(typeof(Clip), typeof(TrackBinding), typeof(ClipActive), typeof(TimelineActive));
            Manager.SetComponentData(clip, new Clip { Track = track });
            Manager.SetComponentData(clip, new TrackBinding { Value = actor });
            Manager.SetComponentEnabled<ClipActive>(clip, true);
            Manager.SetComponentEnabled<TimelineActive>(clip, true);
            return clip;
        }
    }
}
