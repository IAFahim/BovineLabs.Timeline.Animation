using Unity.Burst;
using Unity.Mathematics;

namespace BovineLabs.Timeline.Animation
{
    /// <summary>
    /// The main camera's ground-plane (XZ) orientation, published ONCE per frame by <see cref="CameraGroundBasisSystem"/>
    /// and read by every camera-relative blend clip. Computing the camera basis a single time (instead of per clip /
    /// per character) is the whole point: N characters blending camera-relative share one read.
    /// ponytail: SharedStatic is process-global — a single value shared by ALL worlds. Fine for local/single-player.
    /// The publisher only runs on Local/Client worlds and consumers gate on <see cref="Valid"/>, so a headless server
    /// world simply never sees a valid basis and falls back to non-camera behaviour. Don't rely on it in split worlds.
    /// </summary>
    public struct CameraGroundBasis
    {
        /// <summary>Camera forward projected onto the ground plane, normalized.</summary>
        public float3 Forward;

        /// <summary>Camera right on the ground plane, normalized (orthogonal to <see cref="Forward"/>).</summary>
        public float3 Right;

        /// <summary>False when there is no main camera; consumers fall back to their non-camera path.</summary>
        public bool Valid;

        private struct Key
        {
        }

        private static readonly SharedStatic<CameraGroundBasis> Shared =
            SharedStatic<CameraGroundBasis>.GetOrCreate<CameraGroundBasis, Key>();

        public static ref CameraGroundBasis Data => ref Shared.Data;
    }
}
