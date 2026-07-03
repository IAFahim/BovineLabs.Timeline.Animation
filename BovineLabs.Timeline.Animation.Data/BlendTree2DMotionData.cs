using BovineLabs.Timeline.Data;
using Rukhanka;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Properties;
using Hash128 = Unity.Entities.Hash128;

namespace BovineLabs.Timeline.Animation
{
    [InternalBufferCapacity(0)]
    public struct BlendTree2DMotionData : IBufferElementData
    {
        public Hash128 AnimationHash;
        public ScriptedAnimator.BlendTree2DMotionElement BlendTree2DMotionElement;
    }

    public struct BlendTree2DDirectionClipData : IAnimatedComponent<float2>
    {
        public BlendDirectionReadKind ReadKind;
        public ushort ReadLinkKey;
        [CreateProperty] public float2 Value { get; set; }

        // Interpret the blend direction relative to the main camera's ground projection (see CameraGroundBasis),
        // like AxisTransform's CameraRelative flag. PlayerMoveInput: the stick is lifted through the camera basis
        // into world then expressed in the character's facing. Velocity: the blend is expressed in the camera frame
        // instead of the character facing. No effect on ClipValue, or when there is no main camera.
        public bool CameraRelative;

        public float3 PositionOffset;
        public quaternion RotationOffset;
        public bool RemoveStartOffset;
        public bool ApplyFootIK;
    }

    public enum BlendDirectionReadKind : byte
    {
        ClipValue = 0,
        PhysicsLinearVelocityNormalized = 1,
        PlayerMoveInput = 2
    }

    public struct BlendAnimationTree2DTrackData : IComponentData
    {
        public MotionBlob.Type BlendTreeType;
        public int LayerIndex;

        public float3 TrackPositionOffset;
        public quaternion TrackRotationOffset;
        public bool ApplyAvatarMask;
        public Hash128 AvatarMaskHash;
    }
}