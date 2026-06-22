using System.Runtime.CompilerServices;
using Unity.Mathematics;

[assembly: InternalsVisibleTo("BovineLabs.Timeline.Animation")]

namespace BovineLabs.Timeline.Animation
{
    internal static class TransformConversion
    {
        internal static bool WorldToParentLocal(in float4x4 parentL2W, float3 worldPos, quaternion worldRot,
            out float3 localPos, out quaternion localRot)
        {
            if (math.abs(math.determinant(parentL2W)) <= 1e-8f)
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
            float epsilonDeterminant, out float3 localPos)
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
