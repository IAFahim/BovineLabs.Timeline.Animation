#if !BL_DISABLE_OBJECT_DEFINITION
using BovineLabs.Core.Collections;
using BovineLabs.Nerve.ObjectManagement;
using NUnit.Framework;
using Rukhanka.Toolbox;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace BovineLabs.Timeline.Animation.Tests
{
    public class WeaponGripRegistryTests
    {
        private static readonly uint OneHand = "OneHand".CalculateHash32();
        private static readonly uint TwoHand = "TwoHand".CalculateHash32();
        private static readonly uint RightHand = "RightHand".CalculateHash32();
        private static readonly uint LeftHand = "LeftHand".CalculateHash32();

        [Test]
        public void RegistryResolvesWeaponGripAndDefault()
        {
            var blob = BuildRegistry(weaponId: 7, defaultGrip: 1);
            try
            {
                Assert.IsTrue(blob.Value.Weapons.TryGetValue(new ObjectId(7), out var found));
                ref var grips = ref found.Ref;

                Assert.AreEqual(2, grips.Grips.Length);
                Assert.AreEqual(1, grips.DefaultGrip);

                Assert.AreEqual(OneHand, grips.Grips[0].Key);
                Assert.AreEqual(RightHand, grips.Grips[0].BoneHash);
                Assert.IsTrue(math.all(math.abs(grips.Grips[0].Position - new float3(0.1f, 0.2f, 0.3f)) < 1e-6f));

                Assert.AreEqual(TwoHand, grips.Grips[1].Key);
                Assert.AreEqual(LeftHand, grips.Grips[1].BoneHash);
            }
            finally
            {
                blob.Dispose();
            }
        }

        [Test]
        public void UnknownWeaponIsNotFound()
        {
            var blob = BuildRegistry(weaponId: 7, defaultGrip: 0);
            try
            {
                Assert.IsFalse(blob.Value.Weapons.TryGetValue(new ObjectId(99), out _));
            }
            finally
            {
                blob.Dispose();
            }
        }

        internal static BlobAssetReference<WeaponGripRegistryBlob> BuildRegistry(int weaponId, int defaultGrip)
        {
            var builder = new BlobBuilder(Allocator.Temp);
            ref var root = ref builder.ConstructRoot<WeaponGripRegistryBlob>();
            var map = builder.AllocateHashMap(ref root.Weapons, 1);

            ref var grips = ref map.AddUnique(new ObjectId(weaponId));
            grips.DefaultGrip = defaultGrip;

            var array = builder.Allocate(ref grips.Grips, 2);
            array[0] = new Grip
            {
                Key = OneHand,
                BoneHash = RightHand,
                Position = new float3(0.1f, 0.2f, 0.3f),
                Rotation = quaternion.RotateY(0.5f)
            };
            array[1] = new Grip
            {
                Key = TwoHand,
                BoneHash = LeftHand,
                Position = new float3(-0.1f, 0f, 0.05f),
                Rotation = quaternion.identity
            };

            var blob = builder.CreateBlobAssetReference<WeaponGripRegistryBlob>(Allocator.Persistent);
            builder.Dispose();
            return blob;
        }
    }
}
#endif
