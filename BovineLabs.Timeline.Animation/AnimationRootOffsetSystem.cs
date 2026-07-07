using Rukhanka;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;

namespace BovineLabs.Timeline.Animation
{
    /// <summary>
    /// Applies the clip/track/fallback transform offsets (<c>positionOffset</c> / <c>rotationOffset</c>) to a rig's
    /// root bone OUTSIDE the Rukhanka fork (spike #27). Previously the fork's <c>ComputeBoneAnimationJob</c> composed
    /// each clip's offset onto the root-motion delta bone and forced its TRS flags — ~35 lines of hand-maintained
    /// patch inside the engine's hottest job, gated behind <c>applyRootMotion</c> so offsets silently did nothing on
    /// every non-root-motion rig (all shipping content). This system reads the same per-clip offsets and weights from
    /// the stream's <see cref="AnimationToProcessComponent"/> buffer and composes their weighted blend onto bone 0 via
    /// <see cref="AnimationStream"/>, so offsets now work on ALL CPU rigs with a clear, well-defined semantic (a static
    /// visual translation/rotation of the character root in rig space) and the fork keeps only the tiny
    /// <c>removeStartOffset</c> reshape of the root-motion delta.
    ///
    /// Placement mirrors <see cref="InertializationSystem"/> (post pose + IK, pre skinning); ordered BEFORE it so the
    /// offset is part of the pose inertialization smooths and applies to, exactly as it was upstream in the fork.
    ///
    /// CPU-only by construction: it writes the CPU <c>animatedBonesBuffer</c> that the GPU animation engine never
    /// populates, so GPU rigs are excluded (<see cref="GPUAnimationEngineTag"/> disabled) and keep needing the offset
    /// applied inside the GPU pipeline (GPUStructures + ProcessAnimations.hlsl). The unification system already logs a
    /// one-shot error when a GPU rig carries timeline-animation state, so this exclusion is not a new silent failure.
    /// </summary>
    [UpdateInGroup(typeof(RukhankaAnimationSystemGroup))]
    [UpdateAfter(typeof(RukhankaAnimationInjectionSystemGroup))]
    [UpdateBefore(typeof(InertializationSystem))]
    public partial struct AnimationRootOffsetSystem : ISystem
    {
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<RuntimeAnimationData>();

            var query = SystemAPI.QueryBuilder()
                .WithAll<RigDefinitionComponent, AnimationToProcessComponent>()
                .WithDisabled<GPUAnimationEngineTag>()
                .Build();

            state.RequireForUpdate(query);
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            if (!SystemAPI.TryGetSingletonRW<RuntimeAnimationData>(out var runtimeData))
            {
                return;
            }

            var job = new RootOffsetJob
            {
                runtimeData = runtimeData.ValueRW,
                cullLookup = SystemAPI.GetComponentLookup<CullAnimationsTag>(true),
            };

            job.ScheduleParallel();
        }

        [BurstCompile]
        [WithDisabled(typeof(GPUAnimationEngineTag))]
        private partial struct RootOffsetJob : IJobEntity
        {
            // Each rig writes only into its own bone-pose range (bone 0), so parallel scheduling never overlaps. Same
            // shared-buffer disable pattern InertializationSystem / DynamicBoneChainSystem use.
            [NativeDisableContainerSafetyRestriction]
            public RuntimeAnimationData runtimeData;

            [ReadOnly]
            public ComponentLookup<CullAnimationsTag> cullLookup;

            private void Execute(
                Entity e,
                in RigDefinitionComponent rigDef,
                in DynamicBuffer<AnimationToProcessComponent> atps)
            {
                // Culled rigs: Rukhanka skips pose computation, so the pose we'd read/write is stale. Leave it alone.
                if (cullLookup.HasComponent(e) && cullLookup.IsComponentEnabled(e))
                {
                    return;
                }

                // Blend the active clips' offsets by the same per-clip ATP weight the pose blend uses.
                var acc = default(RootOffsetAccumulator);
                for (var i = 0; i < atps.Length; i++)
                {
                    var atp = atps[i];
                    if (atp.weight <= 0f || atp.layerWeight <= 0f ||
                        atp.animation == BlobAssetReference<AnimationClipBlob>.Null)
                    {
                        continue;
                    }

                    acc.Add(atp.weight, atp.positionOffset, atp.rotationOffset);
                }

                if (!acc.TryResolve(out var offsetPos, out var offsetRot))
                {
                    return;
                }

                // Zero-offset fast path (the common case): composing identity would be a no-op — skip the stream write.
                if (AnimationRootOffsetMath.IsIdentityOffset(offsetPos, offsetRot))
                {
                    return;
                }

                using var stream = AnimationStream.Create(runtimeData, rigDef);
                if (stream.rigFrameData.rigBoneCount <= 0)
                {
                    return;
                }

                var rootLocal = stream.GetLocalPose(0);
                stream.SetLocalPose(0, AnimationRootOffsetMath.ComposeOntoRoot(rootLocal, offsetPos, offsetRot));
            }
        }
    }
}
