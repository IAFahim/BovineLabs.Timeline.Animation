# Weapon Timeline System — Design (v1, 2026-07)

One weapon mechanism to replace two half-finished ones. Grip poses become **data**
(designer-authored presets in a blob registry keyed by ObjectDefinition id) instead of
per-clip hand-typed offsets; equip/re-attach/drop/pickup become **edge clips**; all pose
math reuses the existing, correct `WeaponAnchorBlendSystem` pipeline unchanged.

## 1. Ground truth (read these before writing code)

| Concern | File |
|---|---|
| Existing anchor clip (per-clip offsets, ExposedReference bone — the thing we replace) | `BovineLabs.Timeline.Animation.Authoring/WeaponAnchorClip.cs` |
| Weapon-side buffer + rest authoring | `BovineLabs.Timeline.Animation.Authoring/WeaponAnchorTargetAuthoring.cs` |
| The blend pipeline (Gather→Extract→Fill→Resolve) — REUSE, do not rewrite | `BovineLabs.Timeline.Animation/WeaponAnchorBlendSystem.cs` |
| Weighted quaternion blend + relax-to-rest math — REUSE | `BovineLabs.Timeline.Animation/AnchorMath.cs` |
| Same-frame bone pose via manual parent walk — REUSE | `BovineLabs.Timeline.Animation/BoneWorld.cs` |
| Sample/rest components | `BovineLabs.Timeline.Animation.Data/WeaponAnchorData.cs` |
| Known bug A4: weapon-parent L2W one frame stale in ResolveJob — FIX in Phase 2 | `REVIEW_NOTES.md` |
| Past attempt to learn from (hard snap, stale bones, velocity freeze hack) — do NOT copy | `com.bovinelabs.timeline.physics` → `Sockets/WeaponSocket*.cs`, `SocketReturn*.cs` |
| Stable object id → prefab registry (the blob key) | `com.bovinelabs.core` → `ObjectManagement/ObjectDefinition.cs` |
| ObjectDefinition picker drawer precedent | `com.bovinelabs.timeline.core` → `Editor/ObjectDefinitionFieldDrawer.cs` |
| Bone name → hash, rig blob, per-rig bone remap | `com.rukhanka.animation` runtime (RigDefinitionBlob et al.) |
| Track/clip authoring protocol for this package | `Plugins~/skills/unity-track-animation/SKILL.md` |

Decisions locked by the failures of the two prior attempts:
- **No reparenting.** Attachment is copy-transform via the sample pipeline. Physics bodies
  hate hierarchy churn and the EntityLinks parenting track already documented the
  restore-pointer-not-pose trap.
- **Bones addressed by Rukhanka name hash, never `ExposedReference<Transform>`.** A hash
  resolves on any rig at runtime; clips need zero scene wiring; one preset asset works on
  every character.
- **Never zero `PhysicsVelocity` to fake attachment.** While attached the body is
  kinematic-followed by the sample pipeline; on drop we hand real velocity to physics.

## 2. Data model

### Authoring: `WeaponGripPresetObject : ScriptableObject`
One asset per weapon, next to its ObjectDefinition.

```
ObjectDefinition weapon                 // registry key
int defaultGrip                         // designer-set initial/holster pose  ← "initial position"
Grip[] grips:
    string name                         // designer label, hashed for runtime key
    string bone                         // Rukhanka bone name (hashed at bake)
    float3 localPosition                // authored in a scene gizmo/handle
    quaternion localRotation
```

- Grips are UID-keyed entries following the `ISettings` auto-register pattern
  (`ObjectManagementSettings` precedent). `WeaponGripSettings : SettingsBase` collects all
  preset assets and bakes the registry blob.
- Editor UX (Phase 1): custom drawer — grip list with bone-name field, position/rotation
  handles in SceneView when the preset asset is selected, dropdown of grips on clips.

### Baked: singleton `WeaponGripRegistry`

```csharp
public struct WeaponGripRegistryBlob
{
    public BlobHashMap<ObjectId, WeaponGrips> Weapons;   // ObjectDefinition id → grips
}
public struct WeaponGrips { public BlobArray<Grip> Grips; public int DefaultGrip; }
public struct Grip { public uint Key; public uint BoneHash; public float3 Position; public quaternion Rotation; }
```

Lookup path at runtime: **object id (already on the spawned weapon) → WeaponGrips → grip
by key hash**. This is the "load a different weapon by object id" flow: the id you spawn
with is the key into its pose data. Fall back to `DefaultGrip` when a clip references a
missing key (warn once, `BL_DEBUG`).

