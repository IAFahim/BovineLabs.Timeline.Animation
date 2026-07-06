using BovineLabs.Testing;
using BovineLabs.Timeline.Data;
using NUnit.Framework;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace BovineLabs.Timeline.Animation.Tests
{
    public class AfterImageSystemTests : ECSTestsFixture
    {
        [Test]
        public void GhostDestroyedExternally_WhileClipActive_ResetsAndRespawns()
        {
            var source = Manager.CreateEntity(typeof(LocalToWorld));
            Manager.SetComponentData(source, new LocalToWorld { Value = float4x4.identity });

            var prefab = Manager.CreateEntity(typeof(Prefab), typeof(LocalTransform));
            Manager.SetComponentData(prefab, LocalTransform.Identity);

            var clip = CreateActiveClip(source, prefab);

            var spawned = Tick();
            Assert.AreNotEqual(Entity.Null, spawned, "an active AfterImage clip must spawn a ghost");
            Assert.IsTrue(Manager.Exists(spawned));
            Assert.AreEqual(spawned, Manager.GetComponentData<AfterImageClipData>(clip).SpawnedEntity);

            // Destroy the ghost externally (prefab lifetime / scene unload / gameplay cleanup).
            Manager.DestroyEntity(spawned);

            // The dead pointer must not spend the clip: it clears to honest and respawns while still active.
            RunSystems();
            var respawned = Manager.GetComponentData<AfterImageClipData>(clip).SpawnedEntity;

            Assert.AreNotEqual(Entity.Null, respawned, "a dead ghost must be replaced, not leave the clip spent");
            Assert.AreNotEqual(spawned, respawned);
            Assert.IsTrue(Manager.Exists(respawned));
        }

        [Test]
        public void ClipDestroyed_ReconcilesOrphanedGhost()
        {
            var source = Manager.CreateEntity(typeof(LocalToWorld));
            Manager.SetComponentData(source, new LocalToWorld { Value = float4x4.identity });

            var prefab = Manager.CreateEntity(typeof(Prefab), typeof(LocalTransform));
            Manager.SetComponentData(prefab, LocalTransform.Identity);

            var clip = CreateActiveClip(source, prefab);

            var spawned = Tick();
            Assert.IsTrue(Manager.Exists(spawned));

            // The owning clip disappears (timeline torn down) — the ghost is now an orphan.
            Manager.DestroyEntity(clip);

            RunSystems();

            Assert.IsFalse(Manager.Exists(spawned), "an orphaned ghost must be reconciled away, leaving no zombie");
        }

        private Entity Tick()
        {
            var clipEntity = FindClip();
            RunSystems();
            return Manager.GetComponentData<AfterImageClipData>(clipEntity).SpawnedEntity;
        }

        private Entity FindClip()
        {
            using var query = Manager.CreateEntityQuery(typeof(AfterImageClipData));
            using var entities = query.ToEntityArray(Unity.Collections.Allocator.Temp);
            return entities[0];
        }

        private void RunSystems()
        {
            var ecbSystem = World.CreateSystem<BeginSimulationEntityCommandBufferSystem>();
            var system = World.CreateSystem<AfterImageSpawnSystem>();
            system.Update(WorldUnmanaged);
            ecbSystem.Update(WorldUnmanaged);
            Manager.CompleteAllTrackedJobs();
            World.DestroySystem(system);
            World.DestroySystem(ecbSystem);
        }

        private Entity CreateActiveClip(Entity source, Entity prefab)
        {
            var track = Manager.CreateEntity(typeof(AfterImageTrackData));
            Manager.SetComponentData(track, new AfterImageTrackData { Prefab = prefab });

            var clip = Manager.CreateEntity(
                typeof(AfterImageClipData),
                typeof(TrackBinding),
                typeof(Clip),
                typeof(ClipActive));

            Manager.SetComponentData(clip, new AfterImageClipData { SpawnedEntity = Entity.Null });
            Manager.SetComponentData(clip, new TrackBinding { Value = source });
            Manager.SetComponentData(clip, new Clip { Track = track });
            Manager.SetComponentEnabled<ClipActive>(clip, true);
            return clip;
        }
    }
}
