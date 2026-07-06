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

**NEW — the rig-level fallback (idle) pose can now be Additive too.** On `TimelineAnimationStateAuthoring` there's a **Fallback Blending** group: `Fallback Blend Mode` (Override / Additive), `Fallback Layer Index`, and a `Fallback Additive Reference Pose Clip` + time. So the always-on "no clips active" pose is no longer forced to Override — you can ride a breathing/lean overlay under everything.
⚠️ **Foot-gun:** an Additive fallback on **layer 0** adds over the *bind pose* (garbage). Put additive fallbacks on **layer ≥ 1** so they ride on top of a base Override pose. (The validator flags this — see §9.)

## 5. Track offsets
Only the **"Apply Transform Offsets"** offset mode is supported. Selecting any other offset mode logs a warning at bake and is ignored.

⚠️ **Offsets (and Remove Start Offset) require Apply Root Motion on the rig.** Position/rotation offsets and `removeStartOffset` are applied only to the root-motion bone. If the bound `RigDefinitionAuthoring` has **Apply Root Motion OFF** (the showcase default), every offset field — on the track, on a blend-tree clip, and on the `TimelineAnimationStateAuthoring` fallback — is a **silent no-op** at runtime, and the Scene-view offset handles move a pose that will not actually move in game. The Validator flags this as **`[D6 Offsets Without Root Motion]`** with a one-click **Enable Root Motion** fix. Note: enabling root motion changes how the entity itself moves, so treat it as a deliberate choice, not just an offset toggle.

## 6. Blends & easing
You have **three** ways to control transitions — pick per situation:
1. **Per-clip ease / clip overlap** — drag a clip's ease-in/out handles, or overlap two clips on one timeline to crossfade. This is the exact Unity-native behavior and is faithful (not distorted).
2. **Global crossfade** (`blendIn/blendOutDuration` on the actor, default **0.2s**) — smooths *every* transition, including switches **between two separate timelines** (trackA ends → trackB starts), where per-clip ease alone can't crossfade. Set 0 for hard cuts; raise for softer.
3. **Inertialization** (see §7) — momentum-preserving cuts; the best feel for combat. Off by default.

Zero ease + zero global = a **true instant hard cut** (Unity parity). This now snaps consistently on **every** track — `RukhankaAnimationTrack` *and* `BlendTree2DTrack` — so a zero there really is a zero (the old "per-clip zero still smeared ~0.001s" quirk is gone).

⚠️ **Global crossfade is capped at half the target clip's length.** A `blendIn/blendOutDuration` larger than half the clip it blends onto is shortened automatically (e.g. **0.4s onto a 0.3s clip becomes 0.15s**) so the blend always completes inside the clip. If a short clip blends faster than you expected, this floor is why.

## 7. Extras you didn't have in Unity Timeline

Looping:
- **Continuous Loop** — NEW per-clip toggle on `RukhankaAnimationClip`. When ON, the clip loops on its **own free-running phase** (it advances by its own speed and never resets when the timeline wraps), so a looping clip **never snaps at the timeline's wrap point** no matter how long the timeline is — and multiple looping layers with *different* cycle lengths each stay smooth. This **replaces the old workaround** of hand-matching the timeline duration to a whole number of cycles.
  - **Turn it ON** for looping locomotion (walk/run/idle cycles that just need to keep cycling).
  - **Leave it OFF** for one-shot clips that must **scrub/track the timeline exactly** (a clip you scrub in edit mode, or that has to line up frame-for-frame with other tracks).
  - ⚠️ Requires a **re-bake** to take effect (reload / re-import the SubScene).

Blend-tree tracks (Rukhanka does the math; you author thresholds/weights):
- **BlendTree1D track** — NEW. Walk→run / turn blending from one parameter (speed, input magnitude, or a static value). Motions get a single threshold each.
- **BlendTree2D track** — 2D locomotion blend (all three Mecanim algorithms: Simple Directional, Freeform Directional, Freeform Cartesian) driven by clip/velocity/move-input.
- **BlendTreeDirect track** — NEW. Explicit per-motion weights (no spatial algorithm); good for manual/additive/facial mixes. Optional normalize.

