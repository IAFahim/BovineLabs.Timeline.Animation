using NUnit.Framework;
using Rukhanka;
using Unity.Mathematics;

namespace BovineLabs.Timeline.Animation.Tests
{
    /// <summary>
    /// Locks the semantics the package-owned root-offset post-process (spike #27) must preserve when offset application
    /// moved out of the Rukhanka fork: the compose-onto-root formula matches the fork's per-clip
    /// <c>BoneTransform.Multiply(offsetPose, bonePose)</c>, crossfading clips blend their offsets by weight, and a
    /// zero/identity offset is an exact no-op.
    /// </summary>
    [TestFixture]
    public class AnimationRootOffsetMathTests
    {
        private const float Eps = 1e-5f;

        private static BoneTransform Root(float3 pos, quaternion rot)
        {
            return new BoneTransform { pos = pos, rot = rot, scale = new float3(1f, 1f, 1f) };
        }

        private static void AssertQuatEqual(quaternion expected, quaternion actual, float eps, string msg = "")
        {
            // Quaternions q and -q are the same rotation; compare on the aligned hemisphere.
            var dot = math.dot(expected.value, actual.value);
            if (dot < 0f)
            {
                actual.value = -actual.value;
            }

            Assert.AreEqual(expected.value.x, actual.value.x, eps, msg + " x");
            Assert.AreEqual(expected.value.y, actual.value.y, eps, msg + " y");
            Assert.AreEqual(expected.value.z, actual.value.z, eps, msg + " z");
            Assert.AreEqual(expected.value.w, actual.value.w, eps, msg + " w");
        }

        // ---- IsIdentityOffset ---------------------------------------------------------------------------------------

        [Test]
        public void IsIdentityOffset_ZeroPosIdentityRot_True()
        {
            Assert.IsTrue(AnimationRootOffsetMath.IsIdentityOffset(float3.zero, quaternion.identity));
        }

        [Test]
        public void IsIdentityOffset_NegatedHemisphereIdentity_True()
        {
            // -identity is the same rotation; |w| == 1 must still read as identity.
            var negIdentity = new quaternion(0f, 0f, 0f, -1f);
            Assert.IsTrue(AnimationRootOffsetMath.IsIdentityOffset(float3.zero, negIdentity));
        }

        [Test]
        public void IsIdentityOffset_NonZeroPos_False()
        {
            Assert.IsFalse(AnimationRootOffsetMath.IsIdentityOffset(new float3(0f, 0.5f, 0f), quaternion.identity));
        }

        [Test]
        public void IsIdentityOffset_NonIdentityRot_False()
        {
            Assert.IsFalse(AnimationRootOffsetMath.IsIdentityOffset(float3.zero, quaternion.AxisAngle(math.up(), 0.3f)));
        }

        // ---- ComposeOntoRoot (fork parity) --------------------------------------------------------------------------

        [Test]
        public void ComposeOntoRoot_IdentityOffset_ReturnsRootUnchanged()
        {
            var root = Root(new float3(1f, 2f, 3f), quaternion.AxisAngle(math.up(), 0.7f));
            var result = AnimationRootOffsetMath.ComposeOntoRoot(root, float3.zero, quaternion.identity);

            Assert.AreEqual(root.pos.x, result.pos.x, Eps);
            Assert.AreEqual(root.pos.y, result.pos.y, Eps);
            Assert.AreEqual(root.pos.z, result.pos.z, Eps);
            AssertQuatEqual(root.rot, result.rot, Eps, "rot");
            Assert.AreEqual(1f, result.scale.x, Eps);
        }

        [Test]
        public void ComposeOntoRoot_PureTranslation_AddsOffsetToRootPosition()
        {
            var root = Root(new float3(1f, 0f, 0f), quaternion.identity);
            var result = AnimationRootOffsetMath.ComposeOntoRoot(root, new float3(0f, 2f, 0f), quaternion.identity);

            Assert.AreEqual(1f, result.pos.x, Eps);
            Assert.AreEqual(2f, result.pos.y, Eps);
            Assert.AreEqual(0f, result.pos.z, Eps);
            AssertQuatEqual(quaternion.identity, result.rot, Eps, "rot");
        }

