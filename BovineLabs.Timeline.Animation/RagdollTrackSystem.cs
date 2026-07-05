using BovineLabs.Timeline.Data;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;

namespace BovineLabs.Timeline.Animation
{
    /// <summary>
    /// Toggles each bound rig's <see cref="ActiveRagdoll"/> switch from its RagdollClips. While a clip is active
    /// the switch is enabled; on the clip's end edge it is disabled (returning to animation) — unless the clip is
    /// latched (<c>stayRagdolled</c>), in which case it is left on. RagdollApplySystem does the actual physics
    /// work off the switch's enabled edge. Disable runs before enable so a hand-off between two clips on the same
    /// rig in one frame keeps the ragdoll on.
    /// </summary>
    [UpdateInGroup(typeof(TimelineComponentAnimationGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.LocalSimulation | WorldSystemFilterFlags.ClientSimulation |
                       WorldSystemFilterFlags.ServerSimulation)]
    public partial struct RagdollTrackSystem : ISystem
    {
        private ComponentLookup<ActiveRagdoll> _activeRagdoll;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            _activeRagdoll = state.GetComponentLookup<ActiveRagdoll>();
            // Gate on ragdoll clips existing — NOT on ActiveRagdoll, which starts disabled (enableable): a
            // RequireForUpdate<ActiveRagdoll> query excludes disabled-enableable entities, so this system could
            // never run to enable it in the first place.
            state.RequireForUpdate<RagdollClipTag>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            _activeRagdoll.Update(ref state);

            // End edge first (ClipActivePrevious && !ClipActive): disable, unless the clip latches the ragdoll on.
            state.Dependency = new DisableJob { ActiveRagdoll = _activeRagdoll }.Schedule(state.Dependency);

            // Active (this frame): enable. Runs last so a new clip wins a same-frame hand-off. Idempotent.
            state.Dependency = new EnableJob { ActiveRagdoll = _activeRagdoll }.Schedule(state.Dependency);
        }

        [BurstCompile]
        [WithAll(typeof(ClipActive), typeof(TimelineActive))]
        private partial struct EnableJob : IJobEntity
        {
            [NativeDisableParallelForRestriction] public ComponentLookup<ActiveRagdoll> ActiveRagdoll;

            private void Execute(in RagdollClipTag tag, in TrackBinding binding)
            {
                if (binding.Value != Entity.Null && ActiveRagdoll.HasComponent(binding.Value))
                {
                    ActiveRagdoll.SetComponentEnabled(binding.Value, true);
                }
            }
        }

        [BurstCompile]
        [WithAll(typeof(ClipActivePrevious))]
        [WithNone(typeof(ClipActive))]
        private partial struct DisableJob : IJobEntity
        {
            [NativeDisableParallelForRestriction] public ComponentLookup<ActiveRagdoll> ActiveRagdoll;

            private void Execute(in RagdollClipTag tag, in TrackBinding binding)
            {
                if (tag.Latch)
                {
                    return;
                }

                if (binding.Value != Entity.Null && ActiveRagdoll.HasComponent(binding.Value))
                {
                    ActiveRagdoll.SetComponentEnabled(binding.Value, false);
                }
            }
        }
    }
}
