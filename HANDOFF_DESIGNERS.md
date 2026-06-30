# Designer Handoff — Rukhanka Animation Timeline (DOTS)

Moving from Unity's built-in **Timeline > Animation Track** to this DOTS/Rukhanka track family. Read this first — your mental model changes.

## 1. It's asset-playback, not recording
There is **no Record button, no property keying, no infinite clip**. You drop a `.anim` clip onto a `RukhankaAnimationTrack` and it plays on a Rukhanka-rigged actor. That's it.
For **non-skeletal property animation** (move/rotate/scale a prop, drive UI, change a stat) use the sibling DOTS tracks instead: **Transform**, **Essence**, **UI** timeline tracks. Don't try to animate transforms or values inside an Animation Track.

## 2. Editing a clip
Edit the source **`.anim` in the Project window**, then **reload / re-bake the SubScene** for the change to take effect.
⚠️ Gotcha: programmatic clip edits do **not** re-bake an already-open SubScene. After editing, close+reopen (or re-import) the SubScene — otherwise you're watching stale baked data.

## 3. Layering (no "Add Override Track")
There is no right-click **Add Override Track**. To layer:
1. Add another **parallel `RukhankaAnimationTrack`**.
2. Set its **LayerIndex** (higher index overrides lower).
3. Assign an **Avatar Mask** so it only affects the bones you want.

## 4. NEW — Override vs Additive blend mode
The track now has an **Override / Additive** blend-mode toggle. Override replaces the lower layer's pose; Additive adds on top (e.g. a lean/breathe layer over locomotion).
- **Additive Reference Pose (on the clip):** for Additive layers, pick the clip + time that defines the "zero pose" the additive motion is measured against (e.g. the exhale frame of a breathing cycle). Leave empty to use the clip's import default. Only matters when the track is Additive.

## 5. Track offsets
Only the **"Apply Transform Offsets"** offset mode is supported. Selecting any other offset mode logs a warning at bake and is ignored.

## 6. Blends & easing
You have **three** ways to control transitions — pick per situation:
1. **Per-clip ease / clip overlap** — drag a clip's ease-in/out handles, or overlap two clips on one timeline to crossfade. This is the exact Unity-native behavior and is faithful (not distorted).
2. **Global crossfade** (`blendIn/blendOutDuration` on the actor, default **0.2s**) — smooths *every* transition, including switches **between two separate timelines** (trackA ends → trackB starts), where per-clip ease alone can't crossfade. Set 0 for hard cuts; raise for softer.
3. **Inertialization** (see §7) — momentum-preserving cuts; the best feel for combat. Off by default.

Zero ease + zero global = an instant hard cut (Unity parity), which was impossible before.

## 7. Extras you didn't have in Unity Timeline
Blend-tree tracks (Rukhanka does the math; you author thresholds/weights):
- **BlendTree1D track** — NEW. Walk→run / turn blending from one parameter (speed, input magnitude, or a static value). Motions get a single threshold each.
- **BlendTree2D track** — 2D locomotion blend (all three Mecanim algorithms: Simple Directional, Freeform Directional, Freeform Cartesian) driven by clip/velocity/move-input.
- **BlendTreeDirect track** — NEW. Explicit per-motion weights (no spatial algorithm); good for manual/additive/facial mixes. Optional normalize.

Layer & transition:
- **LayerWeight track** — NEW. Animate a whole layer's overall weight over time (fade an additive upper-body/overlay layer in/out) using the clip's ease handles. Meant for overlay layers (LayerIndex ≥ 1).
- **Inertialization** — NEW (opt-in; `inertializationDuration` on the actor, 0 = off, try 0.1–0.3s). Momentum-preserving transitions: on a dominant-clip change it cuts to the new clip and decays a per-bone offset to zero, carrying the previous motion's momentum across the cut (no mush, no foot slide). Best paired with low/zero global blend. **Needs in-editor tuning** — the duration is a feel value.

Pose/IK:
- **CharacterLookAt / AimIK track** — head/eye aim from the timeline.
- **WeaponAnchor track** — weighted bone attachment (stick a sword to the hand for a clip).
- **AfterImage track** — pose-ghost / motion-blur trail.

## 8. Known rough edges (still present)
- **Per-clip blend smoothing doesn't honor zero-ease snap** the way the global path does. `RukhankaAnimationTrack` and `BlendTree2DTrack` still clamp their own per-clip BlendIn/BlendOutDuration to ~0.001s, so a per-clip zero there won't snap instantly. Use the per-clip *ease* (Section 6) for instant cuts.
- **Rig-level default fallback is always Override.** The Additive toggle (Section 4) applies to track layers; the idle/fallback pose played when no clips are active can't yet be authored as additive.
- **BlendTree2D has no edit-mode preview** — those clips scrub to **bind pose** in the editor. Trust runtime, not the scrub.
- **WeaponAnchor / LookAt need a re-bake.** They now depend on extra baked components, so previously-imported SubScenes must re-bake (happens automatically on scene load) before anchoring/look-at resume.
- **No scene-view offset handles.** Clip/track position+rotation offsets are typed numbers only — there is no draggable gizmo (the edit-mode preview doesn't apply offsets, so a handle would lie). Author offsets numerically and verify at runtime.
- **AfterImageClip has no custom inspector** (it has no tunable fields, by design).

*Status:* all features compile green (8 assemblies, dotnet). **Live in-editor bake/play is still owed** — especially inertialization duration tuning and BlendTree1D/Direct + LayerWeight on a real rig. Sanity-check in-editor before a milestone. Note: the Animation package **and** the `com.rukhanka.animation` fork both have uncommitted changes (1D needed Rukhanka's `ComputeBlendTree1D` made public + a `ComputeBlendTreeDirect` entry point; additive-ref-pose needed the bake-hash fix) — commit/push both forks.
