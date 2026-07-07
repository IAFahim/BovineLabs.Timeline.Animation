// <copyright file="AnimationDoctor.cs" company="BovineLabs">
//     Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Timeline.Animation.Editor
{
    using System.Collections.Generic;
    using Rukhanka;
    using Unity.Mathematics;
    using Hash128 = Unity.Entities.Hash128;

    /// <summary>Designer-facing severity for a <see cref="AnimationDoctor.DoctorFinding"/>. Mapped to a Unity HelpBox
    /// <c>MessageType</c> by the window; kept UnityEditor-free so the checklist logic is testable without an editor.</summary>
    public enum DoctorSeverity
    {
        Info = 0,
        Warning = 1,
        Error = 2,
    }

    /// <summary>Stable identity for each silent-failure check the doctor runs. Tests assert against these codes rather
    /// than message text so the copy can change without breaking coverage.</summary>
    public enum DoctorCode
    {
        Ok = 0,
        NotAnimationActor,
        MissingRig,
        RigDisabled,
        GpuTag,
        RigCulled,
        MissingHash,
        MissingTrackData,
        BlendTreeNoMotions,
        OffsetsWithoutRootMotion,
        LayerWeightOrphan,
        LayerWeightZero,
        ZeroEffectiveWeight,
        ZeroClipWeight,
        MaskExcludesAllBones,
        MaskBlobMissing,
        NoActiveClips,
    }

    /// <summary>
    /// The runtime silent-failure diagnosis engine behind the Animation Doctor window. Given a plain snapshot of one
    /// actor (captured from the live world by <see cref="AnimationDoctorWindow"/>), <see cref="Diagnose"/> runs every
    /// "why is this clip not affecting the pose" check enumerated in the package audit and returns designer-facing
    /// findings — each saying WHAT is wrong and WHERE to fix it. The class is deliberately UnityEditor-free and pure
    /// (snapshot in, findings out) so every check is unit-testable without opening the window or a live ECS world.
    /// </summary>
    public static class AnimationDoctor
    {
        private const float PositionOffsetEpsilonSq = 1e-8f;
        private const float WeightEpsilon = 1e-4f;

        /// <summary>One active integrated blend entry (a <see cref="SmoothBlendGroupEntry"/> row) plus the capture-time
        /// facts the doctor needs (was its clip hash resolvable, how many rig bones its avatar mask includes).</summary>
        public struct DoctorEntry
        {
            public int LayerIndex;
            public Hash128 ClipHash;
            public float CurrentWeight;
            public float TargetWeight;
            public AnimationBlendingMode BlendMode;
            public Hash128 AvatarMaskHash;
            public float3 PositionOffset;
            public quaternion RotationOffset;

            /// <summary>The entry's clip hash was found in the BlobDatabaseSingleton animation map at capture.</summary>
            public bool HashFound;

            /// <summary>-1 when the entry carries no avatar mask. Otherwise the number of rig bones the mask INCLUDES
            /// (0 means the mask excludes the whole body, so the layer is a visual no-op).</summary>
            public int MaskIncludedBoneCount;

            /// <summary>False when the entry carries an avatar mask hash that could not be resolved in the blob DB.</summary>
            public bool MaskResolved;
        }

        /// <summary>One active timeline clip that targets this actor — a candidate "request". The window classifies it
        /// against the same gates the gather jobs use so the doctor can show the REJECTED ones and why.</summary>
        public struct DoctorRequest
        {
            /// <summary>Human label, e.g. "Single clip 'Idle'" or "Blend Tree 2D 'Locomotion'".</summary>
            public string Label;
            public bool IsBlendTree;
            public Hash128 ClipHash;
            public float Weight;
            public bool HashFound;
            public bool HasTrackData;

            /// <summary>Blend-tree only: the track's motion set is empty (bakes an empty blend, produces no pose).</summary>
            public bool MotionsEmpty;

            /// <summary>Blend-tree only: how many of the track's motion hashes were missing from the blob DB.</summary>
            public int MissingMotionHashes;
        }

        public struct DoctorLayerOverride
        {
            public int LayerIndex;
            public float Multiplier;
        }

        /// <summary>Full capture of one actor. Populated from the live world by the window; consumed by
        /// <see cref="Diagnose"/>. All fields are plain data so a test can build one by hand.</summary>
        public sealed class ActorDiagnostic
        {
            public string ActorName = string.Empty;

            // Rig / engine facts.
            public bool HasRigDefinition;
            public bool RigEnabled;
            public bool ApplyRootMotion;
            public int RigBoneCount;
            public bool HasGpuTag;
            public bool IsCulled;

            // Package-component presence (used to confirm this is really a timeline-animation actor).
            public bool HasBlendGroupTimer;
            public bool HasSmoothBuffer;
            public bool HasAtpBuffer;
            public int AtpCount;

            // Fallback (idle) offsets, so check (a) covers the fallback path too.
            public float3 FallbackPositionOffset;
            public quaternion FallbackRotationOffset;

            public readonly List<DoctorEntry> Entries = new();
            public readonly List<DoctorRequest> Requests = new();
            public readonly List<DoctorLayerOverride> LayerOverrides = new();
        }

        /// <summary>A single diagnosis result.</summary>
        public sealed class DoctorFinding
        {
            public DoctorCode Code;
            public DoctorSeverity Severity;
            public string Message = string.Empty;
        }

        /// <summary>Runs the whole checklist against one captured actor and returns the findings, most-severe first.</summary>
        public static List<DoctorFinding> Diagnose(ActorDiagnostic actor)
        {
            var findings = new List<DoctorFinding>();
            if (actor == null)
            {
                return findings;
            }

            CheckIsAnimationActor(actor, findings);
            CheckRigBinding(actor, findings);
            CheckGpuTag(actor, findings);
            CheckCulled(actor, findings);
            CheckRejectedRequests(actor, findings);
            CheckOffsetsWithoutRootMotion(actor, findings);
            CheckLayerWeightOrphans(actor, findings);
            CheckAvatarMask(actor, findings);
            CheckEffectiveWeight(actor, findings);
            CheckIdle(actor, findings);

            findings.Sort((a, b) => ((int)b.Severity).CompareTo((int)a.Severity));
            return findings;
        }

        // (b, part 1) — the selected entity is not a timeline-animation actor at all.
        internal static void CheckIsAnimationActor(ActorDiagnostic actor, List<DoctorFinding> findings)
        {
            if (actor.HasBlendGroupTimer || actor.HasSmoothBuffer)
            {
                return;
            }

            Add(findings, DoctorCode.NotAnimationActor, DoctorSeverity.Error,
                $"'{actor.ActorName}' has no BlendGroupTimer/SmoothBlendGroupEntry — it is not a timeline-animation actor. " +
                "A track's Animator binding must resolve to the rig root that this package baked. Bind the character's Animator, not a child bone or prop.");
        }

        // (b, part 2) — actor is a timeline-animation actor but has no Rukhanka rig, or the rig is disabled.
        internal static void CheckRigBinding(ActorDiagnostic actor, List<DoctorFinding> findings)
        {
            if (!actor.HasBlendGroupTimer && !actor.HasSmoothBuffer)
            {
                return; // already reported by CheckIsAnimationActor.
            }

            if (!actor.HasRigDefinition)
            {
                Add(findings, DoctorCode.MissingRig, DoctorSeverity.Error,
                    $"'{actor.ActorName}' drives timeline animation but has no RigDefinitionComponent — there is no rig to pose. " +
                    "The bound GameObject needs a RigDefinitionAuthoring (Rukhanka rig). Re-bake the SubScene after adding it.");
                return;
            }

            if (!actor.RigEnabled)
            {
                Add(findings, DoctorCode.RigDisabled, DoctorSeverity.Warning,
                    $"'{actor.ActorName}' rig (RigDefinitionComponent) is DISABLED — Rukhanka skips it, so no timeline animation is applied. " +
                    "Something disabled the enableable RigDefinitionComponent; re-enable it or check the system that toggles it.");
            }
        }

        // (e) — GPUAnimationEngineTag with this package's components: offsets / removeStartOffset are silently dropped.
        internal static void CheckGpuTag(ActorDiagnostic actor, List<DoctorFinding> findings)
        {
            if (!actor.HasGpuTag || (!actor.HasBlendGroupTimer && !actor.HasSmoothBuffer))
            {
                return;
            }

            Add(findings, DoctorCode.GpuTag, DoctorSeverity.Warning,
                $"'{actor.ActorName}' carries GPUAnimationEngineTag together with timeline-animation components. The GPU engine ignores the parity " +
                "fields (position/rotation offset, removeStartOffset), so those features silently do nothing on this rig. Use the CPU animation engine for offset-driven clips.");
        }

        // (f) — culled rig: Rukhanka and the gather jobs skip it; nothing poses.
        internal static void CheckCulled(ActorDiagnostic actor, List<DoctorFinding> findings)
        {
            if (!actor.IsCulled)
            {
                return;
            }

            Add(findings, DoctorCode.RigCulled, DoctorSeverity.Info,
                $"'{actor.ActorName}' is CULLED (CullAnimationsTag enabled) — off-screen rigs skip pose computation and the gather jobs drop their clips. " +
                "This is expected off-screen; if the character is on-screen, check the culling bounds/camera setup.");
        }

        // (c, g partial) — enumerate the active clips that were REJECTED and say why. This is the core "why isn't this
        // clip affecting the pose" dump: missing hash (the asymmetry the audit fixed), missing track data, zero weight,
        // empty blend tree.
        internal static void CheckRejectedRequests(ActorDiagnostic actor, List<DoctorFinding> findings)
        {
            foreach (var r in actor.Requests)
            {
                if (r.IsBlendTree && r.MotionsEmpty)
                {
                    Add(findings, DoctorCode.BlendTreeNoMotions, DoctorSeverity.Warning,
                        $"{r.Label}: blend tree has NO motion clips — it bakes an empty blend and produces no pose. Assign motions on the track.");
                    continue;
                }

                if (!r.HasTrackData)
                {
                    Add(findings, DoctorCode.MissingTrackData, DoctorSeverity.Warning,
                        $"{r.Label}: the clip's track has no baked track data (RukhankaSingle/BlendTree track data missing) — the clip is dropped. Re-bake the SubScene.");
                    continue;
                }

                if (r.IsBlendTree && r.MissingMotionHashes > 0)
                {
                    Add(findings, DoctorCode.MissingHash, DoctorSeverity.Warning,
                        $"{r.Label}: {r.MissingMotionHashes} motion animation hash(es) are not in the BlobDatabase — those motions are skipped. Re-bake the SubScene or check the rig binding.");
                }
                else if (!r.IsBlendTree && !r.HashFound)
                {
                    Add(findings, DoctorCode.MissingHash, DoctorSeverity.Warning,
                        $"{r.Label}: the animation hash is not in the BlobDatabase — the clip is silently skipped. Re-bake the SubScene, or the track is bound to a rig that never baked this clip.");
                    continue;
                }

                if (r.Weight <= 0f)
                {
                    Add(findings, DoctorCode.ZeroClipWeight, DoctorSeverity.Info,
                        $"{r.Label}: clip ease weight is 0 (blend-in/out handles closed at this instant, or a LayerWeight track drove it to 0) — it contributes nothing right now.");
                }
            }
        }

        // (a) — offsets authored but the rig has Apply Root Motion OFF (the Rukhanka patch only applies offsets on the
        // root-motion delta bone, so every offset is a silent no-op).
        internal static void CheckOffsetsWithoutRootMotion(ActorDiagnostic actor, List<DoctorFinding> findings)
        {
            if (!actor.HasRigDefinition || actor.ApplyRootMotion)
            {
                return;
            }

            var any = HasPositionOffset(actor.FallbackPositionOffset) || HasRotationOffset(actor.FallbackRotationOffset);
            foreach (var e in actor.Entries)
            {
                any |= HasPositionOffset(e.PositionOffset) || HasRotationOffset(e.RotationOffset);
            }

            if (!any)
            {
                return;
            }

            Add(findings, DoctorCode.OffsetsWithoutRootMotion, DoctorSeverity.Warning,
                $"'{actor.ActorName}' has active clips (or fallback) with transform offsets, but its rig has Apply Root Motion OFF. " +
                "Offsets only apply on the root-motion delta bone, so they are silently ignored. Enable Apply Root Motion on the RigDefinitionAuthoring to make them take effect.");
        }

        // (d) — LayerWeight override targeting a layer no active clip animates, or fading a layer to 0.
        internal static void CheckLayerWeightOrphans(ActorDiagnostic actor, List<DoctorFinding> findings)
        {
            foreach (var o in actor.LayerOverrides)
            {
                var hasLayer = false;
                foreach (var e in actor.Entries)
                {
                    if (e.LayerIndex == o.LayerIndex)
                    {
                        hasLayer = true;
                        break;
                    }
                }

                if (!hasLayer)
                {
                    Add(findings, DoctorCode.LayerWeightOrphan, DoctorSeverity.Warning,
                        $"'{actor.ActorName}' has a Layer Weight override on layer {o.LayerIndex}, but no active animation clip uses that layer — the fade does nothing. " +
                        "Point the Layer Weight track at a layer an animation track on this rig actually uses.");
                }
                else if (o.Multiplier <= WeightEpsilon)
                {
                    Add(findings, DoctorCode.LayerWeightZero, DoctorSeverity.Info,
                        $"'{actor.ActorName}' Layer Weight override drives layer {o.LayerIndex} to ~0 — clips on that layer are faded out and will not show while this is active.");
                }
            }
        }

        // (h) — avatar mask excludes the whole body, or the mask blob is missing.
        internal static void CheckAvatarMask(ActorDiagnostic actor, List<DoctorFinding> findings)
        {
            foreach (var e in actor.Entries)
            {
                if (e.AvatarMaskHash == default)
                {
                    continue;
                }

                if (!e.MaskResolved)
                {
                    Add(findings, DoctorCode.MaskBlobMissing, DoctorSeverity.Warning,
                        $"'{actor.ActorName}' layer {e.LayerIndex} clip declares an avatar mask that is not in the BlobDatabase — the mask cannot be applied. Re-bake the SubScene.");
                    continue;
                }

                if (e.MaskIncludedBoneCount == 0)
                {
                    Add(findings, DoctorCode.MaskExcludesAllBones, DoctorSeverity.Warning,
                        $"'{actor.ActorName}' layer {e.LayerIndex} clip uses an avatar mask that INCLUDES 0 bones — the mask excludes the whole body, so this layer changes nothing. " +
                        "Enable the bones you want this layer to drive in the Avatar Mask asset.");
                }
            }
        }

        // (g) — clips are active/accepted but every integrated layer weight is ~0, so the pose does not move.
        internal static void CheckEffectiveWeight(ActorDiagnostic actor, List<DoctorFinding> findings)
        {
            if (actor.Entries.Count == 0)
            {
                return;
            }

            var maxWeight = 0f;
            foreach (var e in actor.Entries)
            {
                maxWeight = math.max(maxWeight, e.CurrentWeight);
            }

            if (maxWeight < WeightEpsilon)
            {
                Add(findings, DoctorCode.ZeroEffectiveWeight, DoctorSeverity.Warning,
                    $"'{actor.ActorName}' has {actor.Entries.Count} active blend entr(ies) but every one has an effective weight of ~0, so the pose is not changing. " +
                    "A LayerWeight track may be fading the layer to 0, or the clip blend handles are closed. Check the LayerWeight tracks and clip ease.");
            }
        }

        // Informational: nothing is driving this actor, only the fallback idle.
        internal static void CheckIdle(ActorDiagnostic actor, List<DoctorFinding> findings)
        {
            if (actor.Requests.Count == 0 && actor.Entries.Count == 0 && actor.HasBlendGroupTimer)
            {
                Add(findings, DoctorCode.NoActiveClips, DoctorSeverity.Info,
                    $"'{actor.ActorName}' has no active animation clips right now — only the fallback (idle) is playing. " +
                    "If you expected a clip, confirm the timeline is playing and its track is bound to this actor.");
            }
        }

        internal static bool HasPositionOffset(float3 offset)
        {
            return math.lengthsq(offset) > PositionOffsetEpsilonSq;
        }

        internal static bool HasRotationOffset(quaternion rotation)
        {
            var v = rotation.value;

            // Zero quaternion (default, unset) or identity (w == ±1) are "no offset". Anything else is a real rotation.
            if (math.lengthsq(v) < 1e-6f)
            {
                return false;
            }

            return math.abs(math.abs(v.w) - 1f) > 1e-4f;
        }

        private static void Add(List<DoctorFinding> findings, DoctorCode code, DoctorSeverity severity, string message)
        {
            findings.Add(new DoctorFinding { Code = code, Severity = severity, Message = message });
        }
    }
}
