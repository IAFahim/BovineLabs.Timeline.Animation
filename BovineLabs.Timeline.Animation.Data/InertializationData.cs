using Rukhanka;
using Unity.Entities;
using Unity.Mathematics;

namespace BovineLabs.Timeline.Animation
{
    /// <summary>
    /// Per-rig inertialization state. Drives the quintic offset decay that absorbs the pose discontinuity
    /// at a dominant-clip change (David Bollo, "Inertialization", GDC 2018). Added at bake only when the
    /// authoring <c>inertializationDuration</c> is &gt; 0; absent = exactly the current (non-inertialized) behavior.
    /// </summary>
    public struct InertializationState : IComponentData
    {
        /// <summary>Seconds since the last capture; the quintic's <c>t</c>.</summary>
        public float elapsed;

        /// <summary>The active decay window <c>T</c> (configured duration). Per-channel overshoot clamps are applied at evaluation.</summary>
        public float duration;

        /// <summary>0 = idle (no offset applied), 1 = decaying.</summary>
        public byte active;

        /// <summary>motionId of last frame's dominant (highest-weight) entry; the transition detector.</summary>
        public uint lastDominant;

        /// <summary>Normalized [0,1) clip time of last frame's dominant entry; used to detect same-clip phase jumps (raw loop seams).</summary>
        public float lastDominantTime;

        /// <summary>Normalized [0,1) clip time of the dominant entry two frames ago; gives the expected per-frame phase step.</summary>
        public float prevDominantTime;

        /// <summary>0 until the per-bone history/buffer has been initialized for this rig (first frame seeds it).</summary>
        public byte initialized;
    }

    /// <summary>
    /// Per-bone persistent inertialization state. One element per rig bone (indexed by rig bone index, NOT the
    /// absolute <c>bonePoseOffset + i</c> packing). Holds the captured offset for the active decay plus a 3-frame
    /// history of the actually-displayed local pose so a capture always has a valid velocity AND acceleration (the
    /// second difference the full quintic needs). The bone buffers are not double-buffered, so we keep our own history.
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct InertializationBoneState : IBufferElementData
    {
        // Position channel: captured offset (x0), velocity (v0) and acceleration (a0) for the full Bollo quintic.
        public float3 posOffset0;
        public float3 posVel0;
        public float3 posAcc0;

        // Rotation channel: the offset reduced to a scalar angle about a fixed axis, with velocity and acceleration.
        public float3 rotAxis;
        public float rotAngle0;
        public float rotVel0;
        public float rotAcc0;

        // 3-frame history of the actually-displayed local pose (a0 needs a second difference => three samples).
        public BoneTransform prevDisplayed;
        public BoneTransform prevPrevDisplayed;
        public BoneTransform prevPrevPrevDisplayed;
    }
}
