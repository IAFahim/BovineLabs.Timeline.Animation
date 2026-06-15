using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace BovineLabs.Timeline.Animation
{
    public static class BoneWorld
    {
        private const int MaxDepth = 64;

        public static bool TryComputeWorldMatrix(
            Entity bone,
            in ComponentLookup<LocalTransform> localTransformLookup,
            in ComponentLookup<Parent> parentLookup,
            in ComponentLookup<PostTransformMatrix> postTransformMatrixLookup,
            out float4x4 worldMatrix)
        {
            worldMatrix = float4x4.identity;

            if (!localTransformLookup.TryGetComponent(bone, out var localTransform))
                return false;

            var current = bone;
            var matrix = localTransform.ToMatrix();
            if (postTransformMatrixLookup.TryGetComponent(current, out var postTransform))
                matrix = math.mul(matrix, postTransform.Value);

            for (var depth = 0; depth < MaxDepth; depth++)
            {
                if (!parentLookup.TryGetComponent(current, out var parent))
                    break;

                current = parent.Value;
                if (!localTransformLookup.TryGetComponent(current, out var parentTransform))
                    return false;

                var parentMatrix = parentTransform.ToMatrix();
                if (postTransformMatrixLookup.TryGetComponent(current, out var parentPostTransform))
                    parentMatrix = math.mul(parentMatrix, parentPostTransform.Value);

                matrix = math.mul(parentMatrix, matrix);
            }

            worldMatrix = matrix;
            return true;
        }
    }
}
