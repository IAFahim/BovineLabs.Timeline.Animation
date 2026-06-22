#if UNITY_EDITOR || BL_DEBUG
using System;
using BovineLabs.Core.ConfigVars;
using BovineLabs.Quill;
using BovineLabs.Timeline.Core.Debug;
using BovineLabs.Timeline.Data;
using Rukhanka;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace BovineLabs.Timeline.Animation.Debug
{
    [Configurable]
    public static class TimelineAnimationDebugConfig
    {
        [ConfigVar("animation.debug.enabled", false, "Enable timeline animation debug visualization.")]
        public static readonly SharedStatic<bool> Enabled = SharedStatic<bool>.GetOrCreate<Tags.Enabled>();

        [ConfigVar("animation.debug.draw-weight-bars", true, "Draw smoothed weight bars per layer.")]
        public static readonly SharedStatic<bool>
            DrawWeightBars = SharedStatic<bool>.GetOrCreate<Tags.DrawWeightBars>();

        [ConfigVar("animation.debug.draw-fallback", true, "Draw fallback state arrow.")]
        public static readonly SharedStatic<bool> DrawFallback = SharedStatic<bool>.GetOrCreate<Tags.DrawFallback>();

        [ConfigVar("animation.debug.draw-blend-trees", true, "Draw 2D blend tree direction arrows and motion points.")]
        public static readonly SharedStatic<bool>
            DrawBlendTrees = SharedStatic<bool>.GetOrCreate<Tags.DrawBlendTrees>();

        [ConfigVar("animation.debug.draw-clip-labels", true, "Draw clip labels with read kind and progress.")]
        public static readonly SharedStatic<bool>
            DrawClipLabels = SharedStatic<bool>.GetOrCreate<Tags.DrawClipLabels>();

        [ConfigVar("animation.debug.draw-motions", true, "Draw 2D blend tree motion point map.")]
        public static readonly SharedStatic<bool> DrawMotions = SharedStatic<bool>.GetOrCreate<Tags.DrawMotions>();

        [ConfigVar("animation.debug.draw-summary", true,
            "Draw the AnimationDebugState summary line (track/clip counts, fallback weight).")]
        public static readonly SharedStatic<bool> DrawSummary = SharedStatic<bool>.GetOrCreate<Tags.DrawSummary>();

        private struct Tags
        {
            public struct DrawSummary
            {
            }

            public struct Enabled
            {
            }

            public struct DrawWeightBars
            {
            }

            public struct DrawFallback
            {
            }

            public struct DrawBlendTrees
            {
            }

            public struct DrawClipLabels
            {
            }

            public struct DrawMotions
            {
            }
        }
    }

    [BurstCompile]
    [UpdateInGroup(typeof(TimelineComponentAnimationGroup))]
    [UpdateAfter(typeof(TimelineAnimationUnificationSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.LocalSimulation | WorldSystemFilterFlags.ClientSimulation |
                       WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.Editor)]
    public partial struct TimelineAnimationQuillDebugSystem : ISystem
    {
        private static readonly FixedString32Bytes Category = "Animation";

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<DrawSystem.Singleton>();
            state.RequireForUpdate<BlendGroupTimer>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            if (!TimelineAnimationDebugConfig.Enabled.Data)
                return;

            var drawSystem = SystemAPI.GetSingleton<DrawSystem.Singleton>();
            var drawer = drawSystem.CreateDrawer<TimelineAnimationQuillDebugSystem>(Category);

            if (!drawer.IsEnabled)
                return;

            var hasViewer = TimelineDebugTier.TryGetViewer(drawSystem.CameraCulling, out var viewer);

            var ltwLookup = SystemAPI.GetComponentLookup<LocalToWorld>(true);
            var clipLookup = SystemAPI.GetComponentLookup<Clip>(true);
            var clipWeightLookup = SystemAPI.GetComponentLookup<ClipWeight>(true);
            var trackDataLookup = SystemAPI.GetComponentLookup<BlendAnimationTree2DTrackData>(true);
            var motionLookup = SystemAPI.GetBufferLookup<BlendTree2DMotionData>(true);

            state.Dependency = new DrawTargetBlendStateJob
            {
                Drawer = drawer,
                Viewer = viewer,
                HasViewer = hasViewer,
                DrawWeightBars = TimelineAnimationDebugConfig.DrawWeightBars.Data,
                DrawFallback = TimelineAnimationDebugConfig.DrawFallback.Data,
                DrawSummary = TimelineAnimationDebugConfig.DrawSummary.Data,
                DebugStateLookup = SystemAPI.GetComponentLookup<AnimationDebugState>(true)
            }.ScheduleParallel(state.Dependency);

            if (TimelineAnimationDebugConfig.DrawBlendTrees.Data)
                state.Dependency = new DrawBlendTreeClipJob
                {
                    Drawer = drawer,
                    Viewer = viewer,
                    HasViewer = hasViewer,
                    LocalToWorldLookup = ltwLookup,
                    ClipLookup = clipLookup,
                    ClipWeightLookup = clipWeightLookup,
                    TrackDataLookup = trackDataLookup,
                    MotionLookup = motionLookup,
                    DrawClipLabels = TimelineAnimationDebugConfig.DrawClipLabels.Data,
                    DrawMotions = TimelineAnimationDebugConfig.DrawMotions.Data
                }.ScheduleParallel(state.Dependency);
        }

        [BurstCompile]
        private partial struct DrawTargetBlendStateJob : IJobEntity
        {
            public Drawer Drawer;
            public float3 Viewer;
            public bool HasViewer;
            public bool DrawWeightBars;
            public bool DrawFallback;
            public bool DrawSummary;

            [ReadOnly] public ComponentLookup<AnimationDebugState> DebugStateLookup;

            private void Execute(
                Entity entity,
                in LocalToWorld localToWorld,
                in BlendGroupTimer timer,
                in FallbackBlend fallback,
                DynamicBuffer<SmoothBlendGroupEntry> smoothEntries,
                DynamicBuffer<AnimationToProcessComponent> animationsToProcess)
            {
                var root = localToWorld.Position;
                var header = root + new float3(0f, 2.15f, 0f);

                var tier = TimelineDebugTier.Resolve(root, Viewer, HasViewer);

                Drawer.Circle(root + new float3(0f, 0.08f, 0f), new float3(0f, 0.35f, 0f), RingColor());

                if (tier >= DebugTier.Mid)
                {
                    if (DrawFallback && math.lengthsq(fallback.PositionOffset) > 0.0001f)
                        Drawer.Arrow(root + new float3(0f, 1.05f, 0f), fallback.PositionOffset, FallbackColor());

                    Drawer.Text32(header, "Anim", TextColor(), 12f);
                }

                if (tier == DebugTier.Close)
                {
                    var label = default(FixedString128Bytes);
                    label.Append((FixedString32Bytes)"Anim sm:");
                    label.Append(smoothEntries.Length);
                    label.Append((FixedString32Bytes)" out:");
                    label.Append(animationsToProcess.Length);
                    label.Append((FixedString32Bytes)" fb:");
                    label.Append((int)fallback.PlaybackMode);
                    Drawer.Text128(header, label, TextColor(), 12f);

                    if (DrawSummary && DebugStateLookup.TryGetComponent(entity, out var dbg))
                    {
                        var summary = default(FixedString128Bytes);
                        summary.Append((FixedString32Bytes)"trk:");
                        summary.Append(dbg.ActiveTrackCount);
                        summary.Append((FixedString32Bytes)" clp:");
                        summary.Append(dbg.ActiveClipCount);
                        summary.Append((FixedString32Bytes)" fbw:");
                        summary.Append(dbg.FallbackWeight);
                        summary.Append((FixedString32Bytes)" fad:");
                        summary.Append(dbg.FallbackTrackCount);
                        Drawer.Text128(header + new float3(0f, 0.2f, 0f), summary, TextColor(), 10f);
                    }

                    if (DrawWeightBars)
                        DrawWeightBarsFn(root + new float3(-0.75f, 1.65f, 0f), smoothEntries);
                }
            }

            private void DrawWeightBarsFn(float3 origin, DynamicBuffer<SmoothBlendGroupEntry> smoothEntries)
            {
                const int MaxBars = 12;
                var count = math.min(MaxBars, smoothEntries.Length);

                for (var i = 0; i < count; i++)
                {
                    var entry = smoothEntries[i];
                    var current = math.saturate(entry.CurrentWeight);
                    var target = math.saturate(entry.TargetWeight);

                    var barRoot = origin + new float3(i * 0.14f, 0f, 0f);
                    var currentTop = barRoot + new float3(0f, current * 0.55f, 0f);
                    var targetPoint = barRoot + new float3(0f, target * 0.55f, 0f);

                    var color = LayerColor(entry.LayerIndex);
                    Drawer.Line(barRoot, currentTop, color);
                    Drawer.Point(targetPoint, 0.035f, TargetWeightColor());

                    if (math.lengthsq(entry.PositionOffset) > 0.0001f)
                        Drawer.Arrow(barRoot + new float3(0f, 0.65f, 0f), entry.PositionOffset * 0.35f, color);
                }
            }
        }

        [BurstCompile]
        [WithAll(typeof(ClipActive))]
        private partial struct DrawBlendTreeClipJob : IJobEntity
        {
            public Drawer Drawer;
            public float3 Viewer;
            public bool HasViewer;

            [ReadOnly] public ComponentLookup<LocalToWorld> LocalToWorldLookup;
            [ReadOnly] public ComponentLookup<Clip> ClipLookup;
            [ReadOnly] public ComponentLookup<ClipWeight> ClipWeightLookup;
            [ReadOnly] public ComponentLookup<BlendAnimationTree2DTrackData> TrackDataLookup;
            [ReadOnly] public BufferLookup<BlendTree2DMotionData> MotionLookup;
            public bool DrawClipLabels;
            public bool DrawMotions;

            private void Execute(
                Entity clipEntity,
                in BlendTree2DDirectionClipData directionClip,
                in TrackBinding binding,
                in LocalTime localTime)
            {
                if (!LocalToWorldLookup.TryGetComponent(binding.Value, out var targetLtw))
                    return;

                var weight = 1f;
                if (ClipWeightLookup.TryGetComponent(clipEntity, out var clipWeight))
                    weight = clipWeight.Value;

                var origin = targetLtw.Position + new float3(0f, 1.25f, 0f);
                var flatDir = new float3(directionClip.Value.x, 0f, directionClip.Value.y);
                var color = BlendTreeColor();

                var tier = TimelineDebugTier.Resolve(targetLtw.Position, Viewer, HasViewer);

                if (math.lengthsq(flatDir) > 0.0001f)
                    Drawer.Arrow(origin, math.normalize(flatDir) * (0.75f * math.saturate(weight)), color);
                else
                    Drawer.Circle(origin, new float3(0f, 0.1f, 0f), color);

                if (tier >= DebugTier.Mid)
                {
                    if (math.lengthsq(directionClip.PositionOffset) > 0.0001f)
                        Drawer.Arrow(origin + new float3(0f, 0.2f, 0f), directionClip.PositionOffset * 0.35f,
                            OffsetColor());

                    if (DrawClipLabels)
                    {
                        var shortLabel = default(FixedString32Bytes);
                        shortLabel.Append((FixedString32Bytes)"BT2D ");
                        shortLabel.Append(ReadKindName(directionClip.ReadKind));
                        Drawer.Text32(origin + new float3(0f, 0.2f, 0f), shortLabel, color, 12f);
                    }
                }

                if (tier == DebugTier.Close && DrawClipLabels)
                {
                    var label = default(FixedString128Bytes);
                    label.Append((FixedString32Bytes)"BT2D ");
                    label.Append(ReadKindName(directionClip.ReadKind));
                    label.Append((FixedString32Bytes)" w:");
                    label.Append((int)math.round(math.saturate(weight) * 100f));
                    label.Append((FixedString32Bytes)"% t:");
                    label.Append((int)math.round((float)localTime.Value * 100f));
                    label.Append((FixedString32Bytes)"%");

                    if (ClipLookup.TryGetComponent(clipEntity, out var clip) &&
                        TrackDataLookup.TryGetComponent(clip.Track, out var trackData))
                    {
                        label.Append((FixedString32Bytes)" L:");
                        label.Append(trackData.LayerIndex);

                        if (DrawMotions && MotionLookup.TryGetBuffer(clip.Track, out var motions))
                            DrawMotionMap(origin + new float3(0f, 0.35f, 0f), directionClip.Value, motions);
                    }

                    Drawer.Text128(origin + new float3(0f, 0.5f, 0f), label, color, 12f);
                }
            }

            private void DrawMotionMap(float3 center, float2 selectedValue,
                DynamicBuffer<BlendTree2DMotionData> motions)
            {
                const int MaxMotions = 24;
                const float Scale = 0.32f;

                var count = math.min(MaxMotions, motions.Length);
                Drawer.Circle(center, new float3(0f, Scale, 0f), MotionMapRingColor());

                for (var i = 0; i < count; i++)
                {
                    var motion = motions[i];
                    var pos2 = motion.BlendTree2DMotionElement.pos;
                    var point = center + new float3(pos2.x * Scale, 0f, pos2.y * Scale);

                    Drawer.Point(point, 0.04f, MotionPointColor());
                    Drawer.Line(center, point, MotionMapLineColor());
                }

                var selected = center + new float3(selectedValue.x * Scale, 0f, selectedValue.y * Scale);
                Drawer.Point(selected, 0.065f, BlendTreeColor());
            }

            private static FixedString32Bytes ReadKindName(BlendDirectionReadKind readKind)
            {
                return readKind switch
                {
                    BlendDirectionReadKind.ClipValue => "Clip",
                    BlendDirectionReadKind.PhysicsLinearVelocityNormalized => "PhysicsVel",
                    BlendDirectionReadKind.PlayerMoveInput => "MoveInput",
                    _ => "Unknown"
                };
            }
        }

        private static Color LayerColor(int layerIndex)
        {
            return (Math.Abs(layerIndex) % 5) switch
            {
                0 => new Color(0.25f, 0.75f, 1f, 1f),
                1 => new Color(0.5f, 1f, 0.35f, 1f),
                2 => new Color(1f, 0.65f, 0.25f, 1f),
                3 => new Color(0.9f, 0.45f, 1f, 1f),
                _ => new Color(1f, 1f, 0.35f, 1f)
            };
        }

        private static Color TextColor()
        {
            return new Color(1f, 1f, 1f, 1f);
        }

        private static Color RingColor()
        {
            return new Color(0.25f, 0.65f, 1f, 0.75f);
        }

        private static Color TargetWeightColor()
        {
            return new Color(1f, 1f, 1f, 1f);
        }

        private static Color FallbackColor()
        {
            return new Color(1f, 0.35f, 0.35f, 1f);
        }

        private static Color BlendTreeColor()
        {
            return new Color(0.25f, 1f, 0.8f, 1f);
        }

        private static Color OffsetColor()
        {
            return new Color(1f, 0.85f, 0.25f, 1f);
        }

        private static Color MotionPointColor()
        {
            return new Color(0.75f, 0.75f, 0.75f, 1f);
        }

        private static Color MotionMapLineColor()
        {
            return new Color(0.45f, 0.45f, 0.45f, 0.65f);
        }

        private static Color MotionMapRingColor()
        {
            return new Color(0.35f, 0.35f, 0.35f, 0.85f);
        }
    }
}
#endif