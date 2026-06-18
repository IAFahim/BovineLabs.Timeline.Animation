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

## Architecture — "kinematic-follow, then go dynamic" (standard active ragdoll)
A parallel ragdoll skeleton of physics capsules shadows the animated rig; a two-way bridge switches who drives whom:

- **Ragdoll OFF (default):** physics bodies = **Kinematic**, each frame driven to match its animated bone's
  world pose (so they sit exactly on the visual bones, ready to go). `OverrideTransformIK` = **disabled**
  (animation drives the visual bones).
- **Ragdoll ON (clip active):** bodies flip **Dynamic** (`PhysicsMassOverride.IsKinematic=0`) → simulate under
  gravity + joint limits. `OverrideTransformIK` = **enabled** → visual bones follow the simulating bodies.
- Because the bodies tracked the animation right up to the switch, the transition is seamless. (Velocity
  inheritance for momentum is a Phase-3 refinement; v1 starts from rest or approximates from pose delta.)

Ordering: kinematic-follow runs after `AnimationApplicationSystem`, before `PhysicsSystemGroup`;
`OverrideTransformIK` reads post-sim body `LocalToWorld` next frame (1 fixed-step latency, acceptable).

## Components & where they live (mirrors the 6-assembly layout)
- `*.Data`: `RagdollData{bool Enable}`, `RagdollAnimated : IAnimatedComponent<RagdollData>`,
  `ActiveRagdoll : IComponentData, IEnableableComponent`, `RagdollState` (restore), plus a
  `RagdollBodyLink { Entity Bone; Entity Body }` buffer/component tying each physics body to its rig bone.
- `*.Authoring`: `RagdollAuthoring` (the generator — see Phase 2), `RagdollTrack`/`RagdollClip` (+ builder).
- `*`(runtime): `RagdollKinematicFollowSystem` (OFF: bones→bodies), `RagdollTrackSystem` (clip→ActiveRagdoll,
  the while-active edge machine cloned from PhysicsKinematicOverride), `RagdollApplySystem`
  (ActiveRagdoll → set each body's `PhysicsMassOverride.IsKinematic` + enable/disable the bones' `OverrideTransformIK`).

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
