using BovineLabs.Bridge.Data.Camera;
using BovineLabs.Timeline.Data;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace BovineLabs.Timeline.Animation
{
    /// <summary>
    /// Publishes the main camera's ground-plane basis into the <see cref="CameraGroundBasis"/> SharedStatic once per
    /// frame so every camera-relative blend clip reads it instead of re-querying the camera. Runs before the blend
    /// systems that consume it. Local/Client only — a server world has no camera and must not stomp the shared value.
    /// </summary>
    [UpdateInGroup(typeof(TimelineComponentAnimationGroup))]
    [UpdateBefore(typeof(TimelineAnimationBlendTree2DTrackSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.LocalSimulation | WorldSystemFilterFlags.ClientSimulation)]
    public partial struct CameraGroundBasisSystem : ISystem
    {
        private EntityQuery _cameraQuery;
        private ComponentLookup<LocalToWorld> _ltws;
        private ComponentLookup<LocalTransform> _transforms;
        private ComponentLookup<Parent> _parents;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            _cameraQuery = SystemAPI.QueryBuilder().WithAll<CameraMain>().Build();
            _ltws = state.GetComponentLookup<LocalToWorld>(true);
            _transforms = state.GetComponentLookup<LocalTransform>(true);
            _parents = state.GetComponentLookup<Parent>(true);
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            _ltws.Update(ref state);
            _transforms.Update(ref state);
            _parents.Update(ref state);

            var rotation = quaternion.identity;
            var haveCamera = false;

            if (!_cameraQuery.IsEmpty)
            {
                // Split-screen coop registers multiple CameraMain; take the first (matches AxisTransformSystem /
                // InputCommonSystem) rather than GetSingletonEntity, which throws on count != 1.
                using var cameras = _cameraQuery.ToEntityArray(Allocator.Temp);
                var camera = cameras[0];

                if (_ltws.TryGetComponent(camera, out var ltw))
                {
                    rotation = ltw.Rotation;
                    haveCamera = true;
                }
                else if (_transforms.TryGetComponent(camera, out var lt) && !_parents.HasComponent(camera))
                {
                    rotation = lt.Rotation;
                    haveCamera = true;
                }
            }

            ref var basis = ref CameraGroundBasis.Data;

            if (!haveCamera)
            {
                basis.Valid = false;
                return;
            }

            var up = math.up();
            var forward = math.mul(rotation, math.forward());
            forward -= math.dot(forward, up) * up;

            if (math.lengthsq(forward) < 1e-6f)
            {
                // Camera pointing straight down/up: no forward heading on the ground, derive it from the right vector.
                var right = math.mul(rotation, math.right());
                right -= math.dot(right, up) * up;
                if (math.lengthsq(right) < 1e-6f)
                {
                    basis.Valid = false;
                    return;
                }

                right = math.normalize(right);
                basis.Right = right;
                basis.Forward = math.normalize(math.cross(right, up));
                basis.Valid = true;
                return;
            }

            forward = math.normalize(forward);
            basis.Forward = forward;
            basis.Right = math.normalize(math.cross(up, forward));
            basis.Valid = true;
        }
    }
}
