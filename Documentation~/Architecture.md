# BovineLabs.Timeline.Animation — Architecture & Design

> Engineer-facing consolidated design record. This file absorbs the previously loose,
> point-in-time design/review notes (`INERTIALIZATION_DESIGN.md`, `RAGDOLL_PLAN.md`,
> `WEAPON_SYSTEM_DESIGN.md`, `REVIEW_NOTES.md`, `MISSING_FEATURES.md`) into one living
> document (per TODO #28). Designer-facing docs stay at the package root in
> `HANDOFF_DESIGNERS.md`; the audit backlog stays in `TODO.md`.
>
> Some sections are historical (they predate later reworks). Where a note conflicts with
> current code, the code and `TODO.md` win. The `## Missing Features / Open Items`
> section below is the surviving open-item list folded from `MISSING_FEATURES.md`.

---


# ═══════════════════════════════════════════════════════════════
# Section: Architecture Review Notes
# (consolidated from REVIEW_NOTES.md)
# ═══════════════════════════════════════════════════════════════

# BovineLabs.Timeline.Animation — Review Notes

Running log of code-review findings. Confidence is stated explicitly; nothing here is
asserted as a proven bug unless it says so.

## Architecture (confirmed)

This package bypasses Rukhanka's Unity-AnimatorController layer (state machine /
`.controller` asset) and drives the rig directly. It still uses Rukhanka's low-level
playback engine.

Pipeline per frame:

```
Timeline clips/tracks (baked)
  → TimelineSingleAnimationTrackSystem / TimelineAnimationBlendTree2DTrackSystem  → BlendGroupEntry
  → TimelineFallbackOverrideSystem (latch idle/fallback override)
  → TimelineAnimationUnificationSystem  → AnimationToProcessComponent   (same buffer the controller would write)
  → AnimationProcessSystem → AnimationApplicationSystem  (Rukhanka)
```

- Timeline-driven actors carry `BlendGroupTimer` + `FallbackBlend` + blend buffers
  (baked by `TimelineAnimationStateAuthoring`), NOT `AnimatorControllerLayerComponent`.
- `BlendGroupEntry` is the per-frame request buffer; `TimelineAnimationUnificationSystem`
  consumes it and `Clear()`s it at the end of `Execute`.
- Mutually exclusive with a controller per entity: `UnifyAnimationsJob.Execute` starts
  with `atps.Clear()` → last writer wins.
- `AnimationToProcessComponent` is a forked/patched Rukhanka struct: it carries the parity
  fields `positionOffset` / `rotationOffset` / `removeStartOffset` / `applyFootIK`, and
  `ComputeBoneAnimationJob.Execute` has the matching "TIMELINE OFFSET PATCH". These exist
  so the Timeline driver can do per-clip root offsets / foot-IK.

## Findings

### F1 — `EmitFallback` ignores `IsScrubbing` — RESOLVED

**Resolved.** `UnifyAnimationsJob.EmitFallback` now computes
`var fallbackAdvance = (IsScrubbing ? 0f : DeltaTime) / duration;` and both the Hold and
Loop/Clamp branches add `fallbackAdvance` instead of the raw `DeltaTime / duration`, matching
the sibling integrators (`IntegrateWeights` line ~175, `IntegrateBaseLayerControl`). Regression
test added: `UnifyAnimationsBlendMathTests.cs` → `FallbackScrubAdvanceTests`. Original analysis
retained below.

File: `BovineLabs.Timeline.Animation/TimelineAnimationUnificationSystem.cs`,
`UnifyAnimationsJob.EmitFallback`.

Every other time integration in this job snaps/zeroes when `IsScrubbing`:
- `IntegrateWeights`: `s.CurrentWeight = s.TargetWeight`; fading advance uses `(IsScrubbing ? 0f : DeltaTime)`.
- `IntegrateBaseLayerControl`: `if (IsScrubbing) timer.BaseLayerControl = target;`

But the fallback clock advances unconditionally:

```csharp
timer.FallbackAccumulatedTime += DeltaTime / duration;   // no IsScrubbing guard
```

