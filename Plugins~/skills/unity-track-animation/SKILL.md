---
name: unity-track-animation
description: "Master of the BovineLabs.Timeline.Animation track family — RukhankaAnimationTrack (layered/masked single-clip playback), BlendTree2DTrack (ECS-driven 2D blend from clip/velocity/move-input), AfterImageTrack (pose-ghost spawner), WeaponAnchorTrack via WeaponAnchorClip (weighted bone attachment) — all bound to a Rukhanka-rigged Animator. The unusual one: clips DRIVE skinned characters, not stage props. Portable to any project containing the package; worked example from vex-ee. Use when a designer asks \"play this animation / blend locomotion / leave a motion-blur ghost / stick the sword to the hand during this clip\"."
---

# Animation track family specialist

## 1. SCOPE

You own the four tracks of package **`BovineLabs.Timeline.Animation`**, ns
**`BovineLabs.Timeline.Animation.Authoring`** — all `[TrackBindingType(typeof(Animator))]`,
all binding to a **Rukhanka-rigged character** (a `RigDefinitionAuthoring` on the bound
GameObject; see §2 binding). This family is the odd one in the track fleet: its clips animate a
**skinned character**, not move/rotate a stage prop. Designer intents → tracks:

| Designer says | Track | Clip |
|---|---|---|
| "play this animation clip on the character" | `RukhankaAnimationTrack` | `RukhankaAnimationClip` |
| "blend walk/run/strafe by movement / velocity" | `BlendTree2DTrack` | `BlendTree2DClip` |
| "leave a motion-blur ghost / dash afterimage" | `AfterImageTrack` | `AfterImageClip` |
| "stick the weapon to the hand bone now" | `WeaponAnchorTrack`(implicit*) | `WeaponAnchorClip` |

*WeaponAnchor has NO authored Track class — `WeaponAnchorClip` lives on a generic Timeline track
(`TimelineClip`) and binds the WEAPON entity, not the character. Discover the real names in THIS
project (§3); never assume the vex-ee worked example (§5).

Rigs, avatars, animation clips, weapon prefabs, and the stage are OTHER specialists' domains
(protocol §6: report a missing prerequisite, never improvise — except you MAY audit/build the stage
per `unity-stage-foundations`). Operate per **`unity-timeline-track-authoring`** (the SubScene
bracket, discovery preamble, `PRE|` capture, undo-appendix structure, fresh-load verification — do
NOT re-derive any of it here); behave per **`unity-agent-protocol`**; drive the editor per
**`unity-cli`**.

## 2. PORTABLE SEMANTICS

True in ANY project containing `BovineLabs.Timeline.Animation`. (Verified vex-ee 2026-06 from
package source under `Packages/BovineLabs.Timeline.Animation/*.Authoring` and `*.Data`; runtime
systems read; no play mode.)

### THE BINDING TRUTH (family-wide, the #1 trap)
Every track is `[TrackBindingType(typeof(Animator))]`, but the runtime does NOT use the Animator —
`PlayableDirector.ResolveRigDefinition(track)` resolves a **`Rukhanka.Hybrid.RigDefinitionAuthoring`**:
binding is `RigDefinitionAuthoring` directly → use it; binding is `Animator` → `GetComponent<RigDefinitionAuthoring>()`
on the same GO; binding is `GameObject` → same. The `Animator` binding type exists ONLY so the
EDITOR preview graph (the `#if UNITY_EDITOR CreateTrackMixer`/`CreatePlayable` paths) can attach an
`AnimationPlayableOutput`. **Bind the character GameObject/Animator that ALSO carries
`RigDefinitionAuthoring`.** Bind a plain Animator with no rig → bake logs
`'<track>' has no RigDefinitionAuthoring binding — animation data will not be baked` and SILENTLY
emits nothing (Rukhanka/BlendTree2D) or no spawn (AfterImage). A warning, not an error — a clean
error console proves nothing.

### Track-by-track

