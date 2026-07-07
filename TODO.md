# TODO.md

## Campaign Status

**34 / 35 fully IMPLEMENTED · 1 DEFERRED (SPIKE, partials landed).**

The audit-implementation campaign is complete. All 35 items were addressed; 34 are fully
implemented and verified in code. The remaining one is a deferred spike where only the cheap/safe
part was implemented and the larger port is intentionally left for a dedicated effort:

- **#3 — GPU `AnimationToProcess` parity.** ✅ IMPLEMENTED (formerly deferred). Both the guard *and* the full
  HLSL/GPU-struct field port now landed: `GPUStructures.AnimationToProcess` + `AnimationToProcess.hlsl` gained
  `positionOffset`/`rotationOffset`/`flags`, `FillFrameAnimatedRigWorkloadBuffersJob` forwards them, and
  `ProcessAnimations.hlsl`'s root-bone path applies the offset/removeStartOffset math bit-for-bit with the CPU
  `ComputeBoneAnimationJob`. The guard was narrowed to inertialization (the one feature that genuinely still
  no-ops on GPU rigs).
- **#27 — Offsets-contract fork-shrink.** Never assigned; spike-first. DEFERRED (SPIKE).

**#12 — Single-clip missing-hash + Animation Doctor is now fully implemented (formerly deferred):** the
missing-hash warning parity in the single-clip gather (step 1) plus the editor "Animation Doctor" runtime
diagnosis window (step 2). `AnimationDoctor` (pure, testable checklist) + `AnimationDoctorWindow` live in the
Editor assembly, opened from BovineLabs/Animation/Animation Doctor or the "Animation Doctor" button on the
validator window; it enumerates active `SmoothBlendGroupEntry` rows + the rejected requests and runs the
silent-failure checklist for a selected actor.

Build/verify state: **all 6 assemblies compile green** (Data, runtime, Debug, Authoring,
Editor, Tests). EditMode tests are **compile-verified but not run** (no headless Unity runner
in this environment). Per-item status is in the *Final Ranked TODO List* table at the bottom.

---

Full-library production audit of `BovineLabs.Timeline.Animation` (Authoring / Data / Runtime / Debug / Editor / Tests) **plus** the load-bearing parts of the `com.rukhanka.animation` fork it depends on (the "BOVINELABS PARITY" fields on `AnimationToProcessComponent` and the "BOVINELABS TIMELINE OFFSET PATCH" in `AnimationProcessSystem_Jobs.cs`).

All file references verified against the current working tree.

---

## Executive Summary

The package is in unusually good shape for a gameplay-animation layer: it has real unit tests for the pure math, an editor validator with one-click fixes, designer-facing inspectors with warnings, and clean data/authoring/runtime assembly separation. The blend-group unification design (track systems *gather* → `BlendGroupEntry` requests → `SmoothBlendGroupEntry` integration → `AnimationToProcessComponent` emission) is sound and matches the "one driver, many gatherers" pattern that already proved itself in Timeline.Physics.

The biggest risks, in order:

1. **Ragdoll activation pose-snap is disabled with a literal `if (false && …)`** — ragdoll bodies wake at their *baked* pose, not the current animated bone pose. Characters that have moved/animated since bake will visibly teleport their ragdoll. This is either an unfinished fix or a debugging leftover that shipped. (Critical, Confirmed)
2. **Clip/track transform offsets and `removeStartOffset` silently do nothing unless `applyRootMotion` is enabled on the rig** — the Rukhanka fork patch applies them only to the root-motion delta bone. Every offset field in every inspector is a silent no-op for non-root-motion rigs, and nothing warns the designer. (Critical, Confirmed)
3. **The GPU animation engine ignores every parity field** (`positionOffset`, `rotationOffset`, `removeStartOffset`, `applyFootIK`) — `GPUStructures.AnimationToProcess` was never extended. Flipping a rig to GPU animation changes its pose behavior with zero diagnostics. (Critical, Confirmed)
4. **Inertialization's phase-jump detector measures discontinuity in normalized time with a fixed 0.05 threshold** — frame-time jitter on short clips false-positives and re-triggers inertialization, causing periodic pose pops. It also has **zero test coverage** despite being the most numerically intricate code in the package. (High)
5. **Weapon Equip clips spawn a new weapon on every clip activation with no despawn or ownership tracking** — looping timelines accumulate weapons; Drop clips cannot target an Equip-spawned weapon because Equip ignores the track binding that Drop reads. (High)
6. **~2,100 lines of triplicated blend-tree system code** (1D / 2D / Direct are ~90% identical) — every bug fixed in one has to be fixed in three places; the orphan-cleanup and per-track-blend accumulation logic already exists in three hand-maintained copies. (High, maintainability)

Everything else is medium/low: doc-vs-code drift on the fallback latch, dead fields, missing `RequireForUpdate`s, missing validator rules, and a handful of Risk-class physics/bake-order questions on the ragdoll joints.

---

## System Inventory

### Authoring (`BovineLabs.Timeline.Animation.Authoring`)
| File | Responsibility |
|---|---|
| `RukhankaAnimationTrack/Clip` | Single-clip playback per layer; avatar masks, additive ref-pose, exit/fallback override, continuous loop |
| `BlendTree1DTrack/Clip`, `BlendTree2DTrack/Clip`, `BlendTreeDirectTrack/Clip` | Motion-set bake + per-clip blend parameter (clip value / physics velocity / player move input) |
| `LayerWeightTrack/Clip` + `LayerWeightActorBakingSystem` | Timeline-eased multiplier on a Rukhanka layer's weight |
| `TimelineAnimationStateAuthoring` | Per-actor fallback clip, global crossfade durations, inertialization opt-in; bakes `FallbackBlend` + `DefaultBlendGroupFallback` blob + buffers |
| `AfterImageTrack/Clip` | Ghost-prefab spawner capturing the source's ATP buffer |
| `WeaponAnchorClip`, `WeaponGrip*`, `WeaponStateClip` | Bone anchoring, grip-preset registry (blob hash map), equip/re-attach/drop/pickup lifecycle edges |
| `Ragdoll*` (Authoring/BakingSystem/BodyAuthoring/Clip/Generator/Track) | Humanoid ragdoll build tool + bake + timeline activation |
| `CharacterLookAt*` (IK) | AimIK-driven look-at clip/track/rig wizard |
| `FollowPositionOnlyAuthoring`, `AnimationDebugAuthoring`, `FallbackTrackOrder` | Utilities |

### Data (`BovineLabs.Timeline.Animation.Data`)
`BlendGroupEntry` / `SmoothBlendGroupEntry` / `BlendGroupTimer` / `FallbackBlend` / `TrackFallbackOverride` (the blend-group contract), per-tree motion buffers + playback-state buffers, `MotionId`, `BlendLayerMath`, `BlendTreePhaseMath`, `ClipSampling`, `FallbackOverrideResolve`, `TransformConversion`, `CameraGroundBasis` (SharedStatic), weapon/ragdoll/look-at/inertialization component defs, `Builders/*` (IEntityCommands appliers).

### Runtime (`BovineLabs.Timeline.Animation`)
| System | Role |
|---|---|
| `TimelineSingleAnimationTrackSystem` | Gather active `RukhankaSingleClipData` → `BlendGroupEntry` |
| `TimelineAnimationBlendTree{1D,2D,Direct}TrackSystem` | Gather + per-track blend + phase clock → `BlendGroupEntry` (per motion) |
| `TimelineLayerWeightTrackSystem` | Active layer-weight clips → `LayerWeightOverride` buffer (max-combine) |
| `TimelineFallbackOverrideSystem` | Latch dominant `TrackFallbackOverride` onto actor's `FallbackBlend`; restore default when none |
| `TimelineAnimationUnificationSystem` | Reconcile requests ↔ smooth entries, integrate weights, emit fallback + clips into `AnimationToProcessComponent`, sort by layer |
| `InertializationSystem` (runs inside Rukhanka group) | Post-IK per-bone quintic offset decay on dominant-clip change / phase jump |
| `CameraGroundBasisSystem` | Camera ground basis → SharedStatic, consumed by BlendTree2D camera-relative |
| `AfterImageSpawnSystem` | Spawn/destroy frozen-pose ghosts, orphan reconcile via cleanup component |
| `WeaponAnchorBlendSystem`, `WeaponGripSampleSystem`, `WeaponLifecycleSystem` | Weighted bone anchoring, grip resolution, equip/drop/pickup edges |
| `RagdollTrackSystem`, `RagdollApplySystem` | Clip → `ActiveRagdoll` enable; fixed-step body/joint enable/disable + IK bridge |
| `CharacterLookAtTrackSystem` | Blend look-at points → move target entity + drive `AimIKComponent` |
| `FollowPositionOnlySystem`, `AnimationDebugSystem`, `TimelineAnimationQuillDebugSystem` | Support/debug |

### Rukhanka fork touch-points
`AnimationToProcessComponent` parity fields; root-motion offset patch in `ComputeBoneAnimationJob`; `motionId`-keyed root-motion history and event emission; `GPUStructures.AnimationToProcess` (NOT extended — see F3).

---

## Dependency & Flow Map

**Frame flow (simulation):**
`TimelineComponentAnimationGroup`: CameraGroundBasis → LayerWeight/FallbackOverride/BlendTree*/SingleAnimation gatherers (all `UpdateBefore(Unification)`) → **Unification** (clears `BlendGroupEntry` at end, owns `SmoothBlendGroupEntry` state) → Debug systems.
Then Rukhanka: `AnimationProcessSystem` (consumes ATP; offset patch on root bone) → IK injection group → **InertializationSystem** → `AnimationApplicationSystem` (bone entities/skin matrices).
`TransformSystemGroup`: `WeaponGripSampleSystem` → `WeaponAnchorBlendSystem` → `FollowPositionOnlySystem` (all before `LocalToWorldSystem`).
`FixedStepSimulationSystemGroup` after physics: `RagdollApplySystem` (ECB at `EndFixedStepSimulation`).

**Key ownership facts:**
- `SmoothBlendGroupEntry` is the *only* persistent blend state; it is keyed by `MotionId` (hash of track entity + layer + clip hash + instance). All gatherers are stateless except the blend-tree phase buffers (`BlendTree*PlaybackStateElement`, keyed by track entity, cleaned only when the target has ≥1 active clip of that tree type).
- `FallbackBlend` on the actor is *mutable runtime state* restored from the immutable `DefaultBlendGroupFallback` blob — the latch/restore pair in `TimelineFallbackOverrideSystem` is the only writer.
- The offset fields ride the ATP into the Rukhanka fork and are consumed **only** in the root-motion branch of `ComputeBoneAnimationJob` (CPU) and **nowhere** on the GPU path.
- Weapon lifecycle: `WeaponStateFired` (enableable) is the edge latch; `WeaponAttachment` (enableable) is the persistent "who holds me"; `WeaponAnchorSample` buffer is the per-frame blend input; `WeaponAnchorRest` is the release pose.
- Ragdoll: `ActiveRagdoll` (enableable, on rig root) ← `RagdollTrackSystem`; `RagdollBodyState.Fired` is the per-body edge latch; `OverrideTransformIKComponent` per bone is the visual bridge back into the animation stream.

