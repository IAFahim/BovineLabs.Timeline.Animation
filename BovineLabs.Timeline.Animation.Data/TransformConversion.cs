using System.Runtime.CompilerServices;
using Unity.Mathematics;

[assembly: InternalsVisibleTo("BovineLabs.Timeline.Animation")]

namespace BovineLabs.Timeline.Animation
{
    internal static class TransformConversion
    {
        /// <summary>Shared near-singular guard: parent matrices whose |determinant| is at or below this are treated as
        /// non-invertible (world pose passed through). #35: unify the epsilon across both conversions.</summary>
        internal const float DeterminantEpsilon = 1e-8f;

        internal static bool WorldToParentLocal(in float4x4 parentL2W, float3 worldPos, quaternion worldRot,
            out float3 localPos, out quaternion localRot)
        {
            if (math.abs(math.determinant(parentL2W)) <= DeterminantEpsilon)
            {
                localPos = worldPos;
                localRot = worldRot;
                return false;
            }

            var parentRotation = new quaternion(math.orthonormalize(new float3x3(parentL2W)));
            localPos = math.transform(math.inverse(parentL2W), worldPos);
            localRot = math.mul(math.inverse(parentRotation), worldRot);
            return true;
        }

        internal static bool WorldPositionToParentLocal(in float4x4 parentL2W, float3 worldPos,
            out float3 localPos, float epsilonDeterminant = DeterminantEpsilon)
        {
            if (math.abs(math.determinant(parentL2W)) <= epsilonDeterminant)
            {
                localPos = worldPos;
                return false;
            }

            localPos = math.transform(math.inverse(parentL2W), worldPos);
            return true;
        }
    }
}