Effect: in editor preview the idle/fallback animation free-runs by frame DeltaTime instead
of following the scrub position; dragging the playhead backward won't rewind the fallback.

Caveats (why NOT a 200% claim):
- `IsScrubbing` is only true under `#if UNITY_EDITOR && !Application.isPlaying` → runtime unaffected.
- In the editor preview path (`EditorPreviewBootstrap` → `world.Update()` driven by
  `AnimationPreviewUpdater`), editor-world `SystemAPI.Time.DeltaTime` may be ~0, in which
  case the advance is negligible and invisible.
- Not yet reproduced. Suggested: add a scrub-direction test around `EmitFallback` in
  `BovineLabs.Timeline.Animation.Tests`.

## Areas reviewed and found correct / deliberate (no bug)

- `BlendGroupEntry` → `SmoothBlendGroupEntry` → `AnimationToProcessComponent` clear/refill ordering.
- Multihashmap capacity pre-sizing via `CalculateEntityCountWithoutFiltering` (upper bound;
  each entity adds ≤1) in Single / BlendTree2D / FallbackOverride systems.
- `AfterImageSpawnSystem` spawn/reset lifecycle keyed on `ClipActive` (enableable):
  spawn once, frozen ATP snapshot, destroy on deactivate, respawn on reactivate.
- `WeaponAnchorBlendSystem` + `AnchorMath.WeightedBlend` (hemisphere-aligned weighted
  quaternion average; parent-space conversion).
- Parallel job dependency chains (gather → extract keys → apply) across all systems.
- Base-vs-non-base layer weight normalization in `EmitClips`.
- `TimelineSingleAnimationTrackSystem` extrapolation handling.
- `FallbackEquality` matching in `TimelineFallbackOverrideSystem` (latch vs restore are
  disjoint entity sets → no write conflict).

## Foundation critique (architecture / quality)

Severity: [H]igh / [M]edium / [L]ow. Citations are to files in this package.

### Strengths (keep)

- Clean layering Data / Authoring / Builders / Systems; `IEntityCommands.ApplyTo` builder
  pattern is baker-agnostic and testable.
- Well-staged single-responsibility pipeline (gather → fallback latch → unification → Rukhanka).
- `MotionId` as stable cross-frame identity enables weight smoothing instead of popping.
- ParallelWriter capacity pre-sizing via `CalculateEntityCountWithoutFiltering`; stackalloc→heap
  fallback in `DecomposeAndAppendBlendTreeJob` (128 tracks / 64 motions).
- Burst on hot jobs; framerate-independent smoothing `1 - exp(-speed*dt)`.
- Editor preview path actually handled.

### Correctness

- **C1 [H, ~certain] — BlendTree2D per-clip offsets/footIK/removeStartOffset are dead fields.** *(RESOLVED)*
  Fixed: `GatherClipDataJob.Execute` now reads `directionData.PositionOffset/RotationOffset/
  RemoveStartOffset/ApplyFootIK` into `TrackClipData`; `DecomposeAndAppendBlendTreeJob` carries them
  through `PerTrackBlend` (best-weight clip wins) and `ProcessTrackMotions` merges them exactly like
  the single-clip path —
  `finalPosOffset = trackData.TrackPositionOffset + rotate(trackData.TrackRotationOffset, blend.PositionOffset)`,
  `finalRotOffset = mul(trackData.TrackRotationOffset, blend.RotationOffset)`,
  `removeStartOffset = blend.RemoveStartOffset || trackHasOffsets`, `ApplyFootIK = blend.ApplyFootIK`.
  The four inspector fields now drive the emitted `BlendGroupEntry`.
- **C2 [L] — `MotionId.Fallback = 0xFFFFFFFF` collides with hash space.** *(RESOLVED)*
  Fixed: `MotionId.Compute` now ends `return id == Fallback ? Fallback - 1u : id;`, so a computed id
  can never equal the sentinel. Covered by `MotionIdSentinelTests.Compute_NeverEqualsFallbackSentinel`.