        [Test]
        public void ComposeOntoRoot_RotationOffset_RotatesRootTranslationAndOrientation()
        {
            // Offset = 90 deg about +Y applied as a parent transform. Root at (1,0,0) with identity orientation.
            var offsetRot = quaternion.AxisAngle(math.up(), math.radians(90f));
            var root = Root(new float3(1f, 0f, 0f), quaternion.identity);

            var result = AnimationRootOffsetMath.ComposeOntoRoot(root, float3.zero, offsetRot);

            // pos = offsetRot * (1,0,0); rot = offsetRot * identity.
            var expectedPos = math.rotate(offsetRot, new float3(1f, 0f, 0f));
            Assert.AreEqual(expectedPos.x, result.pos.x, Eps);
            Assert.AreEqual(expectedPos.y, result.pos.y, Eps);
            Assert.AreEqual(expectedPos.z, result.pos.z, Eps);
            AssertQuatEqual(offsetRot, result.rot, Eps, "rot");
        }

        [Test]
        public void ComposeOntoRoot_MatchesForkMultiplyFormula()
        {
            // Parity lock: the outside-fork compose must equal the fork's Multiply(offsetPose, bonePose).
            var root = Root(new float3(0.3f, -0.4f, 1.2f), quaternion.AxisAngle(math.normalize(new float3(1f, 2f, 3f)), 0.9f));
            var offPos = new float3(0.1f, 0.2f, -0.3f);
            var offRot = quaternion.AxisAngle(math.up(), 0.5f);

            var offsetPose = new BoneTransform { pos = offPos, rot = offRot, scale = new float3(1f, 1f, 1f) };
            var expected = BoneTransform.Multiply(offsetPose, root);

            var actual = AnimationRootOffsetMath.ComposeOntoRoot(root, offPos, offRot);

            Assert.AreEqual(expected.pos.x, actual.pos.x, Eps);
            Assert.AreEqual(expected.pos.y, actual.pos.y, Eps);
            Assert.AreEqual(expected.pos.z, actual.pos.z, Eps);
            AssertQuatEqual(expected.rot, actual.rot, Eps, "rot");
        }

        // ---- RootOffsetAccumulator (weighting) ----------------------------------------------------------------------

        [Test]
        public void Accumulator_NoClips_ResolvesFalse()
        {
            var acc = default(RootOffsetAccumulator);
            Assert.IsFalse(acc.TryResolve(out _, out _));
        }

        [Test]
        public void Accumulator_ZeroWeightClip_Ignored()
        {
            var acc = default(RootOffsetAccumulator);
            acc.Add(0f, new float3(5f, 5f, 5f), quaternion.AxisAngle(math.up(), 1f));
            Assert.IsFalse(acc.TryResolve(out _, out _));
        }

        [Test]
        public void Accumulator_SingleClip_ResolvesToThatOffset()
        {
            var offPos = new float3(0f, 0f, 2f);
            var offRot = quaternion.AxisAngle(math.right(), 0.4f);

            var acc = default(RootOffsetAccumulator);
            acc.Add(1f, offPos, offRot);

            Assert.IsTrue(acc.TryResolve(out var pos, out var rot));
            Assert.AreEqual(offPos.x, pos.x, Eps);
            Assert.AreEqual(offPos.y, pos.y, Eps);
            Assert.AreEqual(offPos.z, pos.z, Eps);
            AssertQuatEqual(offRot, rot, Eps, "rot");
        }

        [Test]
        public void Accumulator_SingleClipPartialWeight_StillResolvesFullOffset()
        {
            // Weight normalization: a lone clip at weight 0.5 still yields its full offset (0.5*O / 0.5 = O).
            var offPos = new float3(4f, 0f, 0f);
            var acc = default(RootOffsetAccumulator);
            acc.Add(0.5f, offPos, quaternion.identity);

            Assert.IsTrue(acc.TryResolve(out var pos, out _));
            Assert.AreEqual(4f, pos.x, Eps);
        }