| Track | Facts |
|---|---|
| `RukhankaAnimationTrack` | `DOTSTrack`, NOT sealed. `[TrackClipType(RukhankaAnimationClip)]`, `[DisplayName("BovineLabs/Animation/Rukhanka Clip")]`. Bakes `RukhankaSingleTrackData{LayerIndex, TrackPositionOffset, TrackRotationOffset, ApplyAvatarMask, AvatarMaskHash}` onto the track entity, bakes all child clips' AnimationClips into a blob DB, optionally an `AvatarMaskBakingData` buffer, optionally a `TrackFallbackOverride` (the ExitIdle latch). |
| `BlendTree2DTrack` | `DOTSTrack`, NOT sealed. `[TrackClipType(BlendTree2DClip)]`, `[DisplayName("BovineLabs/Animation/Blend Tree 2D")]`. Bakes `BlendAnimationTree2DTrackData{BlendTreeType, LayerIndex, offsets, mask}` + a `BlendTree2DMotionData` BUFFER (one entry per `Motions` element → clip hash + 2D position). The MOTION SET lives on the TRACK; the clip only supplies the live blend POINT. |
| `AfterImageTrack` | `DOTSTrack`. `[TrackClipType(AfterImageClip)]`, `[DisplayName("BovineLabs/Animation/After Image")]`. Single field `afterImagePrefab` (GameObject) → baked to `AfterImageTrackData{Entity Prefab}`. The prefab must have its own `RigDefinitionAuthoring` sharing the SAME Avatar as the source rig. |
| WeaponAnchor (no Track class) | `WeaponAnchorClip` is a plain `DOTSClip` placed on a stock Timeline track; the clip binds the WEAPON entity (the thing that gets moved). The weapon prefab needs `WeaponAnchorTargetAuthoring` (adds a `WeaponAnchorSample` buffer). The character is referenced only indirectly, via the clip's `bone` ExposedReference. |

### Clip-by-clip (camelCase serialized field names; set via `SerializedObject`)

| Clip | clipCaps | duration seed | Key fields |
|---|---|---|---|
| `RukhankaAnimationClip` | `Looping\|Extrapolation\|ClipIn\|SpeedMultiplier\|Blending` | the AnimationClip's `length` | `animationClipHolder` (AnimationClip), `positionOffset`, `eulerAnglesOffset`, `removeStartOffset`(true), `applyFootIK`(true) |
| `BlendTree2DClip` | `ClipCaps.All` | (none) | `BlendParameter`(float2), `ReadKind`(enum), `ReadFrom`(EntityLinkSchema), `maxSpeed`(5, min .001), offsets, `removeStartOffset`, `applyFootIK` |
| `AfterImageClip` | `ClipCaps.None` | **20** (hard-coded) | none — empty marker clip; behavior is all on the Track + the spawn system |
| `WeaponAnchorClip` | `Blending\|Looping` | **1** | `bone`(`ExposedReference<Transform>`), `localPosition`, `localRotationEuler` |

### Enums (verified)
- `RukhankaAnimationTrack.trackOffset` : `TrackOffset` (Unity Timeline) — `ApplyTransformOffsets` (the DOTS-deterministic standard) else offsets are zeroed at bake.
- `FallbackPlaybackMode` (ns `BovineLabs.Timeline.Animation`, in `BlendGroupBuffer.cs`): `Loop=0, Clamp=1, Hold=2` — how the ExitIdle/fallback clip wraps.
- `BlendTree2DTrack.BlendTreeType` : `Rukhanka.MotionBlob.Type` — `BlendTree2DSimpleDirectional` (1D-with-center default), `BlendTree2DFreeformCartesian` (true 2D positions), `BlendTree2DFreeformDirectional` (2D polar). Pick by motion layout.
- `BlendTree2DClip.ReadKind` : `BlendDirectionReadKind` (byte) — `ClipValue=0` (the authored `BlendParameter`, static; no `ReadFrom` needed), `PhysicsLinearVelocityNormalized=1` (velocity rotated into facing, ÷`maxSpeed` → radius 0=idle, 1=`maxSpeed`), `PlayerMoveInput=2` (the `PlayerMoveInput` component's stick). For modes 1 & 2 `ReadFrom` (an `EntityLinkSchema`) is REQUIRED — it names which linked entity supplies the velocity/input; bake LOGS AN ERROR and returns (clip emits nothing) if `ReadFrom` is null or its key can't resolve.

### Runtime semantics (what each does once playing)
- **Rukhanka / BlendTree2D**: feed a unified animation pipeline (`TimelineAnimationUnificationSystem`
  → `AnimationProcessSystem`), in `TimelineComponentAnimationGroup`. **LayerIndex** = base full-body
  is 0; each masked region on its own layer ≥1 overrides only its masked bones over lower layers.
  Two clips that must both play at full strength on different body parts (upper+lower, L+R arm)
  **MUST be on different tracks/layers, each with its own AvatarMask** — same layer = they fight.
- **ExitIdle / fallback latch** (Rukhanka + BlendTree2D `ExitIdleClip`, and the standalone
  `TimelineAnimationStateAuthoring.fallbackAnimationClip`): the clip played when NO timeline clip is
  active on the character. A track's `ExitIdleClip` lets a stance track OWN the idle (movement falls
  back to ITS idle, not the default). Highest `LayerIndex` among simultaneously-active overrides
  wins; the latch PERSISTS until another override track takes over.
