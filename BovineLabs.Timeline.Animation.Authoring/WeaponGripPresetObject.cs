#if !BL_DISABLE_OBJECT_DEFINITION
using System;
using BovineLabs.Nerve.Authoring.ObjectManagement;
using UnityEngine;

namespace BovineLabs.Timeline.Animation.Authoring
{
    /// <summary>
    /// Designer-authored grip poses for one weapon. One asset per weapon, next to its ObjectDefinition.
    /// Collected by <see cref="WeaponGripSettings" /> and baked into the WeaponGripRegistry blob.
    /// </summary>
    [CreateAssetMenu(menuName = "BovineLabs/Timeline/Weapon Grip Preset", fileName = "WeaponGripPreset")]
    public class WeaponGripPresetObject : ScriptableObject
    {
        [Tooltip("The weapon this preset belongs to. Its ObjectDefinition id is the registry key.")]
        public ObjectDefinition weapon;

        [Tooltip("Index into grips used at equip and when a clip references a missing grip name.")]
        public int defaultGrip;

        public GripAuthoring[] grips = Array.Empty<GripAuthoring>();

        [Serializable]
        public class GripAuthoring
        {
            [Tooltip("Designer label; hashed (Rukhanka CalculateHash32) as the runtime grip key.")]
            public string name;

            [Tooltip("Rukhanka bone name the weapon anchors to, e.g. 'mixamorig:RightHand'. Hashed at bake; resolves on any rig.")]
            public string bone;

            [Tooltip("Local position offset from the bone, in the bone's space.")]
            public Vector3 localPosition;

            [Tooltip("Local rotation offset from the bone, in euler degrees.")]
            public Vector3 localRotationEuler;
        }
    }
}
#endif