- **C3 [L, editor-only] — `EmitFallback` ignores `IsScrubbing`.** *(RESOLVED)* See F1 above.
- **C4 [L] — `BlendTree2DClip.Bake` early-returns without `base.Bake`.** *(RESOLVED)*
  Fixed: both early returns (missing `ReadFrom`, key-resolution failure) now call `base.Bake(clipEntity, context)`
  before returning, so the clip is no longer half-baked.

### Architecture & performance

- **A1 [H] — `AfterImageSpawnSystem` does main-thread structural changes + full sync every frame.** *(RESOLVED)*
  Fixed: the system now records all spawns/destroys/sets through a
  `BeginSimulationEntityCommandBufferSystem` command buffer (`ecb.Instantiate/SetComponent/SetBuffer/DestroyEntity`).
  No `state.CompleteDependency()` and no direct `EntityManager.Instantiate/Destroy` remain — the per-frame
  sync point is gone. (Spawn requests are still gathered on the main thread reading source buffers via
  `EntityManager`, but those are reads, not structural changes.)
- **A2 [M] — allocation inconsistency.** `TimelineAnimationBlendTree2DTrackSystem` allocates
  `clipDataMap` + `targetEntities` as `TempJob` fresh each frame, and `ScriptedAnimator.ComputeBlendTree2D*`
  returns a `Temp` `NativeList` per track per frame; meanwhile Single / Fallback / WeaponAnchor
  systems keep persistent containers and `Clear()`. Align BlendTree2D to the persistent pattern.
- **A3 [L] — `UnifyAnimationsJob` is O(n²) per actor** (`ReconcileRequests` nested loops,
  `OverrideSumForLayer` rescans inside `EmitClips`). Fine while `n` (active clips/actor) is tiny;
  bucket by layer if layers stack.

### Netcode / determinism (RESOLVED — non-issue)

Systems run under `ServerSimulation | ClientSimulation`, but smoothing/playback state is **local,
not ghosted**: `SmoothBlendGroupEntry.CurrentWeight`, `BlendGroupTimer`, `BlendTreePlaybackStateElement`.
The original concern was: if the game were networked + predicted, those independently-integrated
weights/phase could diverge and wouldn't roll back cleanly (Rukhanka ghosts its own controller state
via `RUKHANKA_WITH_NETCODE`; this Timeline layer ghosts no equivalent).

**Resolved: the project is LOCAL-ONLY** — single-player plus couch co-op (local split-screen).
Verified across the codebase: **no `com.unity.netcode` dependency, no `GhostComponent`/`GhostField`/
prediction usage, no client/server world bootstrap.** Couch co-op is not networked. There is no
rollback and no predicted client, so the non-ghosted smoothing-state divergence **cannot occur**.
→ **Non-issue; no action needed.** The `ServerSimulation | ClientSimulation` group flags are simply
the default group set and carry no netcode here.

Note: couch co-op does mean multiple player characters are live simultaneously, each with its own
timeline — so concurrent multi-actor blending is the normal case (handled per-entity, no shared
state to diverge), not an edge case.

### Authoring & robustness

- ~~**A4 [L] — `WeaponAnchorBlendSystem` / `FollowPositionOnlySystem` read bone `LocalToWorld`
  one frame stale**~~ (run in `TransformSystemGroup` before `LocalToWorldSystem`). Visible on fast
  swings / hard cuts. **RESOLVED:** both systems now recompute the world matrix from fresh
  `LocalTransform` via `BoneWorld.TryComputeWorldMatrix` (walking `Parent` +
  `PostTransformMatrix`), falling back to the cached `LocalToWorld` only for entities outside
  the `LocalTransform` hierarchy.
- **A5 [L] — no additive blend-mode authoring** (all paths hardcode `Override`). Ties to
  `MISSING_FEATURES.md` M2.
- **A6 [nit] — `AfterImageClip.duration => 20`** default (20s ghost) is an odd default.

### Testing & hygiene

