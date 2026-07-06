# Changelog
All notable changes to this project will be documented in this file.

## [Unreleased]
### Added
- Weapon grip system Phase 1: `WeaponGripRegistry` blob (object id + grip key → local pose),
  `WeaponGripClip`, grip sample system, grip presets, editor UX, and tests.
- Weapon lifecycle Phase 2: `WeaponStateClip` (Equip / ReAttach / Drop / Pickup edge modes),
  `WeaponAttachment` persistent-attachment component, `WeaponLifecycleSystem` (ObjectDefinition
  spawn, physics hand-off with the blended pose velocity, easing pickup), and tests.
- Weapon timeline system design doc (`WEAPON_SYSTEM_DESIGN.md`) covering grip presets and
  lifecycle phases; README + `HANDOFF_DESIGNERS.md` §10 designer workflow.

### Fixed
- A4: `WeaponAnchorBlendSystem.ResolveJob` and `FollowPositionOnlySystem` no longer read the
  one-frame-stale parent `LocalToWorld`; they recompute the parent world matrix from fresh
  `LocalTransform` via `BoneWorld.TryComputeWorldMatrix`, falling back to `LocalToWorld` only
  for parents outside the `LocalTransform` hierarchy.

## [1.0.0] - 2026-01-01
### Added
- Initial release.