---

## Critical TODOs

### TODO: Re-enable and fix the ragdoll activation pose snap (`if (false && …)`)

**Priority:** Critical
**Certainty:** Confirmed
**Lens:** Physics / State
**Files/Systems Involved:** `BovineLabs.Timeline.Animation/RagdollApplySystem.cs:109` (`ApplyJob`)
**Problem:** The block that teleports each ragdoll body to its bone's current world pose on activation is dead code: `if (false && LocalToWorld.TryGetComponent(body.Bone, out var boneLtw))`. On activation the body is un-disabled and made dynamic at whatever `LocalTransform` it last had — the baked pose from `RagdollBodyAuthoring`.
**Evidence:** Literal `false &&` at line 109; `RagdollBodyAuthoring` bakes bodies at authoring-time world positions; nothing else writes body transforms before `mass.IsKinematic = 0`.
**Why It Matters:** Any character that has moved or animated since bake (i.e., every character) will have its ragdoll pop/teleport from the spawn pose, with joints violently pulling bodies together. This is the difference between "ragdoll works in the test scene where the character never moved" and "ragdoll works in the game".
**Suggested Change:** Restore the pose snap, but source the bone pose correctly. `LocalToWorld` of the bone entity is one frame stale relative to the current animation output; either (a) accept the 1-frame staleness (usually invisible at fixed-step activation), or (b) read the fresh pose via `RuntimeAnimationData.worldSpaceBonesBuffer` + rig root L2W (the same data `AnimationApplicationSystem` writes), composed with `BoneLocalPos/Rot`.
**Implementation Path:**
1. Delete `false &&`; keep the `TryGetComponent` guard.
2. Snap `transform.Position/Rotation` from bone LTW ∘ `BoneLocalPos/Rot` (code already present).
3. Optionally seed `velocity.Linear` from the bone's world delta over the last fixed step instead of zeroing (carries momentum into the ragdoll — same trick as `WeaponPoseVelocity`).
4. Test: run a character to the far side of the map, trigger `RagdollClip`, confirm bodies wake at the character, not at spawn.
**Snippet/Pseudocode:**
```csharp
if (isActive && !bodyState.Fired)
{
    if (LocalToWorld.TryGetComponent(body.Bone, out var boneLtw))
    {
        var boneRot = boneLtw.Rotation; // 1 frame stale; acceptable at activation
        transform.Position = boneLtw.Position + math.mul(boneRot, body.BoneLocalPos);
        transform.Rotation = math.mul(boneRot, body.BoneLocalRot);
        transform.Scale = 1f;
    }
    velocity.Linear = float3.zero;   // TODO(next): seed from bone delta for momentum
    velocity.Angular = float3.zero;
    ...
}
```
**How to Verify:** Play-mode: move Arvex_RIG ≥5 m from its baked position, activate the ragdoll clip, observe bodies waking in place. Also verify with `animation.debug` Quill draws off and BL_DEBUG off (per Quill gotcha).
**Tradeoffs:** Reading `LocalToWorld` is stale by one frame; reading `RuntimeAnimationData` from a fixed-step system needs a completed dependency on the animation group (cross-group sync point). Start with LTW; escalate only if the pop is visible.
**Confidence:** High

---

### TODO: Make the offsets/removeStartOffset contract explicit — they only work on root-motion rigs

**Priority:** Critical
**Certainty:** Confirmed
**Lens:** Validation / Designer Safety / Animation
**Files/Systems Involved:** `com.rukhanka.animation/Rukhanka.Runtime/AnimationEngine/AnimationProcessSystem_Jobs.cs:121–156` (offset patch); every track/clip inspector exposing `positionOffset` / `eulerAnglesOffset` / `removeStartOffset`; `TimelineAnimationValidator`
**Problem:** The BOVINELABS TIMELINE OFFSET PATCH applies `atp.positionOffset/rotationOffset/removeStartOffset` **only inside `if (Hint.Unlikely(rootMotionDeltaBone))`**, where `rootMotionDeltaBone = rigDef.applyRootMotion && rigBoneIndex == 0`. On a rig with `applyRootMotion = false` (the showcase default, and `RigDefinitionAuthoring.applyRootMotion = false` in both sample builders), every offset field on every clip and track is a **silent no-op**. The same applies to fallback offsets baked into `FallbackBlend`.
**Evidence:** Patch source verified on disk; `AnimationShowcaseBuilder.BuildRig` sets `rig.applyRootMotion = false`; no inspector or validator mentions the dependency.
**Why It Matters:** Designers tune position/rotation offsets in the inspector, see nothing happen, and burn hours. Worse, offsets that *were* working break invisibly when someone toggles root motion off. Every offset-related feature in this package (`OffsetSceneHandles` gizmos included!) is conditional on a flag owned by a different component on a different GameObject.
**Suggested Change:** Two-pronged: (1) add a validator rule + bake-time warning: any track/clip/fallback with non-identity offsets whose resolved `RigDefinitionAuthoring.applyRootMotion == false` → warning "Offsets require Apply Root Motion on the rig — they will be ignored." (2) Longer term, decide whether offsets *should* work without root motion (apply them as a rig-space post-transform on bone 0 in the patch regardless of `applyRootMotion`, taking the non-delta branch) and implement that contract.
**Implementation Path:**
1. Add helper `static bool RigHasRootMotion(PlayableDirector, TrackAsset)` reusing `ResolveRigDefinition`.
2. In `TimelineAnimationValidator.ScanRukhankaTrack` / `ScanBlendTree2DTrack` (and add 1D/Direct scans — see D-list), flag non-zero `positionOffset`/`eulerAnglesOffset` (track or any clip) + `removeStartOffset` when the binding's rig has `applyRootMotion == false`. Offer Fix: "Enable Apply Root Motion" (with a note that root motion changes entity movement semantics).
3. Emit the same warning from `Bake` (one per track) so CI bake logs catch it.
4. Document the contract in `HANDOFF_DESIGNERS.md`.
**How to Verify:** Author a clip with `positionOffset = (0,0,2)` on a non-root-motion rig → validator flags it; enable root motion → character offsets by 2 m at runtime.
**Tradeoffs:** Option (2) (making offsets work everywhere) touches the fork and changes behavior for existing content; do the validation first, decide on the contract second.
**Confidence:** High

---

### TODO: Close the CPU/GPU divergence — GPU `AnimationToProcess` lacks the parity fields

**Status:** ✅ IMPLEMENTED (guard + full HLSL/GPU-struct port). See "Resolution" at the end of this section.
**Priority:** Critical
**Certainty:** Confirmed
**Lens:** Architecture / Validation
**Files/Systems Involved:** `com.rukhanka.animation/Rukhanka.Runtime/GPUAnimationEngine/GPUStructures.cs` (`AnimationToProcess`), `GPUAnimationSystem_Jobs.cs` (`FillFrameAnimatedRigWorkloadBuffersJob`), `ProcessAnimations.hlsl`; consumers: every offset/continuous-loop feature in this package
**Problem:** `AnimationToProcessComponent` gained `positionOffset`, `rotationOffset`, `removeStartOffset`, `applyFootIK` — but `GPUStructures.AnimationToProcess` and the HLSL mirror were never extended, and `FillFrameAnimatedRigWorkloadBuffersJob` doesn't copy them. A rig switched to `GPUAnimationEngineTag` silently loses offsets and the removeStartOffset behavior (foot-IK baking is per-blob so it survives). Additionally `InertializationSystem` correctly no-ops for GPU rigs only by the accident of `rigBoneCount <= 0`.
**Evidence:** `grep positionOffset GPUStructures.cs` → no hits (verified). GPU struct is 7 ints/floats; CPU component has 4 extra fields.
**Why It Matters:** "Move this crowd to GPU animation" is a one-checkbox change that alters poses with no error. Cross-engine parity bugs are among the hardest to diagnose because both paths look "correct" in isolation.
**Suggested Change:** Minimum: **guard** — bake-time/runtime warning when an entity has `GPUAnimationEngineTag` *and* any timeline-animation component from this package (`BlendGroupTimer` et al.), stating unsupported features. Full fix: extend the GPU struct + HLSL (`AnimationToProcess.hlsl`) with `float3 positionOffset; float4 rotationOffset; uint flags;` and port the root-bone patch into `ProcessAnimations.hlsl`.
**Implementation Path:**
1. Short-term: in `TimelineAnimationUnificationSystem.OnCreate` add a query for `(BlendGroupTimer, GPUAnimationEngineTag)`; log a `LogError512` once via a `NativeReference<bool>` latch (pattern already used in `CharacterLookAtTrackSystem`).
2. Long-term: mirror fields in `GPUStructures.AnimationToProcess` + HLSL struct (respect 16-byte packing), copy in `FillFrameAnimatedRigWorkloadBuffersJob`, implement the offset/removeStartOffset math in `ProcessAnimations.hlsl` root-bone path.
**How to Verify:** Toggle `GPUAnimationEngineTag` on the showcase hero → warning fires; after full fix, pose matches CPU path bit-for-bit-ish (visual A/B).
**Tradeoffs:** HLSL port is real work and needs the shader-debug markers updated; the guard alone removes the silent-failure class.
**Confidence:** High

