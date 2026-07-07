// <copyright file="AnimationDoctorWindow.cs" company="BovineLabs">
//     Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Timeline.Animation.Editor
{
    using System.Collections.Generic;
    using BovineLabs.Timeline.Data;
    using Rukhanka;
    using Unity.Collections;
    using Unity.Entities;
    using Unity.Mathematics;
    using UnityEditor;
    using UnityEngine;
    using Hash128 = Unity.Entities.Hash128;

    /// <summary>
    /// The "Animation Doctor": a runtime diagnosis window that answers "why is this clip not affecting the pose?" for a
    /// selected actor. It captures the actor's live blend state (active SmoothBlendGroupEntry rows), the active timeline
    /// clips that target it (the requests), and the rig/engine facts from the running world, then runs
    /// <see cref="AnimationDoctor"/>'s silent-failure checklist and reports each finding in designer language. Opened
    /// from BovineLabs/Animation/Animation Doctor or the "Animation Doctor" button on the Animation Validator window.
    /// Editor-only; all capture is read-only main-thread access to the default world.
    /// </summary>
    public sealed class AnimationDoctorWindow : EditorWindow
    {
        private readonly List<(Entity Entity, string Name)> actors = new();
        private AnimationDoctor.ActorDiagnostic diagnostic;
        private List<AnimationDoctor.DoctorFinding> findings = new();
        private Entity selected = Entity.Null;
        private Vector2 scroll;
        private bool showEntries = true;
        private bool showRequests = true;

        [MenuItem("BovineLabs/Animation/Animation Doctor")]
        public static void Open()
        {
            var window = GetWindow<AnimationDoctorWindow>();
            window.titleContent = new GUIContent("Animation Doctor");
            window.Refresh();
            window.Show();
        }

        /// <summary>Re-enumerates the actors in the default world and re-runs the diagnosis for the selected one.</summary>
        public void Refresh()
        {
            RefreshActorList();

            // Keep the current selection if it still exists; otherwise pick the first actor.
            if (selected == Entity.Null || !ContainsActor(selected))
            {
                selected = actors.Count > 0 ? actors[0].Entity : Entity.Null;
            }

            Recapture();
            Repaint();
        }

        private static World GetWorld()
        {
            return World.DefaultGameObjectInjectionWorld;
        }

        private bool ContainsActor(Entity entity)
        {
            foreach (var a in actors)
            {
                if (a.Entity == entity)
                {
                    return true;
                }
            }

            return false;
        }

        private void RefreshActorList()
        {
            actors.Clear();

            var world = GetWorld();
            if (world is not { IsCreated: true })
            {
                return;
            }

            var em = world.EntityManager;
            using var query = em.CreateEntityQuery(ComponentType.ReadOnly<BlendGroupTimer>());
            using var entities = query.ToEntityArray(Allocator.Temp);
            for (var i = 0; i < entities.Length; i++)
            {
                var e = entities[i];
                var name = em.GetName(e);
                if (string.IsNullOrEmpty(name))
                {
                    name = $"Entity {e.Index}:{e.Version}";
                }

                actors.Add((e, name));
            }
        }

        private void Recapture()
        {
            diagnostic = null;
            findings = new List<AnimationDoctor.DoctorFinding>();

            var world = GetWorld();
            if (world is not { IsCreated: true } || selected == Entity.Null)
            {
                return;
            }

            var em = world.EntityManager;
            if (!em.Exists(selected))
            {
                return;
            }

            diagnostic = Capture(em, selected);
            findings = AnimationDoctor.Diagnose(diagnostic);
        }

        private static AnimationDoctor.ActorDiagnostic Capture(EntityManager em, Entity actor)
        {
            var actorName = em.GetName(actor);
            if (string.IsNullOrEmpty(actorName))
            {
                actorName = $"Entity {actor.Index}:{actor.Version}";
            }

            var d = new AnimationDoctor.ActorDiagnostic
            {
                ActorName = actorName,
                HasBlendGroupTimer = em.HasComponent<BlendGroupTimer>(actor),
                HasSmoothBuffer = em.HasBuffer<SmoothBlendGroupEntry>(actor),
                HasGpuTag = em.HasComponent<GPUAnimationEngineTag>(actor),
                IsCulled = em.HasComponent<CullAnimationsTag>(actor) && em.IsComponentEnabled<CullAnimationsTag>(actor),
            };

            if (em.HasComponent<RigDefinitionComponent>(actor))
            {
                d.HasRigDefinition = true;
                d.RigEnabled = em.IsComponentEnabled<RigDefinitionComponent>(actor);
                var rig = em.GetComponentData<RigDefinitionComponent>(actor);
                d.ApplyRootMotion = rig.applyRootMotion;
                d.RigBoneCount = rig.rigBlob.IsCreated ? rig.rigBlob.Value.bones.Length : 0;
            }

            if (em.HasBuffer<AnimationToProcessComponent>(actor))
            {
                d.HasAtpBuffer = true;
                d.AtpCount = em.GetBuffer<AnimationToProcessComponent>(actor, true).Length;
            }

            if (em.HasComponent<FallbackBlend>(actor))
            {
                var fb = em.GetComponentData<FallbackBlend>(actor);
                d.FallbackPositionOffset = fb.PositionOffset;
                d.FallbackRotationOffset = fb.RotationOffset;
            }

            // Blob DB for hash resolution. Without it we cannot say whether a hash exists, so leave HashFound true.
            var haveDb = TryGetBlobDatabase(em, out var animations, out var avatarMasks);

            if (d.HasSmoothBuffer)
            {
                var buffer = em.GetBuffer<SmoothBlendGroupEntry>(actor, true);
                for (var i = 0; i < buffer.Length; i++)
                {
                    var s = buffer[i];
                    var entry = new AnimationDoctor.DoctorEntry
                    {
                        LayerIndex = s.LayerIndex,
                        ClipHash = s.ClipHash,
                        CurrentWeight = s.CurrentWeight,
                        TargetWeight = s.TargetWeight,
                        BlendMode = s.BlendMode,
                        AvatarMaskHash = s.AvatarMaskHash,
                        PositionOffset = s.PositionOffset,
                        RotationOffset = s.RotationOffset,
                        HashFound = !haveDb || animations.ContainsKey(s.ClipHash),
                        MaskIncludedBoneCount = -1,
                        MaskResolved = true,
                    };

                    if (s.AvatarMaskHash != default)
                    {
                        var maskResolved = haveDb && avatarMasks.IsCreated &&
                                           avatarMasks.TryGetValue(s.AvatarMaskHash, out var maskBlob) && maskBlob.IsCreated;
                        entry.MaskResolved = maskResolved;
                        entry.MaskIncludedBoneCount = maskResolved
                            ? CountIncludedBones(avatarMasks[s.AvatarMaskHash], d.RigBoneCount)
                            : -1;
                    }

                    d.Entries.Add(entry);
                }
            }

            if (em.HasBuffer<LayerWeightOverride>(actor))
            {
                var buffer = em.GetBuffer<LayerWeightOverride>(actor, true);
                for (var i = 0; i < buffer.Length; i++)
                {
                    d.LayerOverrides.Add(new AnimationDoctor.DoctorLayerOverride
                    {
                        LayerIndex = buffer[i].LayerIndex,
                        Multiplier = buffer[i].Multiplier,
                    });
                }
            }

            CaptureRequests(em, actor, haveDb, animations, d);
            return d;
        }

        private static int CountIncludedBones(BlobAssetReference<AvatarMaskBlob> maskBlob, int rigBoneCount)
        {
            if (!maskBlob.IsCreated)
            {
                return 0;
            }

            ref var blob = ref maskBlob.Value;
            var count = 0;
            var bones = math.max(rigBoneCount, 0);
            for (var i = 0; i < bones; i++)
            {
                if (blob.IsBoneIncluded(i))
                {
                    count++;
                }
            }

            return count;
        }

        private static void CaptureRequests(
            EntityManager em, Entity actor, bool haveDb,
            NativeHashMap<Hash128, BlobAssetReference<AnimationClipBlob>> animations, AnimationDoctor.ActorDiagnostic d)
        {
            // Single-clip requests — the exact path the audit called out (GatherActiveClipsJob dropping missing hashes).
            using (var query = em.CreateEntityQuery(
                       ComponentType.ReadOnly<RukhankaSingleClipData>(),
                       ComponentType.ReadOnly<TrackBinding>(),
                       ComponentType.ReadOnly<Clip>(),
                       ComponentType.ReadOnly<ClipActive>()))
            using (var entities = query.ToEntityArray(Allocator.Temp))
            {
                foreach (var clipEntity in entities)
                {
                    var binding = em.GetComponentData<TrackBinding>(clipEntity);
                    if (binding.Value != actor)
                    {
                        continue;
                    }

                    var clipData = em.GetComponentData<RukhankaSingleClipData>(clipEntity);
                    var clip = em.GetComponentData<Clip>(clipEntity);

                    d.Requests.Add(new AnimationDoctor.DoctorRequest
                    {
                        Label = $"Single clip on '{TrackName(em, clip.Track)}'",
                        IsBlendTree = false,
                        ClipHash = clipData.ClipHash,
                        Weight = ClipWeightOf(em, clipEntity),
                        HasTrackData = em.HasComponent<RukhankaSingleTrackData>(clip.Track),
                        HashFound = !haveDb || animations.ContainsKey(clipData.ClipHash),
                    });
                }
            }

            CaptureBlendTree2D(em, actor, haveDb, animations, d);
            CaptureBlendTree1D(em, actor, haveDb, animations, d);
            CaptureBlendTreeDirect(em, actor, haveDb, animations, d);
        }

        private static void CaptureBlendTree2D(
            EntityManager em, Entity actor, bool haveDb,
            NativeHashMap<Hash128, BlobAssetReference<AnimationClipBlob>> animations, AnimationDoctor.ActorDiagnostic d)
        {
            using var query = em.CreateEntityQuery(
                ComponentType.ReadOnly<BlendTree2DDirectionClipData>(),
                ComponentType.ReadOnly<TrackBinding>(),
                ComponentType.ReadOnly<Clip>(),
                ComponentType.ReadOnly<ClipActive>());
            using var entities = query.ToEntityArray(Allocator.Temp);
            foreach (var clipEntity in entities)
            {
                var binding = em.GetComponentData<TrackBinding>(clipEntity);
                if (binding.Value != actor)
                {
                    continue;
                }

                var clip = em.GetComponentData<Clip>(clipEntity);
                var hasTrack = em.HasComponent<BlendAnimationTree2DTrackData>(clip.Track);
                var motionsEmpty = true;
                var missing = 0;
                if (em.HasBuffer<BlendTree2DMotionData>(clip.Track))
                {
                    var motions = em.GetBuffer<BlendTree2DMotionData>(clip.Track, true);
                    motionsEmpty = motions.Length == 0;
                    for (var i = 0; i < motions.Length; i++)
                    {
                        if (haveDb && !animations.ContainsKey(motions[i].AnimationHash))
                        {
                            missing++;
                        }
                    }
                }

                d.Requests.Add(new AnimationDoctor.DoctorRequest
                {
                    Label = $"Blend Tree 2D on '{TrackName(em, clip.Track)}'",
                    IsBlendTree = true,
                    Weight = ClipWeightOf(em, clipEntity),
                    HasTrackData = hasTrack,
                    MotionsEmpty = motionsEmpty,
                    MissingMotionHashes = missing,
                });
            }
        }

        private static void CaptureBlendTree1D(
            EntityManager em, Entity actor, bool haveDb,
            NativeHashMap<Hash128, BlobAssetReference<AnimationClipBlob>> animations, AnimationDoctor.ActorDiagnostic d)
        {
            using var query = em.CreateEntityQuery(
                ComponentType.ReadOnly<BlendTree1DParameterClipData>(),
                ComponentType.ReadOnly<TrackBinding>(),
                ComponentType.ReadOnly<Clip>(),
                ComponentType.ReadOnly<ClipActive>());
            using var entities = query.ToEntityArray(Allocator.Temp);
            foreach (var clipEntity in entities)
            {
                var binding = em.GetComponentData<TrackBinding>(clipEntity);
                if (binding.Value != actor)
                {
                    continue;
                }

                var clip = em.GetComponentData<Clip>(clipEntity);
                var hasTrack = em.HasComponent<BlendAnimationTree1DTrackData>(clip.Track);
                var motionsEmpty = true;
                var missing = 0;
                if (em.HasBuffer<BlendTree1DMotionData>(clip.Track))
                {
                    var motions = em.GetBuffer<BlendTree1DMotionData>(clip.Track, true);
                    motionsEmpty = motions.Length == 0;
                    for (var i = 0; i < motions.Length; i++)
                    {
                        if (haveDb && !animations.ContainsKey(motions[i].AnimationHash))
                        {
                            missing++;
                        }
                    }
                }

                d.Requests.Add(new AnimationDoctor.DoctorRequest
                {
                    Label = $"Blend Tree 1D on '{TrackName(em, clip.Track)}'",
                    IsBlendTree = true,
                    Weight = ClipWeightOf(em, clipEntity),
                    HasTrackData = hasTrack,
                    MotionsEmpty = motionsEmpty,
                    MissingMotionHashes = missing,
                });
            }
        }

        private static void CaptureBlendTreeDirect(
            EntityManager em, Entity actor, bool haveDb,
            NativeHashMap<Hash128, BlobAssetReference<AnimationClipBlob>> animations, AnimationDoctor.ActorDiagnostic d)
        {
            using var query = em.CreateEntityQuery(
                ComponentType.ReadOnly<BlendTreeDirectClipData>(),
                ComponentType.ReadOnly<TrackBinding>(),
                ComponentType.ReadOnly<Clip>(),
                ComponentType.ReadOnly<ClipActive>());
            using var entities = query.ToEntityArray(Allocator.Temp);
            foreach (var clipEntity in entities)
            {
                var binding = em.GetComponentData<TrackBinding>(clipEntity);
                if (binding.Value != actor)
                {
                    continue;
                }

                var clip = em.GetComponentData<Clip>(clipEntity);
                var hasTrack = em.HasComponent<BlendAnimationTreeDirectTrackData>(clip.Track);
                var motionsEmpty = true;
                var missing = 0;
                if (em.HasBuffer<BlendTreeDirectMotionData>(clip.Track))
                {
                    var motions = em.GetBuffer<BlendTreeDirectMotionData>(clip.Track, true);
                    motionsEmpty = motions.Length == 0;
                    for (var i = 0; i < motions.Length; i++)
                    {
                        if (haveDb && !animations.ContainsKey(motions[i].AnimationHash))
                        {
                            missing++;
                        }
                    }
                }

                d.Requests.Add(new AnimationDoctor.DoctorRequest
                {
                    Label = $"Blend Tree Direct on '{TrackName(em, clip.Track)}'",
                    IsBlendTree = true,
                    Weight = ClipWeightOf(em, clipEntity),
                    HasTrackData = hasTrack,
                    MotionsEmpty = motionsEmpty,
                    MissingMotionHashes = missing,
                });
            }
        }

        private static float ClipWeightOf(EntityManager em, Entity clipEntity)
        {
            return em.HasComponent<ClipWeight>(clipEntity) ? em.GetComponentData<ClipWeight>(clipEntity).Value : 1f;
        }

        private static string TrackName(EntityManager em, Entity track)
        {
            var name = em.GetName(track);
            return string.IsNullOrEmpty(name) ? "track" : name;
        }

        private static bool TryGetBlobDatabase(
            EntityManager em,
            out NativeHashMap<Hash128, BlobAssetReference<AnimationClipBlob>> animations,
            out NativeHashMap<Hash128, BlobAssetReference<AvatarMaskBlob>> avatarMasks)
        {
            animations = default;
            avatarMasks = default;

            using var query = em.CreateEntityQuery(ComponentType.ReadOnly<BlobDatabaseSingleton>());
            if (query.IsEmpty)
            {
                return false;
            }

            var db = query.GetSingleton<BlobDatabaseSingleton>();
            if (!db.animations.IsCreated)
            {
                return false;
            }

            animations = db.animations;
            avatarMasks = db.avatarMasks;
            return true;
        }

        private static MessageType ToMessageType(DoctorSeverity severity)
        {
            return severity switch
            {
                DoctorSeverity.Error => MessageType.Error,
                DoctorSeverity.Warning => MessageType.Warning,
                _ => MessageType.Info,
            };
        }

        private void OnGUI()
        {
            DrawToolbar();

            var world = GetWorld();
            if (world is not { IsCreated: true })
            {
                EditorGUILayout.HelpBox(
                    "No default world. Enter Play mode (or open a baked SubScene) so the animation actors exist, then press Refresh.",
                    MessageType.Info);
                return;
            }

            if (actors.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "No timeline-animation actors found in the default world. An actor is any entity with a BlendGroupTimer " +
                    "(baked from TimelineAnimationStateAuthoring). Enter Play mode and press Refresh.",
                    MessageType.Info);
                return;
            }

            if (diagnostic == null)
            {
                EditorGUILayout.HelpBox("Select an actor and press Refresh.", MessageType.Info);
                return;
            }

            scroll = EditorGUILayout.BeginScrollView(scroll);

            DrawActorSummary(diagnostic);

            if (findings.Count == 0)
            {
                EditorGUILayout.HelpBox("No silent-failure issues detected for this actor.", MessageType.Info);
            }
            else
            {
                foreach (var finding in findings)
                {
                    EditorGUILayout.HelpBox(finding.Message, ToMessageType(finding.Severity));
                }
            }

            DrawEntries(diagnostic);
            DrawRequests(diagnostic);

            EditorGUILayout.EndScrollView();
        }

        private void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(80)))
                {
                    Refresh();
                }

                if (actors.Count > 0)
                {
                    var names = new string[actors.Count];
                    var current = 0;
                    for (var i = 0; i < actors.Count; i++)
                    {
                        names[i] = actors[i].Name;
                        if (actors[i].Entity == selected)
                        {
                            current = i;
                        }
                    }

                    var picked = EditorGUILayout.Popup(current, names, EditorStyles.toolbarPopup, GUILayout.MinWidth(160));
                    if (picked != current)
                    {
                        selected = actors[picked].Entity;
                        Recapture();
                    }
                }

                GUILayout.FlexibleSpace();
                GUILayout.Label($"{findings.Count} finding(s)", EditorStyles.miniLabel);
            }
        }

        private void DrawActorSummary(AnimationDoctor.ActorDiagnostic d)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(d.ActorName, EditorStyles.boldLabel);
                var rig = d.HasRigDefinition
                    ? $"rig {(d.RigEnabled ? "on" : "DISABLED")}, {d.RigBoneCount} bones, root motion {(d.ApplyRootMotion ? "ON" : "off")}"
                    : "NO RIG";
                var engine = d.HasGpuTag ? "GPU engine" : "CPU engine";
                var cull = d.IsCulled ? ", CULLED" : string.Empty;
                EditorGUILayout.LabelField($"{rig} · {engine}{cull}", EditorStyles.miniLabel);
                EditorGUILayout.LabelField(
                    $"active entries: {d.Entries.Count} · active clips: {d.Requests.Count} · ATP out: {d.AtpCount} · layer overrides: {d.LayerOverrides.Count}",
                    EditorStyles.miniLabel);
            }
        }

        private void DrawEntries(AnimationDoctor.ActorDiagnostic d)
        {
            showEntries = EditorGUILayout.Foldout(showEntries, $"Active blend entries ({d.Entries.Count})", true);
            if (!showEntries)
            {
                return;
            }

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (d.Entries.Count == 0)
                {
                    EditorGUILayout.LabelField("— none —", EditorStyles.miniLabel);
                    return;
                }

                foreach (var e in d.Entries)
                {
                    var mask = e.AvatarMaskHash == default
                        ? "no mask"
                        : e.MaskResolved ? $"mask {e.MaskIncludedBoneCount}/{d.RigBoneCount} bones" : "mask MISSING";
                    var hash = e.HashFound ? "hash ok" : "hash MISSING";
                    EditorGUILayout.LabelField(
                        $"L{e.LayerIndex} {e.BlendMode} w {e.CurrentWeight:0.00}->{e.TargetWeight:0.00} · {hash} · {mask}",
                        EditorStyles.miniLabel);
                }
            }
        }

        private void DrawRequests(AnimationDoctor.ActorDiagnostic d)
        {
            showRequests = EditorGUILayout.Foldout(showRequests, $"Active clips / requests ({d.Requests.Count})", true);
            if (!showRequests)
            {
                return;
            }

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (d.Requests.Count == 0)
                {
                    EditorGUILayout.LabelField("— none —", EditorStyles.miniLabel);
                    return;
                }

                foreach (var r in d.Requests)
                {
                    string status;
                    if (!r.HasTrackData)
                    {
                        status = "REJECTED: no track data";
                    }
                    else if (r.IsBlendTree && r.MotionsEmpty)
                    {
                        status = "REJECTED: empty blend tree";
                    }
                    else if (r.IsBlendTree && r.MissingMotionHashes > 0)
                    {
                        status = $"PARTIAL: {r.MissingMotionHashes} motion hash(es) missing";
                    }
                    else if (!r.IsBlendTree && !r.HashFound)
                    {
                        status = "REJECTED: hash missing";
                    }
                    else if (r.Weight <= 0f)
                    {
                        status = "inactive: weight 0";
                    }
                    else
                    {
                        status = $"ok · weight {r.Weight:0.00}";
                    }

                    EditorGUILayout.LabelField($"{r.Label} — {status}", EditorStyles.miniLabel);
                }
            }
        }
    }
}