- **T1 [M] — coverage skewed.** *(PARTLY RESOLVED)* `AnimationDataTests` is still mostly trivial
  reflection asserts. **Now added:** `UnifyAnimationsBlendMathTests` (override base/additional-layer
  normalization, `fallbackWeight = 1 - baseControl`, bucketed-vs-linear layer sum), `MotionIdSentinelTests`
  (C2 sentinel), and `FallbackScrubAdvanceTests` (C3 scrub guard). **Still missing:** a full-World
  behavior test exercising the real `UnifyAnimationsJob` / BlendTree2D offset emission end-to-end
  (current tests mirror the math via static helpers rather than booting the system).
- Nits: `Motionid.cs` filename vs `MotionId` type; `PlayerMoveInput` defined in this *Animation*
  package under `BovineLabs.Timeline.PlayerInputs.Data` — placement/coupling smell.

### Priority order

1. ~~C1 (designer-facing no-op blend-tree offsets)~~ — RESOLVED
2. ~~A1 (after-image sync / structural churn)~~ — RESOLVED
3. ~~Netcode question (if predicted) → then ghost or recompute the smoothing state~~ — RESOLVED: local-only, dropped
4. T1 (full-World behavior test) — PARTLY RESOLVED: static-helper math/sentinel/scrub tests added; end-to-end system test still open
5. ~~C2–C4~~ RESOLVED; ~~A4 (stale L2W)~~ RESOLVED; remaining polish = A2, A3, A5, A6 (allocation consistency, O(n²), additive authoring, AfterImage default).

## Resolved (was: open / not yet examined deeply)

- ~~Sibling `BovineLabs.Timeline.*` packages (to confirm VFX/material gaps in MISSING_FEATURES V1/V2).~~
  **RESOLVED:** all 10 `BovineLabs.Timeline.*` packages (Core, Animation, Physics, EntityLinks, Essence,
  Distance, Time, PlayerInputs, Grid.Influence, UI) were surveyed. **None** implement IK, look-at, aim,
  VFX, material/shader property control, or skinned-mesh sampling → M1/V1/V2 are confirmed missing
  stack-wide, not just from this package.
- ~~Whether the game is netcoded/predicted (drives the determinism finding).~~
  **RESOLVED:** local-only (single-player + couch co-op), no netcode. See the Netcode/determinism section
  above — non-issue.


# ═══════════════════════════════════════════════════════════════
# Section: Inertialization Design
# (consolidated from INERTIALIZATION_DESIGN.md)
# ═══════════════════════════════════════════════════════════════

# Inertialization (momentum-preserving transitions) — design

Status: spec + verified math; implementation in progress.
Technique: David Bollo, "Inertialization: High-Performance Animation Transitions" (GDC 2018).

## Why
Crossfade blends *two clips averaged by weight* for the blend window → mushy mid-poses,
foot slide, and a forced choice between "snap" (short) and "smear" (long). Inertialization
instead **cuts to the target clip immediately** and adds a per-bone offset that starts where
the previous pose was (matching position AND velocity) and **decays to zero** over a short
window — carrying the existing motion's momentum into the new clip. Zero cost except during
the decay window, so combat stays responsive (no constant lag, unlike a spring).

## The math (VERIFIED — see scratchpad/inert proof, all 6 boundary conditions green)
Per scalar channel, given offset `x0`, offset-velocity `v0`, offset-accel `a0`, duration `T`:

```
A = -(a0*T^2 + 6*v0*T + 12*x0) / (2*T^5)
B =  (3*a0*T^2 + 16*v0*T + 30*x0) / (2*T^4)
C = -(3*a0*T^2 + 12*v0*T + 20*x0) / (2*T^3)
x(t) = A*t^5 + B*t^4 + C*t^3 + (a0/2)*t^2 + v0*t + x0     for t in [0, T], else 0
```
Satisfies x(0)=x0, x'(0)=v0, x''(0)=a0, x(T)=x'(T)=x''(T)=0.
Overshoot guard (offset already closing): `T = min(T, -5*x0/v0)` when that is positive.

