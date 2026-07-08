#if !BL_DISABLE_OBJECT_DEFINITION
using System;
using System.Collections.Generic;
using BovineLabs.Core.Authoring.Settings;
using BovineLabs.Core.Collections;
using BovineLabs.Nerve.ObjectManagement;
using BovineLabs.Core.Settings;
using Rukhanka.Toolbox;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace BovineLabs.Timeline.Animation.Authoring
{
    /// <summary>
    /// Bakes every assigned <see cref="WeaponGripPresetObject" /> into the <see cref="WeaponGripRegistry" />
    /// singleton blob (ObjectDefinition id → grips, keyed by grip name hash).
    /// </summary>
    [SettingsGroup("Animation")]
    public class WeaponGripSettings : SettingsBase
    {
        [Tooltip("All weapon grip presets, one per weapon.")]
        [SerializeField]
        private WeaponGripPresetObject[] presets = Array.Empty<WeaponGripPresetObject>();

        public override void Bake(Baker<SettingsAuthoring> baker)
        {
            var valid = new List<WeaponGripPresetObject>();
            var seen = new HashSet<ObjectId>();

            foreach (var preset in this.presets)
            {
                if (preset == null)
                    continue;

                baker.DependsOn(preset);

                if (preset.weapon == null)
                {
                    Debug.LogWarning($"{nameof(WeaponGripSettings)}: preset '{preset.name}' has no weapon assigned; skipped.", preset);
                    continue;
                }

                if (preset.grips == null || preset.grips.Length == 0)
                {
                    Debug.LogWarning($"{nameof(WeaponGripSettings)}: preset '{preset.name}' has no grips; skipped.", preset);
                    continue;
                }

                // A zero ID is an unassigned auto-id: it keys nothing in the registry and would collide in the blob
                // hash map (AddUnique) with any other zero-ID weapon. Skip it and tell the designer to assign one.
                if (preset.weapon.ID == 0)
                {
                    Debug.LogWarning($"{nameof(WeaponGripSettings)}: preset '{preset.name}' weapon '{preset.weapon.name}' has ID 0 (unassigned ObjectDefinition id); skipped. Assign the weapon an ObjectDefinition id.", preset);
                    continue;
                }

                if (!seen.Add(preset.weapon))
                {
                    Debug.LogWarning($"{nameof(WeaponGripSettings)}: multiple presets share weapon ObjectDefinition id {preset.weapon.ID} ('{preset.weapon.name}'); '{preset.name}' skipped. Two ObjectDefinitions with the same id is the duplicate-id trap.", preset);
                    continue;
                }

                valid.Add(preset);
            }

            var builder = new BlobBuilder(Allocator.Temp);
            ref var root = ref builder.ConstructRoot<WeaponGripRegistryBlob>();
            var map = builder.AllocateHashMap(ref root.Weapons, math.max(1, valid.Count));

            foreach (var preset in valid)
            {
                ref var grips = ref map.AddUnique(preset.weapon);
                grips.DefaultGrip = math.clamp(preset.defaultGrip, 0, preset.grips.Length - 1);

                if (grips.DefaultGrip != preset.defaultGrip)
                    Debug.LogWarning($"{nameof(WeaponGripSettings)}: preset '{preset.name}' defaultGrip {preset.defaultGrip} is out of range; clamped.", preset);

                var array = builder.Allocate(ref grips.Grips, preset.grips.Length);
                for (var i = 0; i < preset.grips.Length; i++)
                {
                    var grip = preset.grips[i];
                    if (string.IsNullOrWhiteSpace(grip.name))
                        Debug.LogWarning($"{nameof(WeaponGripSettings)}: preset '{preset.name}' grip {i} has no name; clips cannot reference it.", preset);
                    if (string.IsNullOrWhiteSpace(grip.bone))
                        Debug.LogWarning($"{nameof(WeaponGripSettings)}: preset '{preset.name}' grip '{grip.name}' has no bone; it will never resolve.", preset);

                    array[i] = new Grip
                    {
                        Key = string.IsNullOrWhiteSpace(grip.name) ? 0u : grip.name.CalculateHash32(),
                        BoneHash = string.IsNullOrWhiteSpace(grip.bone) ? 0u : grip.bone.CalculateHash32(),
                        Position = grip.localPosition,
                        Rotation = quaternion.Euler(math.radians(grip.localRotationEuler))
                    };
                }
            }

            var blob = builder.CreateBlobAssetReference<WeaponGripRegistryBlob>(Allocator.Persistent);
            builder.Dispose();

            baker.AddBlobAsset(ref blob, out _);
            var entity = baker.GetEntity(TransformUsageFlags.None);
            baker.AddComponent(entity, new WeaponGripRegistry { Value = blob });
        }
    }
}
#endif
