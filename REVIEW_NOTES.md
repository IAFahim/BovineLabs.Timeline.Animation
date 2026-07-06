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