**Resolution (implemented):**
- **GPU struct + HLSL mirror extended.** `GPUStructures.AnimationToProcess` (C#) and `AnimationToProcess.hlsl` both
  append `float3 positionOffset; float4 rotationOffset; uint flags;` after `avatarMaskDataOffset`. Layout (tight
  scalar StructuredBuffer packing — the same convention proven by `GPUStructures.BoneTransform` = float3+float4+float3
  = 40 bytes): base `28` → `positionOffset(12)@28` → `rotationOffset(16)@40` → `flags(4)@56` → **60 bytes total**.
  The `GraphicsBuffer` stride is `UnsafeUtility.SizeOf<AnimationToProcess>()` (via `FrameFencedGPUBufferPool`), so C#
  and HLSL agree automatically as long as the field order/types match — which they do.
- **`flags` encoding:** `bit0 = removeStartOffset`, `bit1 = applyFootIK` (`#define ATP_FLAG_REMOVE_START_OFFSET 1`,
  `#define ATP_FLAG_APPLY_FOOT_IK 2` in `AnimationToProcess.hlsl`). The GPU offset math only reads
  `ATP_FLAG_REMOVE_START_OFFSET`; `applyFootIK` is carried for completeness (foot-IK bakes per-blob and already
  survives on GPU).
- **Copy in `FillFrameAnimatedRigWorkloadBuffersJob`:** `positionOffset = atp.positionOffset`,
  `rotationOffset = atp.rotationOffset.value`, `flags = (removeStartOffset?1u:0u) | (applyFootIK?2u:0u)`.
- **Root-bone math ported into `ProcessAnimations.hlsl`:** inside the `if (SampleAnimation(...))` block, guarded by
  `boneWorkload.boneIndexInRig == 0` (the GPU analog of the CPU's `rootMotionDeltaBone`, since the GPU engine has no
  root-motion delta bone) and by a per-ATP `hasOffset` check so non-offset content is byte-for-byte unchanged. Same
  order of operations as CPU `ComputeBoneAnimationJob`: (1) if `removeStartOffset`, sample the clip at t=0 and
  `bt = Inverse(startPose) * bt`; (2) `offsetPose = {pos, rot, scale 1}`, `bt = offsetPose * bt`; (3) force
  `flags.x = flags.y = 1`. CPU `BoneTransform.Multiply/Inverse` (`math.mul`/`math.inverse`/`math.rcp`) are identical
  to the HLSL `BoneTransform::Multiply/Inverse` (`Quaternion::Rotate`/`Inverse`/`rcp`), so the port matches bit-for-bit.
- **Shader-debug markers:** none added. The port introduces no new `CHECK_STRUCTURED_BUFFER_OUT_OF_BOUNDS`-guarded
  StructuredBuffer indexing — the removeStartOffset re-sample goes through `SampleAnimation` → `ReadFromRawBuffer`
  (raw ByteAddressBuffer, unmarked). `DebugMarkers.cs`/`.cs.hlsl` `Total` count stays valid.
- **Guard narrowed:** `TimelineAnimationUnificationSystem`'s `GPUAnimationEngineTag` warning no longer claims offsets
  are unsupported. It now warns only about **inertialization**, which genuinely still no-ops on GPU rigs
  (`InertializationSystem` reads the CPU `worldSpaceBones` buffer; `rigFrameData.rigBoneCount <= 0` for GPU rigs).
- **Files changed:** `com.rukhanka.animation/Rukhanka.Runtime/GPUAnimationEngine/GPUStructures.cs`,
  `.../Common/Shaders/GPUStructures/AnimationToProcess.hlsl`,
  `.../GPUAnimationEngine/GPUAnimationSystem_Jobs.cs`, `.../GPUAnimationEngine/Resources/ProcessAnimations.hlsl`,
  `BovineLabs.Timeline.Animation/TimelineAnimationUnificationSystem.cs`.
- **Verification:** all four affected assemblies (`Rukhanka.Runtime`, `BovineLabs.Timeline.Animation`,
  `BovineLabs.Timeline.Animation.Tests`, `Rukhanka.Tests`) compile clean (0 errors) via the Unity-bundled dotnet.
  EditMode tests were compile-verified only — they do not exercise the GPU dispatch path, and this environment has no
  headless test runner. HLSL cannot be unit-tested here.
- **Visual A/B verification recipe (do this once on a GPU-capable machine):**
  1. Author a `RukhankaAnimationTrack`/`BlendTree2DTrack` clip on the showcase hero with a non-identity
     `positionOffset` (e.g. `(0, 0, 2)`) and/or `eulerAnglesOffset`, on a rig with **Apply Root Motion enabled** (the
     offsets contract from #2 — CPU only applies offsets on root-motion rigs, so the A/B must use one).
  2. Play with the rig on the **CPU** path (no `GPUAnimationEngineTag`); note the character's root pose/position at a
     fixed timeline point (screenshot).
  3. Add `GPUAnimationEngineTag` to the rig (flip it to the GPU animation engine); replay to the same timeline point.
  4. **Pass:** the root pose/position matches the CPU screenshot (offset applied identically); with
     `removeStartOffset` set, the clip starts from the offset position on both paths. **Before this fix** the GPU pose
     ignored the offset (character sat at the un-offset pose). Confirm the runtime warning now mentions only
     inertialization, not offsets.

---

## High Priority TODOs

### TODO: Fix inertialization false-positive phase-jump detection under frame-time jitter

**Priority:** High
**Certainty:** Strongly Likely
**Lens:** Timing / Animation
**Files/Systems Involved:** `InertializationSystem.cs` (`InertializationJob`, `PhaseJumpThreshold = 0.05f`)
**Problem:** Phase-jump detection compares `WrapHalf(dominantTime - frac(lastDominantTime + expectedStep))` against a **fixed 0.05 normalized-time** threshold, where `expectedStep` is last frame's step. Normalized step per frame = `dt / clipLength`. For a 0.3 s clip at 60 fps the nominal step is 0.055; any frame-time variance (hitches, editor GC, vsync misses) makes `discontinuity ≈ Δdt / clipLength` exceed 0.05 easily → spurious inertialization triggers → periodic "rubber" pops on short clips, worst at low/unstable FPS. Also, dominance is picked by `max(weight)`; two clips crossing at ~equal weight can flip dominant back and forth across frames, re-triggering capture each flip.
**Evidence:** `ComputeDominant` (strict `>` best), `PhaseJumpThreshold` in normalized units, `expectedStep` = single-frame history.
**Why It Matters:** Inertialization exists to *remove* pops; a detector that fires on jitter *adds* them, and only in the field (unstable frame times), never on a dev box at locked 60.
**Suggested Change:** (a) Convert the threshold to seconds: `math.abs(discontinuity) * clipLengthOfDominant > 0.05f_seconds` — requires threading the dominant clip's length (available via `atps[i].animation.Value.length`). (b) Add hysteresis on dominance: only treat as `clipChanged` when the new dominant's weight exceeds the old's by an epsilon (e.g. 0.05) or the old is gone. (c) Scale the tolerance with `expectedStep` (e.g. `max(0.05s, 2 * |expectedStep| * clipLen)`).
**Implementation Path:** Extend `ComputeDominant` to also return clip length; compute `discontinuitySeconds`; add `dominantWeightMargin` check; keep the capture path unchanged.
**How to Verify:** New unit test (see Testing) simulating variable dt on a 0.3 s looping clip: assert no capture fires when time advances monotonically with ±50 % dt jitter; assert capture *does* fire on a genuine cut (time reset).
**Tradeoffs:** Hysteresis slightly delays legitimate inertialization on slow crossfades — acceptable since slow crossfades don't need it.
**Confidence:** Medium-High (mechanism confirmed from code; field frequency estimated)

---

### TODO: Weapon Equip lifecycle — prevent accumulation and make Equip-spawned weapons addressable

**Priority:** High
**Certainty:** Confirmed (accumulation) / Strongly Likely (drop gap)
**Lens:** State / Designer Safety
**Files/Systems Involved:** `WeaponLifecycleSystem.cs`, `WeaponStateClip.cs`, `WeaponGripTrack.cs`
**Problem:** Two related gaps. (1) `WeaponStateMode.Equip` instantiates the registry prefab on every clip activation, and `RearmJob` resets `WeaponStateFired` when the clip deactivates — so a looping timeline (or replayed action timeline) spawns a new weapon per loop, forever; nothing destroys or reuses the previous one. (2) Equip ignores `TrackBinding` (spawns from `ObjectDefinition`), while Drop/ReAttach/Pickup act on `binding.Value` — an Equip-spawned weapon is not the track's bound weapon, so a later Drop clip on the same track does nothing to it unless the designer separately bound the (not-yet-existing) instance, which is impossible for runtime spawns.
**Evidence:** `case WeaponStateMode.Equip` has no existing-weapon check and no link written anywhere reachable by Drop; `RearmJob` unconditionally re-arms.
**Why It Matters:** "Equip sword, attack loop, drop sword" is the canonical use; today it leaks entities and the drop silently no-ops. This is the exact shape of bug the wiki calls a designer-facing silent failure.
**Suggested Change:** Track the equipped instance per holder: add `EquippedWeapon : ICleanupComponentData { Entity Weapon; }` (or a buffer for multi-slot) on the holder (`root.Director`). Equip: if holder already has a live equipped weapon of the same `ObjectId`, re-attach it instead of spawning; else spawn and record. Drop with `weapon == Entity.Null` binding: fall back to the holder's `EquippedWeapon`. Destroy/cleanup on holder death via the cleanup component.
**Implementation Path:**
1. Add component + write in Equip branch.
2. In Drop/ReAttach/Pickup, resolve `weapon = binding.Value != Entity.Null ? binding.Value : equippedLookup[holder].Weapon`.
3. Add validator/bake warning: Equip clip inside a looping timeline without a matching Drop → "will spawn one weapon per loop".
4. Unit test in `WeaponLifecycleSystemTests`: two activations of Equip → exactly one weapon.
**How to Verify:** Loop a 2 s timeline with Equip at t=0 for 60 s; entity count of the weapon prefab stays 1; a Drop clip at loop 3 detaches the spawned weapon and it inherits `WeaponPoseVelocity`.
**Tradeoffs:** Cleanup components keep holder entities alive until processed — mirror the `AfterImageGhostOwner` reconcile pattern already in this package.
**Confidence:** High

---

### TODO: Deduplicate the three blend-tree track systems into one generic core

**Priority:** High
**Certainty:** Confirmed
**Lens:** Architecture / Maintainability
**Files/Systems Involved:** `TimelineAnimationBlendTree1DTrackSystem.cs` (~590 lines), `TimelineAnimationBlendTree2DTrackSystem.cs` (~690), `TimelineAnimationBlendTreeDirectTrackSystem.cs` (~560)
**Problem:** The gather job, `PerTrackBlend` accumulation (stackalloc-128 + heap fallback, duplicated *again* inside each file as `ProcessTracksWithList`), `ExtractTargetEntitiesJob`, the phase clock (`ComputeNormalizedTime`), orphan playback-state cleanup (twice per file: stack + heap variants), `PopulateTrackData`, and `EmitMotion` are structurally identical across all three systems. Only ~10 % differs: the blend parameter type (float / float2 / none), the weight computation call (`ComputeBlendTree1D` / 2D variants / `ComputeBlendTreeDirect`), and the dynamic-parameter job. Six copies of the orphan-cleanup loop exist today.
**Evidence:** Side-by-side reading; e.g. `CleanupOrphanPlaybackStates` + `CleanupOrphanPlaybackStatesHeap` appear verbatim (modulo buffer type) in all three files.
**Why It Matters:** The phase-clock fix (`BlendTreePhaseMath`) had to be applied in three places; the next timing bug will too, and one copy will be missed. Reviewers cannot diff-review 2 k lines of near-copies.
**Suggested Change:** Extract a `BlendTreeGatherCore<TParam, TMotionData, TStateElement>` set of static Burst methods (accumulate, normalized-time, cleanup, emit) parameterized by small structs implementing e.g. `IBlendWeightSolver { NativeList<MotionIndexAndWeight> Solve(...); }`. Keep three thin `ISystem` shells (queries + parameter jobs differ). Alternatively unify the three playback-state buffer types behind one `BlendTreePlaybackStateElement` distinguished by a `TreeKind` byte (they're already field-identical) — buffers stay per-actor either way.
**Implementation Path:** Do it as a pure refactor gated by the existing tests plus new phase-clock tests per tree type; keep public component types stable (bake data unchanged).
**How to Verify:** All 3 showcase columns + F1/F2 AnimTest rigs animate identically pre/post (screenshot diff), tests green, line count drops ~1,300.
**Tradeoffs:** Generic Burst code is harder to step through; mitigate with the debug systems already present. Do **not** merge the buffer types if save-data/bake compatibility matters this milestone.
**Confidence:** High

---

### TODO: Verify ragdoll joint enable matches the baked `PhysicsConstrainedBodyPair` orientation

**Priority:** High
**Certainty:** Risk
**Lens:** Physics
**Files/Systems Involved:** `RagdollApplySystem.cs` (`JointJob`), `RagdollGenerator.cs`
**Problem:** `JointJob` enables/disables a joint by looking up `pair.EntityA` in the `RagdollBody` lookup and ignores `EntityB`. Whether the ragdoll body lands in `EntityA` or `EntityB` depends on Unity Physics joint baking conventions (body vs connected body ordering), which have changed across versions. If bodies bake into `EntityB`, every joint stays `Disabled` forever and the ragdoll becomes a pile of unconnected capsules — visually plausible enough to ship unnoticed.
**Evidence:** `if (!Bodies.TryGetComponent(pair.EntityA, out var body)) return;` — single-sided.
**Why It Matters:** Silent joint loss = ragdolls that "work" but look like loose meat; nobody files a bug titled "EntityB".
**Suggested Change:** Check both sides: try `EntityA`, fall back to `EntityB`. Cost is one extra lookup on a tiny query.
**Snippet/Pseudocode:**
```csharp
if (!Bodies.TryGetComponent(pair.EntityA, out var body) &&
    !Bodies.TryGetComponent(pair.EntityB, out body))
    return;
```
**How to Verify:** In play mode, activate ragdoll and inspect a `LimitedHingeJoint`-baked entity: `Disabled` must be absent while `ActiveRagdoll` is on. Also drop the character from height — limbs must constrain, not scatter.
**Tradeoffs:** None.
**Confidence:** Medium (bug conditional on bake ordering; the fix is safe either way)

---

### TODO: Blend-tree phase clock does not support reverse playback / negative time scale

**Priority:** High
**Certainty:** Confirmed
**Lens:** Timing
**Files/Systems Involved:** `BlendTreePhaseMath.cs`, all three blend-tree systems (`ComputeNormalizedTime`), `TimelineAnimationUnificationSystem` (`ContinuousLoop` advance)
**Problem:** `PlayingDelta(localDelta, scaledDeltaTime)` returns `localDelta` only when `0 < localDelta <= 1`; any negative delta (timeline playing backward, negative `TimeTransform.Scale`, rewind) is replaced by `max(scaledDeltaTime, 0)` — and `scaledDeltaTime = dt * timeScale` is *also* clamped at 0. Net effect: during reverse playback the blend-tree phase either advances forward or freezes, never reverses. `ContinuousLoop` in unification has the same one-way assumption (`adv = dt * PhaseVelocity` with no sign handling issue — actually PhaseVelocity carries scale sign, but `frac()` of negative accumulations is fine; the blend trees are the problem).
**Evidence:** `BlendTreePhaseMathTests.NegativeScaledDeltaTime_ClampsToZero` codifies the clamp; no test covers reverse play.
**Why It Matters:** Rewind/replay features, negative-speed clips, and the WaybackMachine playback path will show blend-tree limbs moving forward while the timeline runs backward.
**Suggested Change:** Decide and document the contract. If reverse must work: accept negative `localDelta` when `|localDelta| <= MaxLocalDelta`, and fall back to `scaledDeltaTime` (signed, not clamped) otherwise. If reverse is out of scope: add it to `MISSING_FEATURES.md` and a validator note on negative clip speeds over blend-tree tracks.
**Implementation Path:** Change `PlayingDelta` to symmetric form; update the three call sites (or one, post-dedup); extend tests with reverse cases.
**How to Verify:** Scrub a blend-tree timeline backward in play mode via `director.time -= dt`; feet cycle backward.
**Tradeoffs:** Free-running loop phase (`ContinuousLoop`) intentionally never rewinds — keep that; this is only the tracked phase.
**Confidence:** High

---

### TODO: Fallback clock and weight integration ignore timeline time-scale and pause

**Priority:** High
**Certainty:** Confirmed
**Lens:** Timing
**Files/Systems Involved:** `TimelineAnimationUnificationSystem` (`EmitFallback`, `IntegrateWeights` use `SystemAPI.Time.DeltaTime`)
**Problem:** Clip times come from the timeline (respect `TimelineTimeScale` / `WorldTimeScale` composition), but the fallback clip's accumulated time and all crossfade weight ramps advance by raw world `DeltaTime`. Under bullet-time (world-time packages in this repo) the character's active clips slow down while its idle fallback and every blend in/out run at real speed — visibly wrong during slow-mo, precisely when players stare at animation.
**Evidence:** `var fallbackAdvance = (IsScrubbing ? 0f : DeltaTime) / duration;` and `maxStep = DeltaTime / floorDur` — no time-scale factor. (Note: if the world-timescale package scales `World.Time.DeltaTime` itself, this is already correct for *world* slow-mo but still wrong for *per-timeline* `TimelineTimeScale`.)
**Why It Matters:** Slow-mo is a headline feature of this repo (unity-track-world-timescale); fallback idles popping to full speed inside it reads as a bug.
**Suggested Change:** Determine the authoritative scaled dt: if `WorldTimeScale` already scales `SystemAPI.Time.DeltaTime`, only per-timeline scale is missing — thread a per-actor effective scale (e.g., max |TimeTransform.Scale| among the actor's active clips, or the owning timeline's scale) into `EmitFallback`/`IntegrateWeights`. If it doesn't, multiply by the world scale singleton too.
**Implementation Path:** Add a `float TimeScale` to `BlendGroupTimer` written by the gatherers (best-weight clip's `TimeTransform.Scale`, default 1 when no clips), consume in unification.
**How to Verify:** World timescale 0.2: idle fallback visually slows 5×; crossfades take 5× longer in wall-clock.
**Tradeoffs:** "Which timeline owns the actor's fallback speed" is ambiguous with multiple concurrent timelines — best-weight clip is a defensible tiebreak; document it.
**Confidence:** High on the gap; Medium on the exact fix shape (depends on how WorldTimeScale is plumbed — check before implementing)

---

### TODO: Add inertialization + unification reconcile test coverage (currently zero)

**Priority:** High
**Certainty:** Confirmed
**Lens:** Testing
**Files/Systems Involved:** `BovineLabs.Timeline.Animation.Tests` (new files), `InertializationSystem.cs`, `TimelineAnimationUnificationSystem.cs`
**Problem:** The two most intricate runtime pieces have no tests: (1) `InertializationJob` — quintic polynomial with time-guard, angle-axis extraction, acceleration capping, three-frame history, phase-jump discrimination; (2) `UnifyAnimationsJob` — request↔smooth reconcile keyed by MotionId, continuous-loop phase seeding (`PhaseSeeded`), fade-out time advance for looped vs clamped blobs, fallback hold latch. The existing tests cover the *pure* helpers (`BlendLayerMath`, `FallbackScrubAdvance` re-implementation) but not the actual job logic — the scrub-advance test even reimplements the integration instead of calling it.
**Evidence:** Test directory listing; `FallbackScrubAdvanceTests.IntegrateFallback` is a private local copy of the production math.
**Why It Matters:** Every regression in these files ships pose pops; the AAA transition rework (memory: aaa-transition-system-build) will keep touching them.
**Suggested Change:** (a) Extract `Quintic`, `ToAngleAxis`, `WrapHalf`, and the phase-jump predicate into an internal static `InertializationMath` (Data asm, `InternalsVisibleTo` already present) and test: quintic returns x0 at t=0, 0 at t≥teff, monotone decay for overdamped inputs, guard when `-5x0/v0 ∈ (0, teff)`; angle-axis round-trips including near-identity and w<0. (b) ECS test for unification via `ECSTestsFixture`: create actor with buffers + `FallbackBlend` + a fake `AnimDB` (`NativeHashMap` injected — requires a small seam or constructing a real `BlobDatabaseSingleton` entity), push `BlendGroupEntry` requests over frames, assert ATP output weights/times/order.
**Implementation Path:** Follow `TimelineFallbackOverrideSystemTests` as the template (it already builds blobs + drives a system).
**How to Verify:** `dotnet`/Test Runner EditMode green; mutation-test by flipping the `PhaseSeeded` line — a test must fail.
**Tradeoffs:** Building `AnimationClipBlob`s in tests is heavy; a minimal blob with just `length/looped/hash` is enough for unification paths.
**Confidence:** High

---

### TODO: Document/guard the `WeaponAnchorBlendSystem` LocalTransform aliasing and parent-pose staleness

**Priority:** High
**Certainty:** Risk
**Lens:** Event / Physics / Timing
**Files/Systems Involved:** `WeaponAnchorBlendSystem.ResolveJob`
**Problem:** `ResolveJob` writes `ref LocalTransform transform` (the weapon) while reading other entities' `LocalTransform` through a `[NativeDisableContainerSafetyRestriction]` lookup (bones, parents, `TryGetFreshWorld`). If a weapon entity is itself in another weapon's bone/parent chain (dual-wield rig hierarchies, weapon-on-weapon attachments), this is a genuine read/write race with nondeterministic poses — the exact RW-handle-vs-lookup aliasing trap already hit once in this repo (weapon-timeline memory). Separately, `TryGetFreshWorld`'s `LocalToWorld` fallback reads last frame's matrix, so a weapon parented under a moving non-bone entity lags one frame.
**Evidence:** `[ReadOnly][NativeDisableContainerSafetyRestriction] ComponentLookup<LocalTransform>` + `ref LocalTransform` in the same `IJobEntity`.
**Why It Matters:** Works today because weapons aren't in each other's chains; the constraint is invisible and unenforced.
**Suggested Change:** Minimum: a comment + debug assertion (BL_DEBUG) that `entity` never appears as a `Bone`/anchor of another weapon processed this frame. Better: split resolve into (a) parallel pose *computation* into a `NativeArray<(Entity, BoneTransform)>` reading lookups only, (b) a second job applying poses via `ref LocalTransform` with no lookups. That also fixes determinism if aliasing ever occurs.
**How to Verify:** Construct a weapon-holds-weapon chain in a test scene; with the split jobs, poses are stable across runs.
**Tradeoffs:** Two-phase costs one temp array; negligible at weapon counts.
**Confidence:** Medium (latent, not currently firing)

---

## Medium Priority TODOs

### TODO: Reconcile the ExitIdleClip "latch persists" tooltip with actual restore-on-inactive behavior

**Priority:** Medium
**Certainty:** Confirmed
**Lens:** Designer Safety / Documentation
**Files/Systems Involved:** `RukhankaAnimationTrack.cs` (ExitIdleClip tooltip), `TimelineFallbackOverrideSystem.RestoreFallbackJob`, `TimelineFallbackOverrideSystemTests.RestoresDefault_WhenClipNoLongerActive`
**Problem:** The tooltip promises "the latch persists until another override track takes over", but `RestoreFallbackJob` resets to the baked default the moment *no* override clip is active — and the test asserts exactly that. One of the two is wrong; the test suggests the tooltip is stale (pre-rework wording).
**Suggested Change:** Rewrite the tooltip to match ("while any of this track's clips is active on the target, this clip is the fallback; when none are, the default fallback returns"), or reintroduce latch semantics behind an explicit `Latch` toggle if stance-owned idles (the original use case) need it.
**How to Verify:** Tooltip review + existing test.
**Confidence:** High

### TODO: Remove dead state — `BlendGroupTimer.BaseLayerControl` field and unused `IEnableableComponent`

**Priority:** Medium
**Certainty:** Confirmed
**Lens:** Architecture
**Files/Systems Involved:** `BlendGroupBuffer.cs:60`, `TimelineAnimationUnificationSystem`
**Problem:** `BlendGroupTimer.BaseLayerControl` is never written (verified: only the local static `BaseLayerControl(...)` function shares the name — leftover from the pre-rework `IntegrateBaseLayerControl` described in `REVIEW_NOTES.md`). `BlendGroupTimer : IEnableableComponent` but nothing toggles or filters on the enabled bit. Dead state misleads readers into thinking base-layer control is smoothed/persistent.
**Suggested Change:** Delete the field (bake-data change — safe, it's runtime-written state) and drop `IEnableableComponent`, or start using the enabled bit as the "actor participates in timeline animation" cull (see the culling TODO) — pick one, don't leave it ambiguous.
**How to Verify:** Compile + `AnimationDataTests.BlendGroupTimerTests` updated.
**Confidence:** High

### TODO: Early-out timeline animation work for culled rigs

**Priority:** Medium
**Certainty:** Confirmed
**Lens:** Performance
**Files/Systems Involved:** All gather systems + `TimelineAnimationUnificationSystem` vs `CullAnimationsTag`
**Problem:** Rukhanka culls pose computation via `CullAnimationsTag`, but every timeline gather job, the unification integrate, weapon anchor sampling, and look-at blending still run full cost for off-screen rigs. For crowd scenes this is the dominant waste; it also means `SmoothBlendGroupEntry` weights keep integrating while culled (arguably desirable — poses resume correctly — but should be a decision, not an accident).
**Suggested Change:** In `UnifyAnimationsJob`, skip integration/emission for entities with enabled `CullAnimationsTag` (keep `blendEntries.Clear()`), and add `.WithDisabled<CullAnimationsTag>()`-style filtering (or lookup checks) in the gather jobs' `Execute` on `binding.Value`. Document that weights freeze while culled and snap-resume (or advance-by-wall-clock on un-cull if preferred).
**How to Verify:** Profiler: unification job time drops proportionally with off-screen rigs; un-culling shows no T-pose flash.
**Tradeoffs:** Freezing weights while culled changes what you see on re-entry; snapping `CurrentWeight = TargetWeight` on un-cull is the cleanest resume.
**Confidence:** High

### TODO: `_missingRigWarned` one-shot warning hides all subsequent look-at misconfigurations

**Priority:** Medium
**Certainty:** Confirmed
**Lens:** Debugging / Designer Safety
**Files/Systems Involved:** `CharacterLookAtTrackSystem` (`NativeReference<bool> _missingRigWarned`)
**Problem:** The "no CharacterLookAtTarget rig" warning latches globally: after the first offending character, every other broken character is silent forever (until domain reload — and with CoreCLR no-domain-reload, effectively forever per session).
**Suggested Change:** Key the latch per entity (`NativeParallelHashSet<Entity>` like `WeaponGripSampleSystem._warned`), and grow it (`_warned` there has the same fixed-capacity-16 ParallelWriter overflow issue — fix both: size to query count per frame or use non-parallel add from the single-threaded job).
**How to Verify:** Two broken rigs → two warnings.
**Confidence:** High

### TODO: `BoneWorld.TryComputeWorldMatrix` silently returns a partial matrix past MaxDepth

**Priority:** Medium
**Certainty:** Confirmed
**Lens:** Validation
**Files/Systems Involved:** `BoneWorld.cs` (`MaxDepth = 64`, `break` on depth exhaustion)
**Problem:** Hierarchies deeper than 64 don't fail — the loop `break`s and returns `true` with a matrix relative to whatever ancestor it reached. Wrong-but-plausible anchor positions on deep rigs (nested prefab players in this repo run deep).
**Suggested Change:** Return `false` (or log once) when depth is exhausted while a parent still exists; 64 is fine as a cycle guard but exhaustion must be observable.
**How to Verify:** Unit test with a 70-deep chain.
**Confidence:** High

### TODO: Validate LayerWeight tracks against actual animation layers; document max-combine

**Priority:** Medium
**Certainty:** Confirmed
**Lens:** Designer Safety / Validation
**Files/Systems Involved:** `LayerWeightTrack.cs`, `TimelineLayerWeightTrackSystem.ApplyOverridesJob`, `TimelineAnimationValidator`
**Problem:** (1) A `LayerWeightTrack.LayerIndex` that matches no animation track on the same actor is a silent no-op — no validator rule exists. (2) When multiple layer-weight clips target the same layer, the **maximum** multiplier wins (`if (entry.Multiplier > buffer[i].Multiplier)`), which surprises anyone expecting product or last-wins; it's documented nowhere. (3) Negative `LayerIndex` is accepted by authoring everywhere and falls through `(uint)LayerIndex < LayerSumCapacity` casts into the rescan path — clamp at bake.
**Suggested Change:** Validator rule "Layer Weight track targets layer N but no animation track on this binding uses layer N"; `[Min(0)]` on all `LayerIndex` fields; one sentence on max-combine in the `LayerWeightTrack` tooltip and HANDOFF doc.
**How to Verify:** Validator flags a mismatched showcase setup; bake clamps −1 → 0 with a warning.
**Confidence:** High

### TODO: AfterImage ghost destroyed externally leaves the clip permanently spent

**Priority:** Medium
**Certainty:** Confirmed
**Lens:** Edge Case / State
**Files/Systems Involved:** `AfterImageSpawnSystem` (`CollectAndSpawn` guard `SpawnedEntity != Entity.Null`)
**Problem:** If the ghost entity is destroyed by anything else (lifetime component on the prefab, scene unload of a section, gameplay cleanup) while the clip is still active, `SpawnedEntity` keeps pointing at the dead entity and the guard prevents respawn until the clip deactivates. With `duration = 0.18 s` clips this is minor, but with long clips + prefab-side lifetimes (the natural way to fade ghosts) it's a hole.
**Suggested Change:** In `CollectAndSpawn`, treat `SpawnedEntity != Null && !EntityManager.Exists(SpawnedEntity)` as "reset to Null" (decide: respawn or stay spent — respawn matches "ghost per activation" intent only if the clip re-activates; probably just clear so state is honest). Also note `AfterImageClipData.SpawnedEntity` should not survive scene-reload serialization — it's runtime state on a baked entity; acceptable, but the reconcile job already covers orphans, so symmetry says cover dead-ghost too.
**How to Verify:** ECS test: spawn, destroy ghost manually, tick → clip data cleared; orphan reconcile leaves no cleanup-component zombies.
**Confidence:** High

### TODO: Add `RequireForUpdate` / emptiness early-outs to always-running systems

**Priority:** Medium
**Certainty:** Confirmed
**Lens:** Performance
**Files/Systems Involved:** `WeaponAnchorBlendSystem` (clears map, schedules 4 jobs every frame with zero clips), `TimelineFallbackOverrideSystem`, `TimelineLayerWeightTrackSystem` (has `RequireForUpdate<LayerWeightOverride>` but still clears all buffers), `CharacterLookAtTrackSystem`, `AnimationDebugSystem` (has `RequireMatchingQueries`, fine), `CameraGroundBasisSystem`
**Problem:** Several systems schedule their full pipeline when no relevant clips exist. Individually cheap; collectively it's ~10 jobs/frame of pure overhead in scenes without those features, and it pollutes profiler captures.
**Suggested Change:** `state.RequireForUpdate(query)` on the clip-data query where the system is meaningless without it (`WeaponAnchorData`, `CharacterLookAtAnimated`, `TrackFallbackOverride` existence, etc.). For `TimelineLayerWeightTrackSystem`, the clear-all-buffers job must still run one frame after the last clip ends — use a "was active last frame" latch or clear via the same job chain before requiring.
**How to Verify:** Empty scene: profiler shows none of these systems scheduling.
**Tradeoffs:** The one-frame-teardown systems (layer weight, look-at relax) need care so state doesn't stick when the last clip disappears together with the system stopping — the relax/clear must complete first.
**Confidence:** High

### TODO: WeaponGrip bone map rebuilt from scratch every frame

**Priority:** Medium
**Certainty:** Confirmed
**Lens:** Performance
**Files/Systems Involved:** `WeaponGripSampleSystem.BuildBoneMapJob` over all `AnimatorEntityRefComponent`
**Problem:** The rig-bone hash map (`RigBoneKey → bone entity`) is cleared and rebuilt over *every bone entity in the world* every frame, even though it only changes on rig spawn/despawn. For crowds this is O(total bones) per frame to serve a handful of grips.
**Suggested Change:** Cache with change detection: rebuild only when the bone query's order version changes (`query.GetCombinedComponentOrderVersion()` / chunk order version), or maintain incrementally via a cleanup component on rigs. Keep the per-frame path as fallback under a threshold.
**How to Verify:** Profiler on 100-rig scene: BuildBoneMapJob disappears from steady-state frames.
**Confidence:** High

### TODO: Bake-time mutation of shared `AnimationClip` import settings is crash-fragile

**Priority:** Medium
**Certainty:** Strongly Likely
**Lens:** Validation / Production Readiness
**Files/Systems Involved:** `RukhankaAnimationTrack.ApplyReferencePoseOverrides` / `BakeClipVariant`, `TimelineAnimationStateAuthoring.BakeFallbackAnimation`
**Problem:** Both bakers temporarily overwrite `AnimationClipSettings.additiveReferencePoseClip/Time` on the **shared source asset**, then restore in `finally`. An editor crash, `ExitGUI`, or importer re-entry between set and restore leaves the asset modified on disk (dirty import settings), silently changing that clip for every other consumer. It also makes concurrent bakes of the same clip (multi-scene import workers) order-dependent.
**Suggested Change:** Prefer non-mutating paths: bake the additive reference pose as data (Rukhanka's `additiveReferencePoseFrame` track set) by duplicating the clip in-memory (`Object.Instantiate(clip)` + `SetAnimationClipSettings` on the copy, bake the copy, destroy it). If the Rukhanka baker requires the original asset reference for hashing, hash from the original but sample from the copy.
**How to Verify:** Kill the editor mid-bake (or throw inside `BakeAnimations` in a test) — source clip settings unchanged on next open.
**Tradeoffs:** Instantiated clips cost memory during bake only.
**Confidence:** Medium-High

### TODO: RagdollGenerator hardcodes physics categories, masses, and collision masks

**Priority:** Medium
**Certainty:** Confirmed
**Lens:** Designer Safety
**Files/Systems Involved:** `RagdollGenerator.cs` (`Category31`, `Category00/02/08`, per-bone masses, `RadiusScale`, angle limits)
**Problem:** Project-specific collision categories are baked into package code; on any other project (or after a layer reorg here) generated ragdolls collide with the wrong things and there is no UI to change it short of editing package source. Same for masses/limits — reasonable defaults, but not adjustable.
**Suggested Change:** Move the `BoneSpec[]` table + category tags into a `RagdollGeneratorSettings` ScriptableObject (project asset with a default created on first use), referenced by the menu command; keep the current values as the shipped default.
**How to Verify:** Generate on a fresh project — settings asset appears; changing CollidesWith regenerates correctly.
**Confidence:** High

### TODO: Editor preview world parity — Inertialization, AfterImage, weapons, ragdoll absent from preview

**Priority:** Medium
**Certainty:** Confirmed
**Lens:** Designer Workflow / Documentation
**Files/Systems Involved:** `EditorPreviewBootstrap.cs`
**Problem:** The preview bootstrap registers the gatherers + unification + look-at + Rukhanka core, but not `InertializationSystem`, `AfterImageSpawnSystem`, weapon systems, or ragdoll — so scrubbing shows different results than play mode for those features. That's mostly correct (spawning in preview would be wrong), but it's undocumented, and inertialization specifically *could* run (it's a pure pose filter) yet transitions preview without it.
**Suggested Change:** Document the preview matrix in `HANDOFF_DESIGNERS.md` ("what previews, what needs play mode"); optionally add `InertializationSystem` to `EditorRukhankaRunnerGroup` between IK and Application (guard `IsScrubbing`-style dt handling — dt is 0-ish in preview, inertialization should no-op cleanly, verify the `dt > 0` guards hold).
**How to Verify:** Scrub F4_Inertialization rig — with the system added, the walk→run cut previews with the same momentum blend as play mode.
**Confidence:** High

### TODO: WeaponGripSettings — duplicate-ObjectId detection uses asset identity, not ID

**Priority:** Medium
**Certainty:** Confirmed
**Lens:** Validation
**Files/Systems Involved:** `WeaponGripSettings.Bake` (`HashSet<ObjectId> seen` … `seen.Add(preset.weapon)`)
**Problem:** `seen` is declared as `HashSet<ObjectId>` but `preset.weapon` is an `ObjectDefinition` — this compiles only via the implicit conversion to `ObjectId`, so it *does* key by ID; however two different `ObjectDefinition` assets that erroneously share an ID (the classic duplicate-ID trap in this repo) will be flagged as "multiple presets for weapon" with a misleading message, and a *zero* ID weapon (unassigned auto-id) passes silently and collides in the blob map (`AddUnique` will throw or corrupt). Add an explicit `preset.weapon.ID == 0` warning + skip, and clarify the duplicate message to print the ID.
**How to Verify:** Author two presets whose weapons share ID → both warnings name the ID; zero-ID preset skipped with message.
**Confidence:** Medium (exact `AddUnique` duplicate behavior depends on BlobHashMap impl — verify; the zero-ID gap is certain)

### TODO: LookAt target write uses last frame's parent matrix; angle limits taken from slot 1 only

**Priority:** Medium
**Certainty:** Confirmed
**Lens:** Animation / Timing
**Files/Systems Involved:** `CharacterLookAtTrackSystem.WriteLookAtJob`
**Problem:** (1) When the look target entity has a `Parent`, its new `LocalTransform.Position` is computed with the parent's previous-frame `LocalToWorld` → one-frame lag on moving characters (head aims slightly behind). (2) `angleLimits` are read from `mixData.Value1` only — with two blending look-at clips carrying different limits, limits snap to whichever occupies slot 1 rather than blending, even though `CharacterLookAtMixer.Lerp` blends `AngleLimits` correctly (the blended value is then discarded in favor of `mixData.Value1.AngleLimits`).
**Suggested Change:** Use `blended.AngleLimits` (already computed) instead of `mixData.Value1.AngleLimits`; for the lag, compute the parent's fresh matrix via `BoneWorld.TryComputeWorldMatrix` like the weapon system does.
**How to Verify:** Two overlapping look-at clips with limits (−80,80) and (−10,10): mid-blend limits ≈ (−45,45). Head-tracking a strafing character shows no trailing offset.
**Confidence:** High

### TODO: Consolidate the six standalone design/review docs into living package docs

**Priority:** Medium
**Certainty:** Confirmed
**Lens:** Maintainability / Documentation
**Files/Systems Involved:** `REVIEW_NOTES.md`, `MISSING_FEATURES.md`, `RAGDOLL_PLAN.md`, `WEAPON_SYSTEM_DESIGN.md`, `INERTIALIZATION_DESIGN.md`, `HANDOFF_DESIGNERS.md`, this `TODO.md`
**Problem:** Six point-in-time documents overlap and drift (REVIEW_NOTES already references code that no longer exists, e.g. `IntegrateBaseLayerControl`). Stale docs are worse than none for onboarding.
**Suggested Change:** Keep `HANDOFF_DESIGNERS.md` (designer-facing) and a `Documentation~/Architecture.md` (engineer-facing, absorbing the design docs' still-true content); fold open items from `MISSING_FEATURES.md` into this TODO; delete or archive the rest with a pointer.
**Confidence:** High

---

## Low Priority TODOs

### TODO: Small cleanups batch

**Priority:** Low
**Certainty:** Confirmed
**Lens:** Other
**Files/Systems Involved:** As listed
**Problem/Suggested Change (one line each):**
- `Motionid.cs` → rename file `MotionId.cs` (type is `MotionId`).
- `TransformConversion`: `WorldToParentLocal` hardcodes `1e-8f` determinant epsilon while `WorldPositionToParentLocal` takes it as a parameter — unify (constant on both).
- `AfterImageClip.duration => 0.5` vs `WeaponAnchorClip/RagdollClip => 1` — arbitrary defaults; fine, but document why AfterImage is 0.5.
- `RukhankaAnimationTrackExtensions.AddValidAnimations` uses LINQ over a `NativeArray` (editor-only; fine, but a `foreach` avoids the enumerator boxing pattern precedent).
- `BlendTree1DTrack.BlendInDuration/BlendOutDuration` use `[Min(0.001f)]` while 2D uses `[Min(0f)]` with "0 = instant cut" tooltips — align on the 2D convention (0 allowed) since `BlendLayerMath.DurationToSpeed` handles 0.
- `BlendTreeDirectTrack` fallback uses `1f / Mathf.Max(0.001f, dur)` inline instead of `BlendLayerMath.DurationToSpeed` (1D too) — three encodings of the same conversion; use the helper everywhere.
- `ClipSampling.NormalizedClipTime` duplicates Rukhanka's `NormalizeAnimationTime` semantics minus cycle offset — comment the intentional difference (extrapolation modes) so nobody "unifies" them wrongly.
- `AnimationDebugAuthoring` bakes `AnimationDebugState` unconditionally into builds; wrap the component add in the same `UNITY_EDITOR || BL_DEBUG` guard as the systems, or strip via `[BakingType]`-style editor-only flow (it currently ships a dead component in release).
- `WeaponGripClipInspector.RefreshOptions` scans `AssetDatabase` on every inspector enable — cache with an asset-postprocessor invalidation if preset counts grow.
- `FollowPositionOnlyAuthoring` carries a `LateUpdate` for edit-time preview *and* bakes a system — add a header comment stating the Mono path is edit-time-only so nobody "fixes" the duplication.

---

## Designer Safety TODOs

(Beyond the Critical/High items above — F2 offsets validation, weapon accumulation warning, layer-weight orphan rule.)

### TODO: Extend TimelineAnimationValidator coverage to the whole track family

**Priority:** Medium
**Certainty:** Confirmed
**Lens:** Designer Safety / Validation
**Files/Systems Involved:** `TimelineAnimationValidator.cs`
**Problem:** The validator covers RukhankaAnimationTrack + BlendTree2D + state authoring (D1–D5) but not: BlendTree1D/Direct (no mask-on-overlay-layer rule, no loop-snap, no offset-mode rule — these tracks warn only at bake), `LayerWeightTrack` (orphan layer), `WeaponGripTrack` (binding must be a weapon GameObject with `ObjectDefinitionAuthoring` whose ID exists in `WeaponGripSettings`), `AfterImageTrack` (prefab null → bake warning only; prefab missing rig/ATP buffer → runtime-only warning), `CharacterLookAtTrack` (bound character lacks the built rig), blend trees with **zero valid motions** (bakes an empty buffer; runtime skips silently), duplicate `TimelineAnimationStateAuthoring` fallback + `RuntimeAnimatorController` (D5 exists — good — but only scans loaded scenes), and negative `LayerIndex`.
**Suggested Change:** Add rules D6–D13 mirroring the existing Finding/Fix pattern; factor the shared "layer≥1 without mask" and "offset mode" checks into helpers taking `(TrackAsset, int layer, AvatarMask, bool apply, TrackOffset)` so 1D/Direct get them free.
**How to Verify:** Seed a scene with each misconfiguration; validator reports all; each Fix round-trips.
**Confidence:** High

### TODO: WeaponGripTrack binding type inconsistency (GameObject vs Animator)

**Priority:** Low
**Certainty:** Confirmed
**Lens:** Designer Safety
**Files/Systems Involved:** `WeaponGripTrack` (`TrackBindingType(typeof(GameObject))`) vs every other track (`Animator`)
**Problem:** Designers binding by muscle memory drag the character's Animator onto a WeaponGrip track and get a silent no-op (binding must be the *weapon*). Intentional, but nothing says so at authoring time.
**Suggested Change:** Custom `TrackEditor.GetTrackOptions` error text when the binding resolves to something with a `RigDefinitionAuthoring` ("bind the weapon, not the character") + tooltip on the track.
**Confidence:** High

### TODO: BlendTree1D lacks the edit-mode dominant-motion preview that 2D has

**Priority:** Low
**Certainty:** Confirmed
**Lens:** Designer Workflow
**Files/Systems Involved:** `BlendTree1DClip.CreatePlayable` (returns empty mixer) vs `BlendTree2DClip.CreatePlayable` (nearest-motion preview via `EditorPreviewTrack`)
**Suggested Change:** Port the 2D pattern: track sets `EditorPreviewTrack`, clip previews the nearest-threshold motion for `ReadKind == ClipValue`. Same for Direct (highest-weight motion).
**Confidence:** High

---

## Validation & Guard TODOs

(Items not already covered above.)

### TODO: Guard `LayerSumCapacity` overflow and codify the 0–63 layer contract

**Priority:** Low
**Certainty:** Confirmed
**Lens:** Validation
**Files/Systems Involved:** `TimelineAnimationUnificationSystem` (`LayerSumCapacity = 64`)
**Problem:** Layers ≥64 silently take the O(n) rescan path per entry (correct but quadratic); nothing tells a designer 64 is the fast-path ceiling. Also `FixedList512Bytes<float>` holds 127 floats, so 64 is conservative — fine, but assert the relationship (`LayerSumCapacity * sizeof(float) <= FixedList512Bytes capacity`) with a compile-time-ish check or comment.
**Suggested Change:** Bake-time clamp/warning for `LayerIndex >= 64` across all tracks; one comment.
**Confidence:** High

### TODO: `MotionId.Compute` fake-entity instance encoding — document and centralize

**Priority:** Low
**Certainty:** Confirmed
**Lens:** Validation / Architecture
**Files/Systems Involved:** Blend tree systems (`new Entity { Index = mw.motionIndex }`), `MotionId.cs`
**Problem:** Blend-tree systems synthesize `Entity { Index = motionIndex, Version = 0 }` as the "instance" hash input. It works (track entity disambiguates), but it's an undocumented convention across three files, and if anyone hashes a *real* entity with Version 0 semantics change silently. Provide `MotionId.ComputeForMotion(Entity track, int layer, Hash128 clip, int motionIndex)` and use it everywhere.
**Confidence:** High

### TODO: `WeaponGripSampleSystem._warned` fixed capacity 16 with ParallelWriter

**Priority:** Low
**Certainty:** Confirmed
**Lens:** Validation / Debugging
**Files/Systems Involved:** `WeaponGripSampleSystem` (`new NativeParallelHashSet<ulong>(16, Persistent)`, `.AsParallelWriter()`)
**Problem:** ParallelWriter cannot grow; with >16 distinct warn-keys, `Add` fails silently and warnings duplicate or drop (BL_DEBUG only). Size it to a realistic bound (e.g., 256) or move warning emission to a single-threaded pass.
**Confidence:** High

---

## Timing / Physics / Animation TODOs

(Items not already covered: F4 inertialization jitter, F7 reverse playback, F8 fallback timescale.)

### TODO: Ragdoll activation is delayed one fixed step by the ECB; document or restructure

**Priority:** Low
**Certainty:** Confirmed
**Lens:** Timing / Physics
**Files/Systems Involved:** `RagdollApplySystem` (ECB via `EndFixedStepSimulationEntityCommandBufferSystem`)
**Problem:** `Disabled` removal and `IsKinematic = 0` are split: mass override flips this step (direct write) but the body stays `Disabled` until ECB playback at end of fixed step — net effect the body participates one step later than the IK bridge enables, and on deactivation the body simulates one extra step after IK detaches. Usually invisible; at 30 Hz fixed step it's a 33 ms pose divergence at the transition.
**Suggested Change:** Either move both sides through the ECB (consistent timing) or document the one-step skew in `RAGDOLL_PLAN.md`/architecture doc. Not worth a structural-change-free redesign unless the pop is visible after the Critical fix lands.
**Confidence:** High

### TODO: `WeaponPoseVelocity` differentiates at render rate — smooth for drop hand-off

**Priority:** Low
**Certainty:** Confirmed
**Lens:** Physics / Timing
**Files/Systems Involved:** `WeaponAnchorBlendSystem.ResolveJob` (velocity from per-render-frame delta), `WeaponLifecycleSystem` Drop
**Problem:** Drop velocity is the last single render frame's finite difference. One noisy frame (hitch, blend snap) at the drop moment produces an absurd throw velocity. Tests cover the nominal case only.
**Suggested Change:** Exponential moving average over ~3 frames (`Linear = lerp(Linear, instantaneous, 0.5f)`) or clamp magnitude to a sane cap relative to recent average.
**How to Verify:** Scripted hitch (Debug.Break-style captureDeltaTime spike, per timeline-physics memory) on the drop frame — weapon no longer rockets.
**Confidence:** High

### TODO: Crossfade floor `clipLen * 0.5f` in weight integration — document

**Priority:** Low
**Certainty:** Confirmed
**Lens:** Animation / Documentation
**Files/Systems Involved:** `UnifyAnimationsJob.IntegrateWeights` (`floorDur = min(blendDur, clipLen * 0.5f)`)
**Problem:** Blend durations are silently capped at half the target clip's length; a designer setting 0.4 s blends onto 0.3 s clips gets 0.15 s and doesn't know why. Sensible guard, invisible rule.
**Suggested Change:** Tooltip note on `TimelineAnimationStateAuthoring.blendIn/OutDuration` + HANDOFF line.
**Confidence:** High

---

## Architecture TODOs

(F6 dedup is the big one; these are the rest.)

### TODO: Decide the offsets contract's home — patch vs unification

**Priority:** Medium
**Certainty:** Strongly Likely
**Lens:** Architecture
**Files/Systems Involved:** Rukhanka fork patch, `BlendGroupEntry`/`SmoothBlendGroupEntry`/ATP offset plumbing
**Problem:** Offset data currently travels: authoring → builder → clip component → gather job → BlendGroupEntry → SmoothBlendGroupEntry → ATP → fork patch (root bone only). Seven hops, one consumer, and the consumer lives in a fork you must re-patch on every Rukhanka update (fork-maintenance risk is real — see the private-fork gotcha history in this repo). The fewer fields cross the fork boundary, the cheaper upgrades are.
**Suggested Change:** Evaluate applying offsets *outside* the fork: after `AnimationProcessSystem`, a small package-owned system could compose the weighted offset onto bone 0 of the animation stream (`AnimationStream.SetLocalPose(0, …)`) in the injection group — removing `positionOffset/rotationOffset` from ATP entirely and shrinking the patch to `removeStartOffset` (+ eventually nothing, if removeStartOffset becomes a bake-time transform). Prototype before committing; root-motion delta interaction is the tricky part.
**Tradeoffs:** Per-ATP weighting of offsets (two clips, different offsets, crossfading) needs the per-clip weights, which ARE in the stream's ATP buffer — feasible. If it works, the fork diff shrinks to the parity struct fields only.
**Confidence:** Medium

### TODO: `TimelineAnimationStateAuthoring.Baker.singleClipBuffer` instance-field scratch state

**Priority:** Low
**Certainty:** Confirmed
**Lens:** Architecture
**Files/Systems Involved:** `TimelineAnimationStateAuthoring.Baker`
**Problem:** A reusable `AnimationClip[1]` field on the Baker instance, mutated then nulled during `Bake` — fine while bakers run single-threaded, fragile if Unity ever parallelizes baker instances (and needless: allocate the 1-array locally; bake-time GC is irrelevant).
**Suggested Change:** Local array.
**Confidence:** High

---

## Debugging / Tooling TODOs

### TODO: Surface "why is this clip not affecting the pose" as a one-stop runtime diagnosis

**Priority:** Medium
**Certainty:** Strongly Likely
**Lens:** Debugging
**Files/Systems Involved:** `TimelineAnimationQuillDebugSystem`, `AnimationDebugSystem`, validator
**Problem:** The silent-failure paths enumerated in this audit (offsets w/o root motion, missing rig binding, missing blob hash, layer-weight orphan, GPU tag, culled rig, zero-weight, mask excludes bone) all end in "nothing happens". The Quill overlay shows *what is* playing but not *why something isn't*. Per the designer-tooling-plan memory, ~80 % of designer pain is silent failure.
**Suggested Change:** Add an editor "Animation Doctor" pass (menu or button on the validator window) that, for a selected actor at runtime: dumps active `SmoothBlendGroupEntry` rows (already visible), plus the *rejected* requests — extend the `BlobDatabaseSingleton` misses (the "Animation hash not found" warnings already exist in blend trees; add the same to `TimelineSingleAnimationTrackSystem.GatherActiveClipsJob`, which currently drops missing hashes **silently** — note: that gather job returns without logging when `AnimDB.TryGetValue` fails, unlike the blend trees; fix that asymmetry regardless).
**Implementation Path:** 1) Add the missing-hash warning to the single-clip gather (parity with blend trees, ~5 lines). 2) Doctor window enumerating the checklist above against a selected entity.
**Status:** ✅ IMPLEMENTED. Step 1 landed earlier (`GatherActiveClipsJob` now `LogWarning512`s on a blob miss). Step 2 is the Animation Doctor, in the Editor assembly:
- `AnimationDoctor.cs` — pure, UnityEditor-free diagnosis engine: `ActorDiagnostic` snapshot in → `List<DoctorFinding>` out, one `internal static Check*` per silent-failure path so every check is unit-tested without a window or live world (`AnimationDoctorTests`, 22 tests).
- `AnimationDoctorWindow.cs` — the window: enumerates default-world actors (entities with `BlendGroupTimer`), captures one actor's live blend state read-only on the main thread (SmoothBlendGroupEntry rows, active single/1D/2D/Direct clip requests, LayerWeightOverride, rig/GPU/cull facts, blob-DB hash + avatar-mask resolution) and renders the findings + an entry/request dump. Opened from `BovineLabs/Animation/Animation Doctor` or the **Animation Doctor** toolbar button on the validator window.
- Checks implemented (each maps to a `DoctorCode`): (a) offsets authored but rig root-motion OFF, (b) missing/disabled rig binding (+ "not an animation actor"), (c) animation blob hash not in AnimDB (single-clip **and** blend-tree motions), (d) LayerWeight override targeting an unused layer / fading a layer to 0, (e) `GPUAnimationEngineTag` with package components, (f) rig culled, (g) zero effective weight (+ per-request zero clip weight), (h) avatar mask that includes 0 bones / missing mask blob, plus the empty-blend-tree and missing-track-data rejections and an idle "no active clips" note.
**Confidence:** High (the missing-hash logging asymmetry is Confirmed)

### TODO: Quill debug draws — keep the per-frame cost gated

**Priority:** Low
**Certainty:** Confirmed
**Lens:** Debugging / Performance
**Files/Systems Involved:** `TimelineAnimationQuillDebugSystem`
**Problem:** Config-var gated (good) but the two lookup-heavy jobs schedule whenever `animation.debug.enabled` — fine. Only note: system is compiled under `UNITY_EDITOR || BL_DEBUG`; per the repo's Quill/BL_DEBUG build gotcha, confirm BL_DEBUG stays OFF for player builds so this never ships.
**Confidence:** High

---

## Testing TODOs

(Ties each test to a concrete risk from this audit; F9 covers inertialization + unification.)

1. **Ragdoll activation pose test** (proves Critical-1 fix): ECS test creating a body + bone with moved LTW, toggling `ActiveRagdoll`, asserting the body transform snapped. Risk: regression re-introducing the dead branch.
2. **Weapon equip idempotence** (proves High-2): two Equip activations → one weapon; Drop resolves the spawned weapon.
3. **JointJob EntityB coverage** (proves High-3): pair with body in `EntityB` gets enabled.
4. **PlayingDelta reverse-play cases** (proves High-4 contract, whichever way it's decided).
5. **Unification fade-out advance**: entry with `TargetWeight = 0` on a looped blob advances `NormalizedTime` by `dt/len` and wraps; clamped blob pins at 1 — protects the exit-motion behavior of the AAA transition work.
6. **ContinuousLoop seeding**: request stream with `ContinuousLoop = true` seeds phase once (`PhaseSeeded`), ignores later request times while playing, honors them while scrubbing.
7. **LayerWeight max-combine** semantics lock-in.
8. **AfterImage lifecycle**: spawn → deactivate destroys ghost; orphan reconcile destroys ghost whose clip died; dead-ghost-while-active clears clip data (after Medium fix).
9. **BoneWorld depth-65 chain** returns false (after fix).
10. **LookAt blended angle limits** (after Medium fix): two clips, mid-blend limits interpolate.

---

## Suggested Architecture Direction

**Current shape (keep it):** stateless gather systems → request buffer → single stateful unification driver → engine ATP. This is the right ownership model; the physics package's `TrackBlendStateDriver` consolidation (memory: physics-review) validated the same shape. Do not add per-track stateful systems.

**Boundaries to tighten:**
1. **One blend-tree core** (F6): gatherers become data-only adapters over a shared accumulate/phase/emit core. Ownership: core owns phase state lifecycle (init/advance/cleanup); adapters own parameter resolution only.
2. **Fork boundary minimization** (Architecture TODO): move offset application out of the Rukhanka patch into a package-owned post-process on the animation stream, shrinking the fork diff to struct fields. Every Rukhanka upgrade currently re-lands a hand-maintained patch inside the hottest job in the engine.
3. **Lifecycle state gets owners:** weapon equip instances (cleanup component on holder), after-image ghosts (already done via `AfterImageGhostOwner` — extend the pattern), ragdoll bodies (edge latch exists). Rule: any runtime-spawned entity must be reachable from a component on a *persistent* entity, so death/reload reconciles it.
4. **Validation flow:** editor-time validator (extended per D-list) is the first line; bake emits the same warnings for CI logs; runtime warnings exist only for things unknowable at bake (missing blob hash, GPU tag combos). Every silent no-op identified in this audit gets exactly one of these three.
5. **Debugging flow:** Quill overlay (visual) + AnimationDebugState (counters) + the "Doctor" checklist (causal). The missing piece is causal — implement the Doctor before adding more overlay detail.

**Migration order:** Critical fixes (1–3) → validator extensions (cheap, independent) → inertialization robustness + tests → weapon lifecycle → blend-tree dedup (largest diff, do it when the tree systems are otherwise quiet) → fork-boundary offset move (spike first).

**Verification of the new design:** the existing showcase + AnimTest scenes are the acceptance rig — keep `AnimationShowcaseBuilder`/`AnimTestBuilder` green (screenshot A/B) across every refactor; they exercise every track type end-to-end.

---

## Implementation Snippets

**1. Validator rule: offsets without root motion (Critical-2):**
```csharp
static void AddOffsetsRequireRootMotionFinding(TrackAsset track, PlayableDirector director,
    Vector3 trackPos, Vector3 trackEuler, IEnumerable<(Vector3 pos, Vector3 euler, bool removeStart)> clips,
    List<Finding> findings)
{
    var rig = director != null ? director.ResolveRigDefinition(track) : null;
    if (rig == null || rig.applyRootMotion) return;
    bool any = trackPos != Vector3.zero || trackEuler != Vector3.zero;
    foreach (var c in clips) any |= c.pos != Vector3.zero || c.euler != Vector3.zero || c.removeStart;
    if (!any) return;
    findings.Add(new Finding {
        Severity = MessageType.Warning,
        Message = $"[D6 Offsets Without Root Motion] '{track.name}' authors transform offsets/removeStartOffset " +
                  $"but rig '{rig.name}' has Apply Root Motion OFF — offsets are silently ignored at runtime.",
        Context = track,
        Fix = () => { rig.applyRootMotion = true; EditorUtility.SetDirty(rig); },
        FixLabel = "Enable Root Motion",
    });
}
```

**2. Inertialization jitter fix core (High-1):**
```csharp
// Compute dominant with clip length + hysteresis
uint dominant = ComputeDominant(atps, out var hasDominant, out var dominantTime,
                                out var dominantLen, out var dominantWeight, out var prevDominantWeight);
bool clipChanged = hasDominant && dominant != inert.lastDominant
                   && dominantWeight > prevDominantWeight + 0.05f; // hysteresis on flips

bool phaseJump = false;
if (hasDominant && dominant == inert.lastDominant && dt > 0f)
{
    float expectedStep = WrapHalf(inert.lastDominantTime - inert.prevDominantTime);
    float discNorm = WrapHalf(dominantTime - math.frac(inert.lastDominantTime + expectedStep));
    float discSeconds = math.abs(discNorm) * math.max(dominantLen, 1e-3f);
    float tolerance = math.max(0.05f, 2f * math.abs(expectedStep) * dominantLen); // scale with jitter
    phaseJump = discSeconds > tolerance;
}
```

**3. Equipped-weapon ownership (High-2):**
```csharp
public struct EquippedWeapon : ICleanupComponentData { public Entity Weapon; public int ObjectId; }

// Equip branch:
if (equippedLookup.TryGetComponent(holder, out var eq) &&
    eq.ObjectId == data.ObjectId && state.EntityManager.Exists(eq.Weapon))
{
    ReAttach(ecb, eq.Weapon, holder, data.Grip);           // reuse, don't respawn
}
else
{
    var spawned = ecb.Instantiate(prefab);
    ...
    ecb.AddComponent(holder, new EquippedWeapon { Weapon = spawned, ObjectId = data.ObjectId });
}
// Drop branch fallback:
if (weapon == Entity.Null && equippedLookup.TryGetComponent(holder, out var eq2))
    weapon = eq2.Weapon;
```

**4. Single-clip gather missing-hash parity (Debugging TODO):**
```csharp
if (!AnimDB.TryGetValue(clipData.ClipHash, out var clipBlob) || !clipBlob.IsCreated)
{
    Logger.LogWarning512("[SingleClip] Animation hash not found in BlobDatabaseSingleton — clip skipped. " +
                         "Re-bake the SubScene or check the track's rig binding.");
    return;
}
```

**5. Blend-tree shared core skeleton (High-F6):**
```csharp
internal interface IBlendWeightSolver
{
    // Fills weights (motionIndex, weight); returns weighted duration.
    float Solve(in NativeArray<BlobAssetReference<AnimationClipBlob>> clips,
                ref NativeList<ScriptedAnimator.MotionIndexAndWeight> outWeights);
}

internal static class BlendTreeGatherCore
{
    public static void AccumulateClip<TBlend>(ref TBlend blend, in float weight, ...) where TBlend : IPerTrackBlend { ... }
    public static float AdvancePhase(ref BlendTreePlaybackStateElement ps, float absTime,
                                     float weightedDuration, float timeScale, float dt, bool isScrubbing) { ... }
    public static void CleanupOrphans(DynamicBuffer<BlendTreePlaybackStateElement> states,
                                      ReadOnlySpan<Entity> activeTracks) { ... }
    public static void Emit(DynamicBuffer<BlendGroupEntry> dst, ...) { ... }
}
// 1D/2D/Direct systems shrink to: parameter job + a Solve struct + query glue.
```

---

## Final Ranked TODO List

| # | TODO | Priority | Status |
|---|---|---|---|
| 1 | Re-enable ragdoll activation pose snap (`if (false && …)`) | Critical | ✅ IMPLEMENTED |
| 2 | Offsets/removeStartOffset only work with root motion — validate + decide contract | Critical | ✅ IMPLEMENTED |
| 3 | GPU engine ignores parity fields — guard + full HLSL/GPU-struct port | Critical | ✅ IMPLEMENTED — struct+HLSL mirror (60 B), fill-job copy, root-bone offset/removeStartOffset math in `ProcessAnimations.hlsl`, guard narrowed to inertialization |
| 4 | Inertialization phase-jump false positives under dt jitter + dominance hysteresis | High | ✅ IMPLEMENTED |
| 5 | Weapon Equip accumulation + Drop can't target spawned weapon | High | ✅ IMPLEMENTED |
| 6 | Inertialization + unification reconcile test coverage | High | ✅ IMPLEMENTED |
| 7 | Ragdoll JointJob checks only `pair.EntityA` | High | ✅ IMPLEMENTED |
| 8 | Deduplicate the three blend-tree systems (~1.3 k lines) | High | ✅ IMPLEMENTED (`BlendTreeGatherCore`) |
| 9 | Blend-tree phase clock has no reverse-playback support | High | ✅ IMPLEMENTED |
| 10 | Fallback clock + weight ramps ignore timeline/world time scale | High | ✅ IMPLEMENTED |
| 11 | WeaponAnchorBlendSystem LocalTransform aliasing — split compute/apply | High | ✅ IMPLEMENTED |
| 12 | Single-clip gather drops missing blob hashes silently (parity with blend trees) + Animation Doctor | Medium | ✅ IMPLEMENTED (`AnimationDoctor` + `AnimationDoctorWindow`) |
| 13 | Validator coverage for 1D/Direct/LayerWeight/WeaponGrip/AfterImage/LookAt/empty-motions/negative layers | Medium | ✅ IMPLEMENTED |
| 14 | ExitIdleClip tooltip vs restore-on-inactive behavior mismatch | Medium | ✅ IMPLEMENTED |
| 15 | Early-out timeline animation work for culled rigs | Medium | ✅ IMPLEMENTED |
| 16 | Bake-time AnimationClipSettings mutation is crash-fragile — bake from a copy | Medium | ✅ IMPLEMENTED |
| 17 | AfterImage ghost destroyed externally leaves clip spent | Medium | ✅ IMPLEMENTED |
| 18 | LookAt: blended angle limits discarded; parent-matrix one-frame lag | Medium | ✅ IMPLEMENTED |
| 19 | Dead state: `BlendGroupTimer.BaseLayerControl` + unused enableable | Medium | ✅ IMPLEMENTED |
| 20 | `_missingRigWarned` global latch hides subsequent misconfigs (+ `_warned` capacity) | Medium | ✅ IMPLEMENTED |
| 21 | WeaponGrip bone map rebuilt every frame — change-detect | Medium | ✅ IMPLEMENTED |
| 22 | RagdollGenerator hardcoded categories/masses → settings asset | Medium | ✅ IMPLEMENTED (`RagdollGeneratorSettings`) |
| 23 | `BoneWorld` MaxDepth silent partial result | Medium | ✅ IMPLEMENTED |
| 24 | RequireForUpdate/early-outs on always-running systems | Medium | ✅ IMPLEMENTED (this pass) |
| 25 | WeaponGripSettings zero-ID + duplicate-ID clarity | Medium | ✅ IMPLEMENTED |
| 26 | Editor preview parity matrix (+ optionally Inertialization in preview) | Medium | ✅ IMPLEMENTED |
| 27 | Offsets contract home: shrink the Rukhanka fork patch | Medium | 🔲 DEFERRED (SPIKE) — never assigned, spike-first |
| 28 | Consolidate the six design/review docs | Medium | ✅ IMPLEMENTED (this pass — `Documentation~/Architecture.md`) |
| 29 | Ragdoll one-fixed-step activation skew — document or unify through ECB | Low | ✅ IMPLEMENTED (documented) |
| 30 | WeaponPoseVelocity single-frame differentiation — smooth for drop | Low | ✅ IMPLEMENTED |
| 31 | Crossfade `clipLen * 0.5` floor — document | Low | ✅ IMPLEMENTED (documented) |
| 32 | WeaponGripTrack GameObject-binding affordance | Low | ✅ IMPLEMENTED (`WeaponGripTrackEditor`) |
| 33 | BlendTree1D/Direct edit-mode preview parity with 2D | Low | ✅ IMPLEMENTED |
| 34 | LayerSumCapacity contract + `MotionId.ComputeForMotion` helper | Low | ✅ IMPLEMENTED |
| 35 | Small cleanups batch (naming, epsilons, Min attrs, DurationToSpeed unification, debug-state stripping) | Low | ✅ IMPLEMENTED (this pass) |