- **AfterImage**: `AfterImageSpawnSystem` — on clip ENTER (`ClipActive`, `SpawnedEntity==Null`)
  Instantiates `Prefab`, copies the source rig's `LocalToWorld` AND its `AnimationToProcessComponent`
  buffer (so the ghost freezes the source's CURRENT pose), records `SpawnedEntity`. On clip EXIT
  (no `ClipActive`) it DESTROYS the spawned entity and resets. So the ghost lives exactly while the
  clip is active — **the ghost does not animate forward or decay on its own**; for a fading trail
  you spawn a SHORT clip per ghost (or a prefab that self-fades/self-destroys via its own systems,
  e.g. a LifeCycle). The 20s seed duration is just so a freshly-dragged clip is visible.
- **WeaponAnchor**: `WeaponAnchorBlendSystem` (TransformSystemGroup, before LocalToWorld). For each
  active `WeaponAnchorClip`: samples the `bone`'s world transform + the clip's local offset, weighted
  by `ClipWeight` (blend), groups samples per weapon entity, weighted-blends them
  (`AnchorMath.WeightedBlend`) into the weapon's `LocalTransform` (parent-relativized if parented).
  Multiple anchor clips on a weapon BLEND by weight — that's how you cross-fade a weapon from hand to
  back. Weapon needs the `WeaponAnchorSample` buffer (`WeaponAnchorTargetAuthoring`).

### Traps
- **DON'T bind a non-rigged Animator** — the binding type lies (§2 binding truth); no
  `RigDefinitionAuthoring` ⇒ silent no-bake (warning only).
- **AfterImage prefab MUST share the source Avatar** and carry its own `RigDefinitionAuthoring`,
  else the copied `AnimationToProcessComponent` blobs don't map and the ghost is wrong/empty. The
  prefab is a separate authored asset — a missing/mis-rigged prefab is a prerequisite to REPORT.
- **BlendTree2D ReadFrom is mandatory for velocity/input modes** — null/unresolvable link → bake
  ERROR, clip silently empty. `ClipValue` mode is the only self-contained one.
- **BlendTree2D motion set is on the TRACK, not the clip** — editing one clip's blend point doesn't
  change the available motions; add/remove `Motions` entries on the track.
- **Same-layer clips fight; full-strength simultaneous parts need separate layers + masks.**
- **WeaponAnchor binds the WEAPON, not the character** — the clip's `bone` ExposedReference names the
  character bone; the track BINDING is the weapon being moved. Unresolved `bone` → bake ERROR, clip
  anchors nothing.
- **Editor preview vs runtime are different code paths** — the `#if UNITY_EDITOR` mixer paths drive a
  native PlayableGraph for scrubbing; BlendTree2D/AfterImage return empty editor playables (DOTS-only
  content), so "nothing previews in the Timeline window" is NOT a failure for those two.

## 3. DISCOVERY RECIPES

Act only through `unity-cli exec`/`console`; never the filesystem; never play mode; Safe Loop on
every mutation. Names below are PARAMETERS — discover them here, never assume §5.

**3.1 Confirm the package (else MISSING_PREREQUISITE, protocol §6):**
```csharp
var t = System.Type.GetType("BovineLabs.Timeline.Animation.Authoring.RukhankaAnimationTrack, BovineLabs.Timeline.Animation.Authoring");
return t == null ? "MISSING_PREREQUISITE|package BovineLabs.Timeline.Animation absent" : "OK|" + t.AssemblyQualifiedName + "|dataPath=" + UnityEngine.Application.dataPath;
```

**3.2 Scene + SubScene(s):** run the unity-cli skill's First Command. Record `parentScenePath` + each `subScenePath`.

**3.3 PlayableDirector(s)** inside the SubScene (read-only additive open, restore parent): per
`unity-timeline-track-authoring` §discovery. Selection rule: the director in the chosen SubScene
carrying the project's timeline-reference authoring component; else ask the designer.

**3.4 Find the bind target = the RIGGED CHARACTER (by COMPONENT, never by name):**
```csharp
var rigs = UnityEngine.Object.FindObjectsByType<Rukhanka.Hybrid.RigDefinitionAuthoring>(
    UnityEngine.FindObjectsInactive.Include, UnityEngine.FindObjectsSortMode.None);
// print per rig: hierarchy path, scene.path, GetComponent<Animator>()!=null, GetAvatar()?.name,
// sibling TimelineAnimationStateAuthoring? (fallback idle source).
```
ZERO rigs in the SubScene → MISSING_PREREQUISITE (a rig specialist must add one; you bind, you don't
create). For **AfterImage** also find the ghost prefab: an asset/prefab with `RigDefinitionAuthoring`
+ matching Avatar — `AssetDatabase.FindAssets` then verify the Avatar matches the source rig; missing
= report. For **WeaponAnchor**: find the weapon entity (carries `WeaponAnchorTargetAuthoring`) AND
the bone Transform under the character — both must exist. For **BlendTree2D** velocity/input modes:
discover the `EntityLinkSchema` asset (`AssetDatabase.FindAssets("t:EntityLinkSchema")`) for `ReadFrom`
and confirm the source publishes it (per `unity-stage-foundations`/EntityLinks family).

**3.5 Capture pre-state (`PRE|`)** per `unity-timeline-track-authoring` §3.5: `PRE|playableAsset=<path|null>`
and one `PRE|binding|<i>|<track name>|<track type>|<bound hierarchy path + component or null>` per
`GetOutputTracks()`. Journal before any mutation.

## 4. CANONICAL PATTERNS (4 clip patterns)

One logical change per exec block; print `PRE|` before mutating; save inside the block; verify from a
fresh load (§7) — full bracket + save/restore is in `unity-timeline-track-authoring` §recipe; below is
ONLY the family-unique authoring. Bind the COMPONENT (the rigged Animator/GameObject); the baker
coerces to the rig entity. Clip fields are camelCase via `SerializedObject`.

**Pattern A — Play one animation (RukhankaAnimationClip):**
```csharp
var track = timeline.CreateTrack<BovineLabs.Timeline.Animation.Authoring.RukhankaAnimationTrack>(null, trackName);
// track.LayerIndex = 0; (full body)  set avatarMask + LayerIndex>=1 for an upper-body overlay
var so = new UnityEditor.SerializedObject(track);
so.FindProperty("LayerIndex").intValue = 0; so.ApplyModifiedPropertiesWithoutUndo();
var clip = track.CreateClip<BovineLabs.Timeline.Animation.Authoring.RukhankaAnimationClip>();
clip.start = 0; clip.displayName = "<clipName>";
var cso = new UnityEditor.SerializedObject((UnityEngine.Object)clip.asset);
cso.FindProperty("animationClipHolder").objectReferenceValue = /* AnimationClip discovered in §3 */;
cso.ApplyModifiedPropertiesWithoutUndo();
// clip.duration auto = clip.length via the asset's duration override
director.SetGenericBinding(track, /* rigged character Animator/GameObject */);
```
Upper-body-overlay variant: second track, `LayerIndex=1`, assign an AvatarMask isolating the arms;
the two play simultaneously. Stance-owns-idle variant: set the track's `ExitIdleClip` + Blend
durations + `FallbackPlaybackMode`.

**Pattern B — 2D locomotion blend (BlendTree2DTrack/Clip):** put the MOTIONS on the TRACK
(`Motions` list: each entry `clip` + `degreeCalc`(−180..180) + `rangeCalc`; idle at range 0). Set
`BlendTreeType` to `BlendTree2DFreeformCartesian` for true 2D. The clip picks the live point:
`ReadKind=PhysicsLinearVelocityNormalized` + `ReadFrom`=movement-body link + `maxSpeed`=top speed →
the blend follows actual velocity; or `PlayerMoveInput`; or `ClipValue` + `BlendParameter=(x,y)` for a
fixed pose. (Authoring the `Motions` list: build `BlendTree2DTrack.BlendTree2DMotionEntry` instances
and set the serialized `Motions` array — the inspector auto-fills `directionCalc` from degree/range.)

**Pattern C — Dash afterimage / motion-blur ghost (AfterImageTrack/Clip):**
```csharp
var track = timeline.CreateTrack<BovineLabs.Timeline.Animation.Authoring.AfterImageTrack>(null, trackName);
new UnityEditor.SerializedObject(track){}; // set afterImagePrefab:
var so = new UnityEditor.SerializedObject(track);
so.FindProperty("afterImagePrefab").objectReferenceValue = /* ghost prefab (own RigDefinitionAuthoring, same Avatar) */;
so.ApplyModifiedPropertiesWithoutUndo();
var clip = track.CreateClip<BovineLabs.Timeline.Animation.Authoring.AfterImageClip>();
clip.start = 0; clip.duration = 0.15; // SHORT — ghost lives only while the clip is active
director.SetGenericBinding(track, /* the SOURCE rigged character */);
```
For a trail, lay several short clips in series (each = one frozen-pose ghost) or give the prefab its
own fade/lifecycle. The ghost is destroyed the instant its clip exits.

**Pattern D — Stick weapon to a hand bone (WeaponAnchorClip):** clip goes on a stock Timeline track
BOUND TO THE WEAPON entity (which carries `WeaponAnchorTargetAuthoring`). Set the clip's `bone`
ExposedReference to the character's hand bone Transform (resolve via the director's scene reference
table), `localPosition`/`localRotationEuler` for grip fit. Cross-fade hand→back = two overlapping
anchor clips (Blending caps) to two bones; the system weight-blends by `ClipWeight`.

