# BovineLabs.Timeline.Animation — Missing / Underutilized Features

Gap analysis: Rukhanka (and adjacent) capabilities that are reachable by the runtime
engine but have **no Timeline authoring track/clip** in this package, so they cannot be
driven from a fully-Timeline game.

Scope note: this covers the `…Timeline.Animation` package only, checked against the
Rukhanka package. The sibling `BovineLabs.Timeline.*` packages **have since been read**
(boundary caveat below now resolved): none implement IK / VFX / material control, so the
gaps here are stack-wide, not just package-local.

## What IS exposed today (baseline)

- `RukhankaAnimationTrack` / `RukhankaAnimationClip` — single-clip playback, layers,
  avatar mask, per-clip + per-track offsets, fallback/exit-idle override.
- `BlendTree2DTrack` / `BlendTree2DClip` — 2D blend tree, direction from ClipValue /
  PhysicsLinearVelocityNormalized / PlayerMoveInput.
- `AfterImageTrack` / `AfterImageClip` — pose-ghost spawner (frozen ATP snapshot).
- `WeaponAnchorClip` + `WeaponAnchorTargetAuthoring` — weighted CPU bone attach.
- `FollowPositionOnlyAuthoring` — follow a bone's position.
- `TimelineAnimationStateAuthoring` — fallback/idle config + blend in/out.
- `AnimationDebugAuthoring` — debug state.

## Missing — HIGH confidence (zero references to these in the 48 package files)

| # | Capability | Rukhanka evidence it exists | Notes / why it matters for a fully-Timeline game |
|---|---|---|---|
| M1 | **IK — all 5 solvers**: AimIK, FABRIK, TwoBoneIK, OverrideTransformIK, DynamicBoneChain | each is `IComponentData, IEnableableComponent` with a `weight` field; samples drive weights via UI sliders | No track touches any IK component. Cutscene aiming (look-at), foot IK on/off, hand placement, secondary bone dynamics — all impossible from Timeline. Biggest gap. **IN PROGRESS (current wave):** the **look-at / AimIK subset is being implemented now** — a `CharacterLookAt` track/clip driving head/body aim from Timeline, plus a per-character look-at target entity, a setup wizard, and editor tooling. The remaining solvers (foot-IK / TwoBoneIK, FABRIK, OverrideTransformIK, DynamicBoneChain) stay unimplemented / deferred. |
| M2 | **Additive blending** | `AnimationBlendingMode.Additive`; `UnifyAnimationsJob.EmitClips` has the additive branch already | Runtime supports it, but every authoring path hardcodes `Override` (`TimelineSingleAnimationTrackSystem`, `TimelineAnimationBlendTree2DTrackSystem`, `TimelineAnimationStateBuilder`). No way to author an additive layer (recoil, breathing, lean). |
| M3 | **1D / Direct blend trees** | `ScriptedAnimator.ComputeBlendTree1D`, direct-blend in controller | Only 2D wired. 2D can fake 1D but Direct (per-clip weight params) has no path. |
| M4 | **Root motion** | `RootMotionVelocityComponent`, `applyRootMotion`, root-motion delta path in `ComputeBoneAnimationJob` | Timeline only does static pos/rot offsets + `removeStartOffset`. No root-motion-driven traversal/locomotion track. |
| M5 | **GPU attachments** | `GPUAttachmentComponent` (attach mesh to bone in shader) | `WeaponAnchor` is CPU-only. For GPU-animated characters, attaching props/weapons cheaply is unavailable from Timeline. |
| M6 | **Direct blend-shape control** | `BlendShapeWeight` buffer + `AnimateBlendShapeWeightsJob` (keyed by blendshape hash) | Blend shapes only move if baked into a clip's curves. No "drive a named blend shape weight over Timeline" track (facial expression sliders, morphs). |
| M7 | **Animation events** | `AnimationEventComponent`, `AnimationEventEmitSystem` | Rukhanka clip events are not bridged to Timeline signals/markers. |
| M8 | **GPU↔CPU engine switch / animation culling control** | `GPUAnimationEngineTag` (enableable), `CullAnimationsTag`, `AnimationCullingConfig` | Not Timeline-driven (sample-only). Possible perf/LOD track. Niche. |
| M9 | **Runtime skinned-mesh / modular-rig swap** | modular rig sample (`ModularRig*`, `SwitchableBodyPart*`) | No "swap outfit/part at this beat" track. |

## Missing — VFX / Material (true for THIS package; boundary caveat applies)

| # | Capability | Evidence | Status |
|---|---|---|---|
| V1 | **Rukhanka VFX skinned-mesh sampling** | `VFXSkinnedMeshSamplerSystem` feeds `DeformedMeshIndex` into a `VisualEffect` (Rukhanka sample) | No `VisualEffect` reference anywhere in this package. Not a Timeline track. |
| V2 | **Material property control** | Rukhanka `[MaterialProperty]` components (`DeformedMeshIndex`, GPU-attachment MP components) | Nothing in this package drives material properties from Timeline. |

## Boundary caveat — RESOLVED

- This analysis began limited to `BovineLabs.Timeline.Animation` + `Rukhanka`.
- VFX and material control are arguably **out of scope** for an *animation* package and
  would more naturally live in a sibling `BovineLabs.Timeline.*` package (e.g. a UI /
  material / VFX family). That was the open question.
- **Now resolved:** all 10 sibling `BovineLabs.Timeline.*` packages (Core, Animation,
  Physics, EntityLinks, Essence, Distance, Time, PlayerInputs, Grid.Influence, UI) **were
  read.** None implement IK, look-at/aim, VFX, material/shader property control, or
  skinned-mesh sampling.
- Therefore: **V1/V2 (and M1) are confirmed missing from the whole Timeline stack**, not
  just from this package or Rukhanka's *animation* surface. (Caveat: the look-at subset of
  M1 is now being added in the current wave — see M1 above.)

## Verdict

The animation Timeline layer is a focused MVP. A substantial part of Rukhanka —
especially IK (M1), additive blending (M2), root motion (M4), blend shapes (M6), GPU
attachments (M5), and VFX sampling (V1) — is left on the table. Whether each is "missing"
vs "intentionally scoped out" depends on game needs.
