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