## 5. WORKED EXAMPLE (vex-ee) — rediscover, never assume

Project `/home/i/GitHub/vex-ee` (`dataPath=…/Assets`). Package path
`Packages/BovineLabs.Timeline.Animation/`. Bind target = a character carrying
`Rukhanka.Hybrid.RigDefinitionAuthoring` (discover via §3.4; the package's debug/preview helpers
`AnimationDebugAuthoring`, `TimelineAnimationStateAuthoring`, `RukhankaAnimationClipInspector`
confirm the rig/preview path). BlendTree2D velocity mode reads a movement-body `EntityLinkSchema`
(EntityLinks family) and the `PlayerMoveInput` component (driven via `unity-player-input`). Rig
assets, avatar masks, ghost prefabs, and weapon prefabs are all EXTERNAL Unity assets discovered at
runtime — none are hardcoded here. Stage built by `unity-stage-foundations`.

## 6. UNDO APPENDIX

Runtime effects (spawned ghosts, blended poses) exist only in play mode and are transient by design
(AfterImage self-destroys on exit; poses recompute each frame) — undo scope is the AUTHORING
artifacts. Inventory + ORDER + UNDO-1/2/3/4 templates are exactly the family-standard ones in
`unity-timeline-track-authoring` §undo: (1) created `.playable` (track+clip sub-assets), (2)
possibly-created folder, (3) mutated `director.playableAsset`, (4) added generic binding for the new
track. ORDER: restore the director FIRST (clear bindings, restore captured `playableAsset` by path),
THEN `DeleteAsset` the `.playable`, THEN any other captured scene values (this family edits none on
the character/weapon). UNDO-4 = fresh-load verification (§7).