Channels:
- **Position**: run the curve per component of `float3` (x0/v0 are float3, a0=0 for v1).
- **Rotation**: take the relative rotation `qOffset = qPrevDisplayed * inverse(qTarget)`, convert to
  angle-axis → scalar angle `x0` about a fixed `axis`; decay the scalar; reapply
  `q = axisAngle(axis, x(t)) * qTarget`. (a0=0 for v1.)

## Seam (from Rukhanka recon)
New `InertializationSystem : ISystem`, `[UpdateAfter(RukhankaAnimationInjectionSystemGroup)]`
`[UpdateBefore(AnimationApplicationSystem)]` (after pose+IK, before skinning).
Read/write bones via `AnimationStream` exactly as `DynamicBoneChainSystem` does (handles
local↔world coherence). Bone index = `rigDef.dynamicFrameData.bonePoseOffset + rigBoneIndex`.

## Per-bone persistent state (new buffer on the rig entity)
```
struct InertializationBoneState : IBufferElementData   // one per rig bone
{
    float3 posOffset0;  float3 posVel0;     // captured offset + its velocity (position)
    float3 rotAxis;     float  rotAngle0;   float rotVel0;  // rotation scalar channel
    BoneTransform prevDisplayed;            // last frame's actually-displayed local pose
    BoneTransform prevPrevDisplayed;        // frame before that (for velocity at capture)
}
struct InertializationState : IComponentData  // per rig
{
    float elapsed;       // seconds since last capture (the quintic's t)
    float duration;      // T for the active decay (clamped per capture)
    byte  active;        // 0 = idle (no offset), 1 = decaying
    uint  lastDominant;  // motionId of last frame's dominant entry (transition detector)
}
```