Layer & transition:
- **LayerWeight track** — NEW. Animate a whole layer's overall weight over time (fade an additive upper-body/overlay layer in/out) using the clip's ease handles. Meant for overlay layers (LayerIndex ≥ 1). Point its **LayerIndex** at a layer a real animation track actually uses — a mismatch is a silent no-op the Validator flags as **`[D8 Layer Weight Orphan]`**. When **two** LayerWeight clips drive the **same** layer at once, the **maximum** multiplier wins (not product, not last-wins).
- **Inertialization** — NEW (opt-in; `inertializationDuration` on the actor, 0 = off, try 0.1–0.3s). Momentum-preserving transitions: on a dominant-clip change it cuts to the new clip and decays a per-bone offset to zero, carrying the previous motion's momentum across the cut (no mush, no foot slide). Best paired with low/zero global blend. **Needs in-editor tuning** — the duration is a feel value.
  - Now the **full Bollo quintic** — it carries position **and velocity and acceleration** across the cut (not just position + velocity), for a smoother, less "poppy" settle.
  - **Loop-aware:** it also smooths a *raw loop seam* for a looping clip that is **not** using Continuous Loop, and is guarded so it never fires on a clean full-cycle wrap. (If a clip uses Continuous Loop there's no seam to smooth — the two features stack fine.)

Pose/IK:
- **CharacterLookAt / AimIK track** — head/eye aim from the timeline.
- **WeaponAnchor track** — weighted bone attachment (stick a sword to the hand for a clip).
- **AfterImage track** — pose-ghost / motion-blur trail.

## 8. Known rough edges
Several long-standing rough edges are now **fixed** (kept here briefly so the change is obvious):
- ✅ **Zero-ease is now a true instant cut on every track** (was: per-clip zero smeared ~0.001s on `RukhankaAnimationTrack`/`BlendTree2DTrack`). See §6.
- ✅ **The rig-level fallback can now be Additive** (was: fallback always Override). See §4 — mind the layer-0 foot-gun.
- ✅ **Scene-view offset handles exist now** (was: offsets were typed numbers only). See below.

Still worth knowing:
- **Blend-tree edit-mode preview shows the *dominant* motion, not the full blend.** All three blend-tree tracks now preview a single representative motion instead of collapsing to the bind pose: **2D** samples the nearest motion at the blend point (nearest-neighbor), **1D** previews the nearest-threshold motion (for a static Clip Value), **Direct** previews the highest-weight motion. Useful for framing a pose, but **not** the weighted blend — trust the runtime for the real blended result.
- **Scene-view offset handles are an authoring aid, not the source of truth.** Clip/track position+rotation offsets now have **draggable Handles** in the Scene view, and the edit-mode preview honors offsets so the handle sits where the pose actually is. Caveats: the **runtime bake is the final truth** (verify there), and `OnSceneGUI` may **not fire** for a clip selected purely inside the Timeline window — select the track/clip so its inspector is active if the handle doesn't appear.
- **WeaponAnchor / LookAt need a re-bake.** They depend on extra baked components, so previously-imported SubScenes must re-bake (happens automatically on scene load) before anchoring/look-at resume.
- **AfterImageClip has no custom inspector** (it has no tunable fields, by design).

## 10. Weapons — grip presets + equip/drop/pickup (NEW)

The old **WeaponAnchorClip** (hand-typed offset + an ExposedReference to a bone per clip) is replaced by **data-driven grips**. You author a weapon's hold-poses once, then reference them by name from any timeline — no per-clip bone wiring, and the same asset works on every character rig.

### Author a weapon's grips (once per weapon)
1. Create a **Weapon Grip Preset** asset (`Create ▸ BovineLabs ▸ Timeline ▸ Weapon Grip Preset`) next to the weapon's **ObjectDefinition**.
2. Assign that **ObjectDefinition** — this is the blob key; the grip lookup is by weapon object id, so it resolves for any spawned instance of that weapon.
3. Add a **Grip** row per hold-style: give it a **name** (e.g. `OneHand`, `TwoHand`, `Sheath`), pick the **bone** it attaches to, and set **local position/rotation**. Drag the **Scene-view handles** on a previewed character to place it by eye instead of typing numbers.
4. Set **Default Grip** — the fallback used when a clip names a grip this weapon doesn't have.

All presets bake into one **WeaponGripSettings** registry blob (a `SettingsBase`); nothing else to wire.

### Use grips on a timeline
- **WeaponGripClip** (on a **WeaponGripTrack** bound to the weapon): pick a grip from the **dropdown** (populated from every preset). While the clip is active the weapon rides that bone via the same weighted blend pipeline as WeaponAnchor — **overlap two grip clips to crossfade** from one hold to another (one-hand → two-hand) with no snap.

### Lifecycle — WeaponStateClip (equip / re-attach / drop / pickup)
One **edge clip** (fires once when it goes active; length is irrelevant) with a **Mode**:
- **Equip** — spawns the weapon from its **ObjectDefinition** already in-hand at the chosen grip (appears at the designed pose, never an incidental one). Physics simulation is disabled while held.
- **ReAttach** — retargets an already-held weapon to a different grip; the pose change rides the crossfade.
- **Drop** — releases the weapon to physics, handing it the **blended pose's real velocity** so a throw/drop flies believably. No velocity fakery.
- **Pickup** — attaches a world weapon and **eases** it into the grip from wherever it lay ("stylish pickup"), instead of snapping.

⚠️ Notes:
- The weapon needs the anchor-pipeline runtime components; Equip/ReAttach/Pickup add them automatically when missing.
- Grip **names are hashed** (Rukhanka name hash) — a typo silently falls back to the Default Grip (a `BL_DEBUG` warning fires). Keep grip names consistent across a weapon's presets.
- Re-bake the SubScene after editing preset poses, same as any baked data.

## 9. Validate before a milestone
Menu **BovineLabs ▸ Animation ▸ Validate Timelines** opens a window that scans every timeline (in loaded scenes and in the project) plus your `TimelineAnimationStateAuthoring` rigs, and lists authoring foot-guns — each with a **Ping** button and, where a safe automatic fix exists, a **one-click Fix**:
- **Loop-snap risk** — a looping clip whose duration isn't a whole number of cycles will snap at the seam. *Primary fix:* **Enable Continuous Loop** (seam-proof at any duration; requires a re-bake). *Alternative:* snap the clip duration to a whole number of cycles. (BlendTree2D clips have no Continuous Loop, so only the snap fix is offered.)
- **Overlay layer with no Avatar Mask** — a layer ≥ 1 track with no mask overrides the whole body.
- **Additive track / fallback with no reference pose** — the additive pose would be garbage; fix sets the reference pose to the clip itself at time 0 (and the layer-0 additive-fallback foot-gun from §4 is flagged).
- **Unsupported track offset mode** — anything but *Apply Transform Offsets* is ignored by DOTS; fix switches it.
- **Controller + fallback duplicate-bake collision** — a rig that has *both* an Animator controller and a fallback clip that lives inside that controller bakes the clip twice; fix clears one side.

The same warnings also appear **inline** in the track inspectors, so you'll see the common ones without opening the window. Run the validator before any milestone / hand-off and clear it.

**Defaults:** the Sample/showcase rig ships with `inertializationDuration` **0.15** so the demo feels good out of the box; the **library default stays 0** (inertialization is opt-in). **Foot IK stays on by default** (grounded).

## 11. Preview vs Play Mode — what scrubbing actually shows

The edit-mode Timeline preview exercises the **pose-sampling pipeline only** (unification, the gatherers, masks/layers, look-at, offset handles). Anything that **spawns entities** or runs in the **physics / fixed-step** loop is **Play Mode only** — scrubbing will look different from the game for those. When in doubt, verify in Play Mode; the runtime bake is always the final truth.

| Feature | Edit-mode preview | Notes |
|---|---|---|
| Rukhanka single clip | ✅ Previews | Full pose; offsets honored only on a root-motion rig (§5) |
| BlendTree 1D / 2D / Direct | ⚠️ Dominant motion only | Nearest-threshold (1D) / nearest-neighbor (2D) / highest-weight (Direct) — not the weighted blend |
| Masks / layering / layer weight | ✅ Previews | The gather → unification pipeline runs in preview |
| Fallback / Exit-Idle override | ✅ Previews | Restores the default fallback when no clip is active |
| CharacterLookAt / AimIK | ✅ Previews | |
| Transform offset handles | ✅ Previews | Handles + preview honor offsets on root-motion rigs (§5) |
| Inertialization | ❌ Play Mode only | Pure pose filter, but not registered in the preview world — transitions preview *without* the momentum settle |
| AfterImage ghosts | ❌ Play Mode only | Spawns ghost entities — never in preview |
| Weapon equip / grip / drop / pickup | ❌ Play Mode only | Spawns/attaches entities; runs in TransformSystemGroup |
| Ragdoll | ❌ Play Mode only | Physics fixed-step |

*Status:* **Live in-editor verification PASSED** — regression-clean; Continuous Loop, zero-ease instant cut, inertialization (full quintic + loop-aware), and the Validator were all verified in play mode on real rigs. Remaining: designer **visual/feel sign-off**, and **commit/push both forks** — `BovineLabs.Timeline.Animation` **and** `com.rukhanka.animation`.
