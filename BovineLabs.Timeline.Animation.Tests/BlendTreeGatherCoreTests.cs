using System;
using NUnit.Framework;
using Unity.Entities;
using Unity.Mathematics;

namespace BovineLabs.Timeline.Animation.Tests
{
    public class BlendTreeGatherCoreTests
    {
        private const float FrameTime = 1f / 60f;

        [Test]
        public void AdvancePhase_FirstSight_SeedsFromAbsoluteTime()
        {
            var state = default(BlendTreeGatherCore.PhaseClockState);

            var normalized = BlendTreeGatherCore.AdvancePhase(ref state, 0.5f, 2f, 1f, FrameTime, false);

            Assert.IsTrue(state.Initialized);
            Assert.AreEqual(0.25f, state.AccumulatedTime, 1e-6f);
            Assert.AreEqual(0.5f, state.PreviousAbsoluteTime, 1e-6f);
            Assert.AreEqual(0.25f, normalized, 1e-6f);
        }

        [Test]
        public void AdvancePhase_ForwardStep_AccruesPlausibleDelta()
        {
            var state = new BlendTreeGatherCore.PhaseClockState
            {
                Initialized = true,
                AccumulatedTime = 0.25f,
                PreviousAbsoluteTime = 0.5f,
            };

            var normalized = BlendTreeGatherCore.AdvancePhase(ref state, 0.5f + FrameTime, 2f, 1f, FrameTime, false);

            Assert.AreEqual(0.25f + (FrameTime / 2f), state.AccumulatedTime, 1e-6f);
            Assert.AreEqual(0.5f + FrameTime, state.PreviousAbsoluteTime, 1e-6f);
            Assert.AreEqual(math.frac(0.25f + (FrameTime / 2f)), normalized, 1e-6f);
        }

        [Test]
        public void AdvancePhase_Scrubbing_FollowsAbsoluteTimeDirectly()
        {
            var state = new BlendTreeGatherCore.PhaseClockState
            {
                Initialized = true,
                AccumulatedTime = 0.25f,
                PreviousAbsoluteTime = 0.5f,
            };

            // A 0.4s jump exceeds the plausible frame step; while scrubbing PlayingDelta is bypassed so the raw
            // absolute delta drives the phase (0.4 / 2 = 0.2 added).
            BlendTreeGatherCore.AdvancePhase(ref state, 0.9f, 2f, 1f, FrameTime, true);

            Assert.AreEqual(0.45f, state.AccumulatedTime, 1e-6f);
            Assert.AreEqual(0.9f, state.PreviousAbsoluteTime, 1e-6f);
        }

        [Test]
        public void AdvancePhase_ReverseStep_MovesPhaseBackward()
        {
            var state = new BlendTreeGatherCore.PhaseClockState
            {
                Initialized = true,
                AccumulatedTime = 0.5f,
                PreviousAbsoluteTime = 0.5f,
            };

            BlendTreeGatherCore.AdvancePhase(ref state, 0.5f - FrameTime, 2f, 1f, FrameTime, false);

            Assert.Less(state.AccumulatedTime, 0.5f);
            Assert.AreEqual(0.5f - (FrameTime / 2f), state.AccumulatedTime, 1e-6f);
        }

        [Test]
        public void AdvancePhase_ReverseIntoNegative_WrapsIntoUnitRange()
        {
            var state = new BlendTreeGatherCore.PhaseClockState
            {
                Initialized = true,
                AccumulatedTime = 0.05f,
                PreviousAbsoluteTime = 1f,
            };

            // delta = 0.9 - 1.0 = -0.1 (honored); accumulated 0.05 - 0.1 = -0.05 → frac wraps to 0.95.
            var normalized = BlendTreeGatherCore.AdvancePhase(ref state, 0.9f, 1f, 1f, FrameTime, false);

            Assert.AreEqual(-0.05f, state.AccumulatedTime, 1e-6f);
            Assert.GreaterOrEqual(normalized, 0f);
            Assert.Less(normalized, 1f);
            Assert.AreEqual(0.95f, normalized, 1e-5f);
        }

        [Test]
        public void ContainsEntity_MatchesMembership()
        {
            var a = new Entity { Index = 1, Version = 1 };
            var b = new Entity { Index = 2, Version = 1 };
            var c = new Entity { Index = 3, Version = 1 };

            ReadOnlySpan<Entity> active = new[] { a, c };

            Assert.IsTrue(BlendTreeGatherCore.ContainsEntity(active, a));
            Assert.IsTrue(BlendTreeGatherCore.ContainsEntity(active, c));
            Assert.IsFalse(BlendTreeGatherCore.ContainsEntity(active, b));
        }

        [Test]
        public void OrphanRemoval_KeepsActiveTracks_DropsStaleOnes()
        {
            var a = new Entity { Index = 1, Version = 1 };
            var b = new Entity { Index = 2, Version = 1 };
            var c = new Entity { Index = 3, Version = 1 };
            var d = new Entity { Index = 4, Version = 1 };

            ReadOnlySpan<Entity> active = new[] { a, c };

            // Mirror CleanupOrphanPlaybackStates: swap-back remove any state track not in the active set.
            var stateTracks = new System.Collections.Generic.List<Entity> { a, b, c, d };
            for (var i = stateTracks.Count - 1; i >= 0; i--)
            {
                if (!BlendTreeGatherCore.ContainsEntity(active, stateTracks[i]))
                {
                    stateTracks[i] = stateTracks[stateTracks.Count - 1];
                    stateTracks.RemoveAt(stateTracks.Count - 1);
                }
            }

            Assert.AreEqual(2, stateTracks.Count);
            Assert.Contains(a, stateTracks);
            Assert.Contains(c, stateTracks);
            Assert.IsFalse(stateTracks.Contains(b));
            Assert.IsFalse(stateTracks.Contains(d));
        }
    }
}