        [Test]
        public void Accumulator_IdentityClipDilutesOffset_Crossfade5050()
        {
            // Crossfade: clip A offset (2,0,0) at 0.5, clip B identity at 0.5 -> half-magnitude composite (1,0,0),
            // exactly as the fork's per-clip weighted blend diluted an offset against a non-offset clip.
            var acc = default(RootOffsetAccumulator);
            acc.Add(0.5f, new float3(2f, 0f, 0f), quaternion.identity);
            acc.Add(0.5f, float3.zero, quaternion.identity);

            Assert.IsTrue(acc.TryResolve(out var pos, out var rot));
            Assert.AreEqual(1f, pos.x, Eps);
            Assert.AreEqual(0f, pos.y, Eps);
            Assert.AreEqual(0f, pos.z, Eps);
            AssertQuatEqual(quaternion.identity, rot, Eps, "rot");
        }

        [Test]
        public void Accumulator_TwoOffsetsWeighted_PositionInterpolates()
        {
            var a = new float3(0f, 4f, 0f);
            var b = new float3(0f, 0f, 8f);

            var acc = default(RootOffsetAccumulator);
            acc.Add(0.75f, a, quaternion.identity);
            acc.Add(0.25f, b, quaternion.identity);

            Assert.IsTrue(acc.TryResolve(out var pos, out _));
            // 0.75*a + 0.25*b normalized by total weight 1.0.
            Assert.AreEqual(0f, pos.x, Eps);
            Assert.AreEqual(3f, pos.y, Eps); // 0.75 * 4
            Assert.AreEqual(2f, pos.z, Eps); // 0.25 * 8
        }

        [Test]
        public void Accumulator_RotationCrossfade_BlendsHalfway()
        {
            // Identity vs a small rotation about X at 50/50 -> ~half the angle (nlerp ~= slerp for small angles).
            const float angle = 0.2f;
            var acc = default(RootOffsetAccumulator);
            acc.Add(0.5f, float3.zero, quaternion.identity);
            acc.Add(0.5f, float3.zero, quaternion.AxisAngle(math.right(), angle));

            Assert.IsTrue(acc.TryResolve(out _, out var rot));

            var expected = quaternion.AxisAngle(math.right(), angle * 0.5f);
            AssertQuatEqual(expected, rot, 1e-3f, "half-angle blend");
        }

        [Test]
        public void Accumulator_OppositeHemisphereEncodings_DoNotCancel()
        {
            // The same rotation encoded as q and -q must average to that rotation, not to zero.
            var q = quaternion.AxisAngle(math.up(), 0.6f);
            var negQ = new quaternion(-q.value);

            var acc = default(RootOffsetAccumulator);
            acc.Add(0.5f, float3.zero, q);
            acc.Add(0.5f, float3.zero, negQ);

            Assert.IsTrue(acc.TryResolve(out _, out var rot));
            AssertQuatEqual(q, rot, Eps, "hemisphere-aligned average");
        }

        // ---- End-to-end: resolve then compose -----------------------------------------------------------------------

        [Test]
        public void ResolveThenCompose_CrossfadeOffsets_ShiftsRootByBlendedOffset()
        {
            var root = Root(new float3(1f, 1f, 1f), quaternion.identity);

            var acc = default(RootOffsetAccumulator);
            acc.Add(0.5f, new float3(2f, 0f, 0f), quaternion.identity);
            acc.Add(0.5f, new float3(0f, 2f, 0f), quaternion.identity);

            Assert.IsTrue(acc.TryResolve(out var pos, out var rot));
            var result = AnimationRootOffsetMath.ComposeOntoRoot(root, pos, rot);

            // Blended offset (1,1,0) added to root (1,1,1).
            Assert.AreEqual(2f, result.pos.x, Eps);
            Assert.AreEqual(2f, result.pos.y, Eps);
            Assert.AreEqual(1f, result.pos.z, Eps);
        }
    }
}