### Runtime state (Phase 2)
```csharp
public struct WeaponAttachment : IComponentData, IEnableableComponent
{
    public Entity Holder;      // character entity (rig root)
    public uint Grip;          // current grip key
}
```
Persistent attachment outside clip windows: while enabled and no grip clip is active, the
sample system emits one full-weight sample for `Grip` — the equip cutscene ends and the
weapon stays in the hand.

## 3. Clips (both follow SKILL.md conventions, `Builder` bake pattern — note
`WeaponAnchorClip` currently bypasses `Builder`; new code must not)

### `WeaponGripClip` — the workhorse (Phase 1)
- Track binding: weapon entity (same as `WeaponAnchorClip`).
- Fields: grip key (dropdown sourced from the bound weapon's preset asset in editor,
  baked as uint hash).
- Runtime: resolve grip from registry blob + bone entity from the holder's rig by hash →
  write `WeaponAnchorSample`. Everything downstream (weighted blend, ease in/out via
  `ClipWeight`, relax-to-rest) is the existing pipeline.
- Holder resolution: the weapon's `WeaponAttachment.Holder` when present, else the
  timeline owner — keep it simple, no new binding type.
- Two overlapping `WeaponGripClip`s = preset crossfade. That is the whole "blend between
  holds during animation" feature; no extra blending code.

### `WeaponStateClip` — lifecycle edges (Phase 2). Mode enum:
- **Equip(objectId, grip)** — spawn via ObjectDefinition, enable `WeaponAttachment` at
  `defaultGrip`/named grip; weapon appears at the authored pose, never at an incidental one.
- **ReAttach(grip)** — retarget attachment; pose change rides the sample crossfade
  (hand → back holster for free).
- **Drop** — disable `WeaponAttachment`, re-enable physics, write `PhysicsVelocity` from
  the last two frames of blended pose delta (linear + angular) so it flies believably.
- **Pickup** — target via EntityLinks (`LinkedTarget`); capture the ground weapon's world
  pose as blend-from and blend toward the grip pose over the clip ease-in (the relax math
  run in the attach direction). "With style."

## 4. Systems (runtime asm)

- `WeaponGripSampleSystem` — replaces/absorbs today's `GatherJob` clip query: resolves
  grip + bone, emits `WeaponAnchorSample` for (a) active grip clips, (b) enabled
  `WeaponAttachment`s with no active clip. Legacy `WeaponAnchorClip` samples keep flowing
  through the same buffer during migration.
- `WeaponAnchorBlendSystem` — unchanged, except Phase 2 fixes A4: compute the weapon's
  parent L2W with the same `BoneWorld` manual walk used for bones.
- `WeaponLifecycleSystem` — consumes `WeaponStateClip` edges: spawn/attach/drop/pickup,
  physics handoff. Structural changes via ECB; no per-frame structural work.

## 5. Migration
- `WeaponAnchorClip` keeps compiling and sampling (same buffer). Mark `[Obsolete]` only
  after grip clips cover existing timelines.
- `com.bovinelabs.timeline.physics` `WeaponSocket*`/`SocketReturn*` are superseded; do not
  modify that package in this effort — note the replacement in HANDOFF_DESIGNERS.md.

## 6. Phases
1. **Grips**: blob + settings + preset SO + `WeaponGripClip` + `WeaponGripSampleSystem`
   + editor drawers/gizmos + tests. Ships alone; kills per-clip offset typing.
2. **Lifecycle**: `WeaponStateClip` + `WeaponAttachment` + `WeaponLifecycleSystem` +
   drop velocity handoff + A4 fix + HANDOFF_DESIGNERS.md update + tests.
3. **Deferred (do not build)**: per-character grip overrides, GPU-animation path
   (MISSING_FEATURES M5), finger IK micro-adjust.

## 7. Definition of done (per phase)
- Compiles clean (editor + player asms) via the repo's established verification.
- `ECSTestsFixture` tests: registry lookup (hit/miss/default), grip clip sample emission,
  crossfade weights sum, and for Phase 2: attach persistence, drop velocity ≠ 0, pickup
  blend-from capture.
- Zero regressions in existing `WeaponAnchorClip` behavior (its tests keep passing).
- Docs: README section + HANDOFF_DESIGNERS.md entry (designer workflow: create preset
  asset → name grips → drag clip → pick grip from dropdown).
