// <copyright file="RigRebinder.cs" company="BovineLabs">
//     Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Timeline.Animation.Editor
{
    using System.Collections.Generic;
    using Rukhanka.Hybrid;
    using UnityEditor;
    using UnityEngine;
    using UnityEngine.Playables;
    using UnityEngine.Timeline;
    using Object = UnityEngine.Object;

    /// <summary>
    /// One-click re-binder for the Animator (Rig) that the animation timeline tracks are bound to. When you re-chain /
    /// replace the <c>Rig</c> under a character (e.g. Player_XX), every <see cref="PlayableDirector"/> that drives it
    /// keeps a per-instance generic binding pointing at the OLD Animator — so all the Rukhanka/BlendTree/LookAt/AfterImage
    /// /LayerWeight tracks silently go unbound and animation breaks. This window finds every Animator-typed track binding
    /// under a search root (or across the loaded scenes / open Prefab Stage) and re-points them to the new Rig in one
    /// click, with a preview and full Undo. Editor-only, non-destructive until <b>Rebind</b> is pressed.
    /// </summary>
    public sealed class RigRebinder : EditorWindow
    {
        [SerializeField]
        private Object newRig; // Character root, Rig, Animator, or RigDefinitionAuthoring — FindRig pulls the RigDefinitionAuthoring out.

        [SerializeField]
        private GameObject searchRoot; // Optional: limit the scan to this hierarchy (e.g. Player_XX). Null = loaded scenes.

        [SerializeField]
        private bool onlyMissing; // Only list/rebind bindings that are currently null (broken), leave valid ones alone.

        private readonly List<Row> rows = new();
        private Vector2 scroll;

        [MenuItem("BovineLabs/Animation/Rebind Rig")]
        private static void Open()
        {
            var window = GetWindow<RigRebinder>();
            window.titleContent = new GUIContent("Rebind Rig");
            window.minSize = new Vector2(440f, 320f);
            window.PrefillFromSelection();
            window.Rescan();
            window.Show();
        }

        /// <summary>One (director, track) binding that targets an Animator.</summary>
        private sealed class Row
        {
            public PlayableDirector Director;
            public TrackAsset Track;
            public string TimelineName;
            public Object Current;
            public bool Include = true;
        }

        /// <summary>
        /// The rig component we will actually bind. Binding the <see cref="RigDefinitionAuthoring"/> directly is
        /// bake-proof — <c>RukhankaAnimationTrackExtensions.ResolveRigDefinition</c> returns it as-is (its first case),
        /// so it works no matter which GameObject the Animator sits on. Searches the dropped object's hierarchy so the
        /// designer can drop the character root, the Rig, the Animator, or the RigDefinitionAuthoring — all resolve.
        /// </summary>
        private static RigDefinitionAuthoring FindRig(Object o)
        {
            switch (o)
            {
                case null:
                    return null;
                case RigDefinitionAuthoring rda:
                    return rda;
                case GameObject go:
                    return go.GetComponentInChildren<RigDefinitionAuthoring>(true);
                case Component c:
                    return c.GetComponentInChildren<RigDefinitionAuthoring>(true);
                default:
                    return null;
            }
        }

        /// <summary>
        /// What the DOTS bake will resolve an existing binding down to. Mirrors
        /// <c>RukhankaAnimationTrackExtensions.ResolveRigDefinition</c> EXACTLY (same-GameObject GetComponent, no
        /// hierarchy search) so the tool's "already bound" verdict can never disagree with what actually bakes.
        /// </summary>
        private static RigDefinitionAuthoring BakeResolve(Object binding)
        {
            switch (binding)
            {
                case RigDefinitionAuthoring rda:
                    return rda;
                case Animator a:
                    return a.GetComponent<RigDefinitionAuthoring>();
                case GameObject go:
                    return go.GetComponent<RigDefinitionAuthoring>();
                default:
                    return null;
            }
        }

        private void PrefillFromSelection()
        {
            var sel = Selection.activeGameObject;
            if (sel == null)
            {
                return;
            }

            var rig = sel.GetComponentInChildren<RigDefinitionAuthoring>(true);
            if (this.newRig == null && rig != null)
            {
                this.newRig = rig;
            }

            if (this.searchRoot == null)
            {
                this.searchRoot = sel.transform.root.gameObject;
            }
        }

        private void Rescan()
        {
            this.rows.Clear();

            IEnumerable<PlayableDirector> directors = this.searchRoot != null
                ? this.searchRoot.GetComponentsInChildren<PlayableDirector>(true)
                : Object.FindObjectsByType<PlayableDirector>(FindObjectsInactive.Include, FindObjectsSortMode.None);

            foreach (var director in directors)
            {
                if (director.playableAsset == null)
                {
                    continue;
                }

                foreach (var binding in director.playableAsset.outputs)
                {
                    // Generic: any track whose output expects an Animator (Rukhanka, BlendTree 1D/2D/Direct, LayerWeight,
                    // AfterImage, CharacterLookAt, plus stock AnimationTrack). No per-track-type code needed.
                    if (binding.outputTargetType == null || !typeof(Animator).IsAssignableFrom(binding.outputTargetType))
                    {
                        continue;
                    }

                    if (binding.sourceObject is not TrackAsset track)
                    {
                        continue;
                    }

                    var current = director.GetGenericBinding(track);
                    if (this.onlyMissing && current != null)
                    {
                        continue;
                    }

                    this.rows.Add(new Row
                    {
                        Director = director,
                        Track = track,
                        TimelineName = director.playableAsset.name,
                        Current = current,
                    });
                }
            }

            this.Repaint();
        }

        private void RebindChecked()
        {
            var target = FindRig(this.newRig);
            if (target == null)
            {
                return;
            }

            var dirtied = new HashSet<PlayableDirector>();
            foreach (var row in this.rows)
            {
                if (row.Include && row.Director != null)
                {
                    dirtied.Add(row.Director);
                }
            }

            foreach (var director in dirtied)
            {
                Undo.RegisterCompleteObjectUndo(director, "Rebind Timeline Rig");
            }

            var changed = 0;
            foreach (var row in this.rows)
            {
                if (!row.Include || row.Director == null || row.Track == null)
                {
                    continue;
                }

                if (BakeResolve(row.Current) == target)
                {
                    continue; // already bakes to the right rig
                }

                row.Director.SetGenericBinding(row.Track, target);
                EditorUtility.SetDirty(row.Director);
                if (PrefabUtility.IsPartOfPrefabInstance(row.Director))
                {
                    PrefabUtility.RecordPrefabInstancePropertyModifications(row.Director);
                }

                changed++;
            }

            Debug.Log(
                $"[RigRebinder] Rebound {changed} track binding(s) to rig '{target.name}'. " +
                "If these directors live in a SubScene, re-bake it (re-enter Play or reopen the SubScene) for the change to take effect at runtime.",
                target);
            this.Rescan();
        }

        private void OnGUI()
        {
            var target = FindRig(this.newRig);

            EditorGUILayout.HelpBox(
                "Re-point every animation timeline track to a swapped Rig.\n" +
                "1. Drop the NEW Rig (Animator / GameObject) below.\n" +
                "2. Optionally set a Search Root (e.g. Player_XX) to scope it.\n" +
                "3. Review the list, then press Rebind.",
                MessageType.Info);

            EditorGUI.BeginChangeCheck();
            this.newRig = EditorGUILayout.ObjectField(
                new GUIContent("New Rig", "Character root, Rig, Animator, or RigDefinitionAuthoring — the RigDefinitionAuthoring under it is what gets bound."),
                this.newRig, typeof(Object), true);
            this.searchRoot = (GameObject)EditorGUILayout.ObjectField(
                new GUIContent("Search Root", "Only scan directors under this object. Empty = all loaded scenes / open Prefab Stage."),
                this.searchRoot, typeof(GameObject), true);
            this.onlyMissing = EditorGUILayout.ToggleLeft(
                new GUIContent("Only fix missing (null) bindings", "Leave bindings that already point somewhere untouched."),
                this.onlyMissing);
            if (EditorGUI.EndChangeCheck())
            {
                this.Rescan();
            }

            if (this.newRig != null && target == null)
            {
                EditorGUILayout.HelpBox("No RigDefinitionAuthoring found under the New Rig — the bake would drop the animation.", MessageType.Error);
            }

            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (GUILayout.Button("Rescan", EditorStyles.toolbarButton, GUILayout.Width(80f)))
                {
                    this.Rescan();
                }

                GUILayout.FlexibleSpace();
                GUILayout.Label($"{this.rows.Count} binding(s)", EditorStyles.miniLabel);
            }

            if (this.rows.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "No Animator-bound tracks found in scope.\n" +
                    "Open the character's Prefab or its SubScene first — closed SubScenes aren't loaded, so their directors can't be scanned.",
                    MessageType.None);
            }
            else
            {
                this.scroll = EditorGUILayout.BeginScrollView(this.scroll);
                var lastDirector = default(PlayableDirector);
                foreach (var row in this.rows)
                {
                    if (row.Director != lastDirector)
                    {
                        lastDirector = row.Director;
                        EditorGUILayout.Space(2f);
                        EditorGUILayout.LabelField($"{row.Director.name}  ·  {row.TimelineName}", EditorStyles.boldLabel);
                    }

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        row.Include = EditorGUILayout.Toggle(row.Include, GUILayout.Width(18f));
                        EditorGUILayout.LabelField(row.Track != null ? row.Track.name : "<null track>", GUILayout.MinWidth(90f));

                        var (statusText, statusColor) = this.Status(row, target);
                        var prev = GUI.color;
                        GUI.color = statusColor;
                        EditorGUILayout.LabelField(statusText, EditorStyles.miniLabel);
                        GUI.color = prev;

                        if (GUILayout.Button("Ping", GUILayout.Width(46f)))
                        {
                            EditorGUIUtility.PingObject(row.Director);
                            Selection.activeObject = row.Director;
                        }
                    }
                }

                EditorGUILayout.EndScrollView();
            }

            EditorGUILayout.Space(4f);
            using (new EditorGUI.DisabledScope(target == null || this.rows.Count == 0))
            {
                var label = target != null ? $"Rebind checked  →  {target.name}" : "Rebind checked";
                if (GUILayout.Button(label, GUILayout.Height(30f)))
                {
                    this.RebindChecked();
                }
            }
        }

        private (string, Color) Status(Row row, RigDefinitionAuthoring target)
        {
            if (row.Current == null)
            {
                return ("missing → will bind", new Color(1f, 0.5f, 0.4f));
            }

            var bakesTo = BakeResolve(row.Current);
            if (target != null && bakesTo == target)
            {
                return ("already bound", new Color(0.5f, 0.9f, 0.5f));
            }

            if (bakesTo == null)
            {
                return ($"{row.Current.name} (no rig!) → will rebind", new Color(1f, 0.5f, 0.4f));
            }

            return ($"{bakesTo.name} → will rebind", new Color(0.95f, 0.85f, 0.4f));
        }
    }
}