Family-specific note: AfterImage/WeaponAnchor add NO components to the character or weapon at AUTHOR
time beyond the binding — `WeaponAnchorTargetAuthoring` on the weapon prefab is a PREREQUISITE you
verified, not something you added; do not delete it on undo.

## 7. VERIFICATION PROTOCOL

Per `unity-timeline-track-authoring` §verification, with these family expectations:
1. **Fresh-load asset dump**: load the `.playable`, dump each track/clip. Expect track `DisplayName`
   (e.g. `BovineLabs/Animation/Rukhanka Clip`), and: Rukhanka clip caps
   `Looping|Extrapolation|ClipIn|SpeedMultiplier|Blending`, `animationClipHolder` set, duration ==
   clip length; BlendTree2D caps `All`, `Motions` count on the TRACK > 0, `ReadKind` and (for
   velocity/input modes) `ReadFrom` non-null; AfterImage caps `None`, `afterImagePrefab` set, clip
   duration as authored; WeaponAnchor caps `Blending|Looping`, `bone` ExposedReference resolves.
2. **Binding from a RELOADED SubScene**: `GetGenericBinding` returns the bound COMPONENT verbatim.
   Confirm it is (or has) a `RigDefinitionAuthoring` (Rukhanka/BlendTree2D/AfterImage) — a bare
   Animator with no rig is the silent-no-bake trap. For WeaponAnchor the binding is the WEAPON entity.
3. **Prerequisite checks**: source rig's Avatar == AfterImage prefab's Avatar; BlendTree2D `ReadFrom`
   link is actually published by the source; WeaponAnchor weapon has `WeaponAnchorTargetAuthoring`.
4. **Console**: `unity-cli console --filter` for the bake warnings/errors named in §2 (no-rig
   warning, ReadFrom error, unresolved-bone error) against the project baseline — their ABSENCE is
   the real evidence here, since the data path is silent.
5. **Parent-scene restore** (sceneCount=1, parent loaded/active, not dirty).
