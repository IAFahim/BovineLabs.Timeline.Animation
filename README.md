# com.bovinelabs.timeline.animation

**Description**: A module for the BovineLabs Timeline package.

**Dependencies**: Documented in package.json.

**Usage**: Add the relevant track to your Timeline.

## Weapon timeline system

Data-driven weapon holding, equip/drop/pickup, and hold-style blending — replaces the
hand-typed `WeaponAnchorClip`.

- **Grips are data.** A `WeaponGripPresetObject` (one per weapon, keyed by its
  `ObjectDefinition` id) names each hold-pose (bone + local offset). All presets bake into
  one `WeaponGripRegistry` blob (`BlobHashMap<ObjectId, WeaponGrips>`). Bones are addressed
  by **Rukhanka name hash**, so one preset works on every rig with no scene wiring.
- **`WeaponGripClip`** drives a bound weapon onto a named grip; overlap two clips to
  crossfade hold-styles. Pose math reuses `WeaponAnchorBlendSystem` unchanged.
- **`WeaponStateClip`** (edge-triggered) modes: **Equip** (spawn from ObjectDefinition
  in-hand), **ReAttach** (retarget grip), **Drop** (physics hand-off with the blended pose
  velocity), **Pickup** (ease a world weapon into the grip).

No reparenting (copy-transform via the sample pipeline); no velocity fakery (real momentum
handed to physics on drop). Designer workflow in `HANDOFF_DESIGNERS.md` §10; full design in
`Documentation~/Architecture.md` § Weapon System Design.

## Documentation

- **Designers**: `HANDOFF_DESIGNERS.md` (package root) — how to author every track.
- **Engineers**: `Documentation~/Architecture.md` — consolidated architecture, design
  records (inertialization, ragdoll, weapon system), review notes, and open items.
- **Backlog**: `TODO.md` — the full production audit and its implementation status.
