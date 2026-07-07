using Rukhanka;
using Unity.Mathematics;

namespace BovineLabs.Timeline.Animation
{
    /// <summary>
    /// Pure math for the package-owned root-offset post-process (spike #27). The Rukhanka fork used to compose each
    /// clip's <c>positionOffset</c>/<c>rotationOffset</c> onto the root-motion delta bone INSIDE
    /// <c>ComputeBoneAnimationJob</c>; that work now happens outside the fork, on bone 0 of the animated pose, in
    /// <c>AnimationRootOffsetSystem</c>. This helper holds the two testable pieces: the per-frame weighted blend of
    /// the active clips' offsets (<see cref="RootOffsetAccumulator"/>) and the compose-onto-root operation
    /// (<see cref="ComposeOntoRoot"/>), which mirrors the fork's <c>BoneTransform.Multiply(offsetPose, bonePose)</c>.
    /// </summary>
    internal static class AnimationRootOffsetMath
    {
        // Below these magnitudes the resolved composite offset is treated as identity and the pose is left untouched,
        // guaranteeing the zero-offset no-op (the overwhelmingly common case: no content authors non-zero offsets).
        internal const float IdentityPosEpsilonSq = 1e-12f;
        internal const float IdentityRotEpsilon = 1e-6f;

        // TotalWeight below this means no clip contributed an offset this frame -> nothing to apply.
        internal const float WeightEpsilon = 1e-6f;

        /// <summary>True when a resolved offset is close enough to identity that composing it would be a no-op.</summary>
        internal static bool IsIdentityOffset(float3 pos, quaternion rot)
        {
            var isPos = math.lengthsq(pos) <= IdentityPosEpsilonSq;
            // A unit quaternion is identity iff |w| == 1 (w == 1 or the negated hemisphere w == -1).
            var isRot = 1f - math.abs(rot.value.w) <= IdentityRotEpsilon;
            return isPos && isRot;
        }

        /// <summary>
        /// Compose an offset (as a parent transform, scale 1) onto the root bone's local pose. Identical form to the
        /// old fork patch: <c>BoneTransform.Multiply(offsetPose, rootLocal)</c> — the offset is applied in the root
        /// bone's parent space (i.e. rig/entity space for bone 0).
        /// </summary>
        internal static BoneTransform ComposeOntoRoot(in BoneTransform rootLocal, float3 offsetPos, quaternion offsetRot)
        {
            var offsetPose = new BoneTransform
            {
                pos = offsetPos,
                rot = offsetRot,
                scale = new float3(1f, 1f, 1f),
            };

            return BoneTransform.Multiply(offsetPose, rootLocal);
        }
    }

    /// <summary>
    /// Accumulates a weight-normalized blend of the active clips' offsets so a crossfade blends its offsets exactly as
    /// it blends its poses. Position is a weighted average; rotation is a weighted, hemisphere-aligned nlerp (valid for
    /// the near-identity offsets these fields carry). A single active clip resolves to that clip's offset unchanged
    /// (parity with the fork's single-clip case). Clips with an identity offset still contribute their weight, so a
    /// half-weighted offset clip yields a half-magnitude composite exactly as the fork's per-clip blend did.
    /// </summary>
    internal struct RootOffsetAccumulator
    {
        public float TotalWeight;
        public float3 PosSum;
        public float4 RotSum;

        /// <summary>Add one clip's offset weighted by its (already layer-normalized) ATP weight.</summary>
        public void Add(float weight, float3 offsetPos, quaternion offsetRot)
        {
            if (weight <= 0f)
            {
                return;
            }

            TotalWeight += weight;
            PosSum += offsetPos * weight;

            // Hemisphere-align every quaternion to the identity hemisphere (w >= 0) before summing so opposite-sign
            // encodings of the same rotation do not cancel in the average.
            var q = offsetRot.value;
            if (q.w < 0f)
            {
                q = -q;
            }

            RotSum += q * weight;
        }

        /// <summary>
        /// Resolve the blended offset. Returns false when no clip contributed (nothing to apply). A zero rotation sum
        /// (all-identity) normalizes back to identity via <see cref="math.normalizesafe(quaternion)"/>.
        /// </summary>
        public bool TryResolve(out float3 pos, out quaternion rot)
        {
            if (TotalWeight <= AnimationRootOffsetMath.WeightEpsilon)
            {
                pos = float3.zero;
                rot = quaternion.identity;
                return false;
            }

            pos = PosSum / TotalWeight;
            rot = math.normalizesafe(new quaternion(RotSum), quaternion.identity);
            return true;
        }
    }
}
