#if UNITY_EDITOR || BL_DEBUG
using Rukhanka;
using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;

namespace BovineLabs.Timeline.Animation
{
    [UpdateInGroup(typeof(TimelineComponentAnimationGroup))]
    [UpdateAfter(typeof(TimelineAnimationUnificationSystem))]
    [RequireMatchingQueriesForUpdate]
    [WorldSystemFilter(WorldSystemFilterFlags.LocalSimulation | WorldSystemFilterFlags.ClientSimulation |
                       WorldSystemFilterFlags.ServerSimulation)]
    public partial struct AnimationDebugSystem : ISystem
    {
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            foreach (var (smoothBuf, playbackBuf, timer, fallback, debug) in
                     SystemAPI.Query<
                         DynamicBuffer<SmoothBlendGroupEntry>,
                         DynamicBuffer<BlendTreePlaybackStateElement>,
                         RefRO<BlendGroupTimer>,
                         RefRO<FallbackBlend>,
                         RefRW<AnimationDebugState>>())
            {
                var fb = fallback.ValueRO;
                ref var d = ref debug.ValueRW;

                d.ActiveTrackCount = playbackBuf.Length;
                d.ActiveClipCount = smoothBuf.Length;
                d.BlendInSpeed = fb.BlendInSpeed;
                d.BlendOutSpeed = fb.BlendOutSpeed;
                d.PlaybackMode = fb.PlaybackMode;

                var overrideWeight = 0f;
                var fadingClips = 0;
                for (var i = 0; i < smoothBuf.Length; i++)
                {
                    var s = smoothBuf[i];
                    if (s.BlendMode == AnimationBlendingMode.Override && s.LayerIndex == fb.LayerIndex)
                        overrideWeight += s.CurrentWeight;
                    if (s.TargetWeight <= 0.0001f && s.CurrentWeight > 0.0001f)
                        fadingClips++;
                }

                d.FallbackWeight = math.max(0f, 1f - math.min(1f, overrideWeight));
                d.FallbackTrackCount = fadingClips;
            }
        }
    }
}
#endif