## Frame algorithm (per rig)
1. Compute `dominant` = motionId of the highest-weight `AnimationToProcessComponent` entry.
2. **Transition?** `dominant != state.lastDominant` (and both valid). If yes → CAPTURE:
   - For each bone: `target = current local pose` (this frame, pre-inertialization).
     `x0 = prevDisplayed - target` (position componentwise; rotation as angle-axis above).
     `v0 = (prevDisplayed - prevPrevDisplayed)/dt` mapped into offset space (the incoming
     motion's velocity → the "coast"). Clamp `T` via the overshoot guard.
   - `state.elapsed = 0; state.active = 1; state.duration = configured (clamped)`.
3. **If active**: `t = elapsed`; if `t >= duration` → `active = 0` (offset gone). Else evaluate
   the quintic per channel, `displayed = target + offset`; write `displayed` back via
   `AnimationStream`. `elapsed += dt`.
4. **Always**: shift history `prevPrevDisplayed = prevDisplayed; prevDisplayed = displayed`.
5. `state.lastDominant = dominant`.

History (steps 4) runs every frame even when idle, so a capture always has a valid velocity.

## Authoring knob
`TimelineAnimationStateAuthoring.inertializationDuration` (seconds, default 0 = OFF → behaves
exactly as today; designers opt in). When > 0, the rig gets the inertialization buffers at bake
and transitions use inertialization instead of relying solely on the global crossfade. Tunable
per character; the feel parameter is this duration (typ. 0.1–0.3s).

## Interplay with existing crossfade
Inertialization absorbs the *pose discontinuity* at a dominant-clip change. With it enabled,
set the global `blendIn/OutDuration` low/0 for the crispest result (the offset decay IS the
blend). Leaving the global blend on is harmless but redundant. Documented for designers.

## Gotchas (from recon)
- Bone buffers are NOT double-buffered → capture during the transition frame (we keep our own
  2-frame history, so this is satisfied).
- `[CullAnimationsTag]` rigs are frozen → skip (no capture, no decay) and clear `active`.
- Respect `bonePoseOffset` indexing; never assume sequential packing.
- Burst: no managed types in the job; thread DeltaTime in; quaternion via `Unity.Mathematics`.

## Verification plan
- Standalone quintic proof: DONE (boundary conditions + overshoot guard green).
- Compile gate: the new assembly builds.
- Live (owed, needs editor): trigger a trackA→trackB hard cut with duration 0 vs 0.2; confirm
  no snap and no foot slide; tune duration. This is the one step that needs a human in-editor.
```


# ═══════════════════════════════════════════════════════════════
# Section: Ragdoll Plan
# (consolidated from RAGDOLL_PLAN.md)
# ═══════════════════════════════════════════════════════════════

# Plan: DOTS Ragdoll + enable/disable Timeline clip (Rukhanka rigs)

## Context / goal
Bring the Rukhanka "ragdoll" sample behaviour to vex-ee and add a Timeline clip to toggle it.
The Rukhanka sample is a **classic** Unity ragdoll (11 `Rigidbody` + 10 `CharacterJoint` +
capsule colliders + a `RigDefinitionAuthoring`) — it does **not** port, because vex-ee is pure
DOTS (classic Rigidbody/CharacterJoint don't bake into a SubScene). There is **no ragdoll code in
Rukhanka** and **no ragdoll Timeline clip** anywhere in the project. So this is a build, on a clean
DOTS path. The good news: every primitive we need already exists; nothing exotic to invent.

## The building blocks (all verified present)
- **Bone↔physics bridge:** `Rukhanka.OverrideTransformIKComponent { Entity target; float positionWeight; float rotationWeight }`
  — `IEnableableComponent`. `OverrideTransformIKSystem` (in `RukhankaAnimationInjectionSystemGroup`,
  after FABRIK) forces the bound bone to match `target`'s world pose at weight=1. **The enable bit IS
  the ragdoll switch.** Authoring: `OverrideTransformIKAuthoring`/`OverrideTransformIKBaker` (Rukhanka.Hybrid).
  Target can be any entity with `LocalTransform`/`LocalToWorld`.
- **Kinematic↔dynamic toggle:** `Unity.Physics.PhysicsMassOverride { byte IsKinematic; byte SetVelocityToZero }`.
  Set `IsKinematic=1` → body follows its transform (animation); `=0` → body simulates. This is exactly
  what `PhysicsKinematicOverrideApplySystem` (BovineLabs.Timeline.Physics) already does — reuse the pattern.
  A `PhysicsBodyAuthoring` body authored **Kinematic** still bakes `PhysicsMass` + `PhysicsVelocity`, so it
  CAN be flipped dynamic at runtime (verified in `PhysicsBodyBakingSystem`).
- **DOTS physics authoring:** `Unity.Physics.Authoring.PhysicsBodyAuthoring` (MotionType/Mass/damping/GravityFactor),
  `PhysicsShapeAuthoring` (capsule), and joints `RagdollJoint` (cone+twist, the standard ragdoll joint),
  `LimitedHingeJoint` (knees/elbows) — all in `Packages/com.unity.physics.custom`. ⚠ `PositionLocal`
  defaults to the body CENTER — joints MUST be anchored at the bone connection point, not (0,0,0).
- **Bone lookup:** `RigDefinitionComponent.rigBlob.Value.humanData.Value.humanBoneToSkeletonBoneIndices[(int)HumanBodyBones.X]`
  → skeleton bone index; rig bones are entities carrying `AnimatorEntityRefComponent { boneIndexInAnimationRig, animatorEntity }`.
  Animated world poses live in `RuntimeAnimationData.worldSpaceBonesBuffer` (and on the bone entity's LocalToWorld after `AnimationApplicationSystem`).
- **Timeline track template:** `PhysicsKinematicOverrideTrack/Clip/Data/Builder/TrackSystem` in
  `BovineLabs.Timeline.Physics` is the exact while-active override pattern to clone (DOTSTrack/DOTSClip,
  `IAnimatedComponent<T>`, `Active<X>` `IEnableableComponent`, edge-triggered via `ClipActive`/`ClipActivePrevious`,
  `TrackBinding.Value` = bound entity).

## Architecture — "snap on enable, then go dynamic" (OFF is FREE)
A parallel ragdoll skeleton of physics capsules sits dormant next to the rig; the switch is edge-triggered,
so off-state costs nothing:

- **Ragdoll OFF (default) — zero per-frame cost:** the physics bodies are **disabled** (`Disabled` / out of the
  physics world → no broadphase, no sim); `OverrideTransformIK` is **disabled** so `OverrideTransformIKSystem`
  (which has `RequireForUpdate` on its query) **doesn't run at all**; **no follow system runs**. State ≈ a
  dormant archetype. Only normal animation executes.
- **On ENABLE (one-time, edge):** snapshot the current animated bone world poses → teleport each body onto its
  bone (+ optional velocity seed for momentum), flip bodies **Dynamic** (`PhysicsMassOverride.IsKinematic=0`),
  enable the bones' `OverrideTransformIK`. ~11 bodies, one frame — imperceptible, and seamless because the snap
  happens at the instant of switching.
- **Ragdoll ON:** physics simulates the bodies under gravity + joint limits; `OverrideTransformIK` makes the
  visual bones follow the bodies. This is the ONLY time ragdoll work happens.
- **On DISABLE:** disable `OverrideTransformIK`, disable/kinematic the bodies → back to free. (Blend-back/get-up
  = Phase 3.)

NOTE: continuous kinematic-follow was rejected — it would make OFF non-free for no benefit (its only upside is
using the capsules as live hitboxes on the animated character, a separate opt-in feature). Ordering: the
enable-snapshot runs after `AnimationApplicationSystem`; `OverrideTransformIK` reads post-sim body `LocalToWorld`
(1 fixed-step latency while ON, acceptable).

## Components & where they live (mirrors the 6-assembly layout)
- `*.Data`: `RagdollData{bool Enable}`, `RagdollAnimated : IAnimatedComponent<RagdollData>`,
  `ActiveRagdoll : IComponentData, IEnableableComponent`, `RagdollState` (restore), plus a
  `RagdollBodyLink { Entity Bone; Entity Body }` buffer/component tying each physics body to its rig bone.
- `*.Authoring`: `RagdollAuthoring` (the generator — see Phase 2), `RagdollTrack`/`RagdollClip` (+ builder).
- `*`(runtime): `RagdollApplySystem` — edge-triggered on `ActiveRagdoll` (IEnableableComponent): on the
  enable edge, snapshot animated bone world poses → set each body's transform (+velocity), remove `Disabled`,
  set `PhysicsMassOverride.IsKinematic=0`, enable the bones' `OverrideTransformIK`; on the disable edge, reverse
  it (disable IK, re-disable/kinematic bodies). NO per-frame follow system. The later `RagdollTrackSystem`
  (Timeline) just toggles `ActiveRagdoll`; a plain component/API toggle works identically for non-timeline use.

## The enable/disable clip (your explicit ask)
`RagdollTrack : DOTSTrack` `[TrackClipType(RagdollClip)] [TrackBindingType(GameObject)]` bound to the rig.
`RagdollClip : DOTSClip` with `public bool enableRagdoll = true; duration => 1; clipCaps = None`. Bakes
`RagdollAnimated` via a builder. `RagdollTrackSystem` (clone of `PhysicsKinematicOverrideTrackSystem`):
on clip-enter enable `ActiveRagdoll` on the bound rig; on exit disable it. `RagdollApplySystem` reacts to
`ActiveRagdoll` enabled→ragdoll ON (bodies dynamic + OverrideTransformIK on), disabled→OFF (bodies kinematic +
OverrideTransformIK off). Net: drop a clip on the track → character ragdolls for the clip's duration, then
returns to animation. (A one-way "stay ragdolled" variant = a clip that latches and doesn't restore.)

## Phased implementation (de-risk the novel part first)
1. **Proof (the risk): manual ragdoll + bridge + first-cut clip.** Hand-author ~6 physics capsules+joints on
   ONE rig (pelvis/spine/head/upperarms/upperlegs), wire `OverrideTransformIK` + `RagdollBodyLink`, build the
   two runtime systems + a minimal `RagdollClip`. Prove: clip active → it ragdolls; clip ends → back to idle.
   This validates the kinematic↔dynamic switch + bone-follow, which is the only genuinely new mechanism.
2. **Auto-generator `RagdollAuthoring`.** A DOTS equivalent of Unity's Ragdoll Wizard: from the humanoid
   `HumanBodyBones`, generate the ~11 standard bodies (pelvis, spine, head, L/R upper+lower arm, L/R upper+lower
   leg), capsules sized from bone lengths, `RagdollJoint` for shoulders/hips/neck and `LimitedHingeJoint` for
   knees/elbows, anchored at the joint (not body center), with default masses/limits. One click → ragdoll-ready rig.
3. **Polish.** Velocity inheritance (ragdoll with momentum), per-limb/partial ragdoll (enable a bone subset),
   blend-back/get-up, tuning presets (à la the PID presets). Editor-preview ragdoll works for free since the
   IK injection group already runs in the preview world.

## Risks / traps
- **Ragdoll quality is hand-tuning, not code** — collider sizes, joint axes/limits, masses. Auto-gen gives a
  starting point; expect per-character tuning. Budget time here, it's the real cost.
- `PositionLocal` joint-center trap (anchor at the bone joint, vertical/twist axes per limb).
- Seamless switch needs bodies co-located with bones at the instant of going dynamic (the kinematic-follow guarantees it).
- Gameplay/root-motion interaction: while ragdolling, animation/root-motion should not fight the bodies (disable the rig's normal drive on ragdolled bones — OverrideTransformIK already overrides them).

## Verification
- Build a SubScene rig with the ragdoll + a director with a `RagdollClip`. In play: before the clip, idle plays
  and bodies shadow the bones (kinematic); during the clip, the character collapses under gravity and limbs
  respect joint limits; after, it returns to animation. Probe with the same `unity-cli` ECS reads we used for
  foot IK (bone world positions, `PhysicsMassOverride.IsKinematic`, `OverrideTransformIK` enabled state).
- Console clean; no classic physics components anywhere (ECS-pure).

## Honest scope
This is a real feature (~physics authoring + 3 systems + a new Timeline track), not a port. Phase 1 is the
meaningful spike (proves the mechanism); Phase 2 is the ergonomics; Phase 3 is polish. Recommend doing
Phase 1 first and looking at it before committing to 2–3.

## Timing note: the one-fixed-step activation skew (TODO #29)

`RagdollApplySystem` runs in `FixedStepSimulationSystemGroup`, `UpdateAfter(PhysicsSystemGroup)`, while the
animation that produces the bone poses runs once per *rendered* frame (variable step) in the
`TimelineComponentAnimationGroup` → Rukhanka pipeline. On the ragdoll ENTER edge the system snaps each body onto
`LocalToWorld` of its bone (`bone.LTW ∘ BoneLocalPos/Rot`). That `LocalToWorld` is written by
`AnimationApplicationSystem`/`LocalToWorldSystem` on the *previous* frame, so the snap pose is up to one fixed
step stale relative to the freshest animated pose. This is deliberate and acceptable: at the instant of
activation the body position is off by at most `velocity * fixedDeltaTime`, which at a walk (~1.5 m/s, 1/60 s) is
~2.5 cm and invisible once the solver takes over the very next step. Reading the live
`RuntimeAnimationData.worldSpaceBonesBuffer` instead would remove the skew but forces a cross-group sync point
(the fixed-step system would have to complete a dependency on the per-frame animation group), which is not worth
the cost. If a visible pop ever appears on fast-moving characters, escalate to the fresh-buffer read; until then
the one-step LTW staleness is the intended contract.


# ═══════════════════════════════════════════════════════════════
# Section: Weapon System Design
# (consolidated from WEAPON_SYSTEM_DESIGN.md)
# ═══════════════════════════════════════════════════════════════

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
| ~~Known bug A4: weapon-parent L2W one frame stale in ResolveJob~~ — FIXED in Phase 2 (`BoneWorld` parent walk in ResolveJob + `FollowPositionOnlySystem`) | `REVIEW_NOTES.md` |
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


# ═══════════════════════════════════════════════════════════════
# Section: Missing Features / Open Items
# (consolidated from MISSING_FEATURES.md)
# ═══════════════════════════════════════════════════════════════

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

