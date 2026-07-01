using System.Collections.Generic;
using System.Linq;
using BovineLabs.Timeline.Animation;
using BovineLabs.Timeline.Animation.Authoring;
using Rukhanka;
using Rukhanka.Hybrid;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using UnityEngine.Timeline;

// Build Anim Test 2 -----------------------------------------------------------------------------------------
// Adds the 5 new animation-timeline FEATURES onto rigs inside an ALREADY-COPIED, fully-set-up scene:
//   Parent : Assets/_AnimTest/AnimTest_Main.unity      (has the SubScene component -> the sub below)
//   Sub    : Assets/_AnimTest/AnimTest_Main_Sub.unity  (3 working Rukhanka rigs: "Player" + 2x "Arvex_RIG2")
//
// The two "Arvex_RIG2" rigs are DUPLICATED (Object.Instantiate preserves their 4 valid materials, so the
// BatchRendererGroup never sees a null-material rig) to make 5 rig instances, one per feature, spread along X.
// Each rig gets a child "*_Driver" (PlayableDirector, playOnAwake + Loop) driving a .playable whose track(s)
// are bound to that rig's Animator via SetGenericBinding.
//
// Two fixes over the prior attempt (both baked track data but not the runtime buffer):
//   FIX A (Inertialization): the prior code used GetComponent (root only) on the rig; the rig's real
//     TimelineAnimationStateAuthoring lives on a CHILD (co-located with RigDefinitionAuthoring = the actor),
//     so the write landed on a NEW duplicate component added to the ROOT while the actor's real component
//     stayed 0 -> InertializationState was never baked onto the actor entity. Here we edit the EXISTING
//     child component (GetComponentInChildren) via SerializedObject and read it back to confirm it persisted.
//   FIX B (LayerWeightOverride): LayerWeightTrack.Bake only bakes LayerWeightActorBakeRef (which triggers
//     LayerWeightActorBakingSystem to add the LayerWeightOverride buffer to the actor) when
//     director.ResolveRigDefinition(track) != null, i.e. the track's generic binding must resolve to a
//     RigDefinitionAuthoring. We bind the Animator that is co-located with the rig's RigDefinitionAuthoring
//     (falling back to binding the RigDefinitionAuthoring component directly), bind EVERY track (recursive
//     walk, not just GetOutputTracks), and verify resolution, so the override buffer is always provisioned.
public static class AnimTestBuilder
{
    private const string RootFolder = "Assets/_AnimTest";
    private const string TimelineFolder = RootFolder + "/Timelines";
    private const string ParentPath = RootFolder + "/AnimTest_Main.unity";
    private const string SubPath = RootFolder + "/AnimTest_Main_Sub.unity";

    private const string SourceRigName = "Arvex_RIG2";

    private const string IdleClip = "Packages/com.bovinelabs.polygon/Masculine/Idle/A_Idle_Standing_Masc.fbx";
    private const string WalkClip = "Packages/com.bovinelabs.polygon/Masculine/Locomotion/Walk/A_Walk_F_Masc.fbx";
    private const string RunClip = "Packages/com.bovinelabs.polygon/Masculine/Locomotion/Run/A_Run_F_Masc.fbx";

    private const float ColStep = 4.5f;

    private static readonly string[] FeatureNames =
    {
        "F1_BlendTree1D",
        "F2_BlendTreeDirect",
        "F3_LayerWeight",
        "F4_Inertialization",
        "F5_AdditiveRefPose",
    };

    private sealed class Wire
    {
        public string RigName;      // rig root whose Animator binds every track
        public string DriverName;   // child of the rig holding the PlayableDirector
        public string TimelinePath;
    }

    private static readonly List<Wire> Wires = new List<Wire>();

    [MenuItem("Showcase/Build Anim Test 2")]
    public static void Build2()
    {
        Wires.Clear();
        EnsureFolders();
        ResetTimelines();

        // ---- Pass 1: open the copied subscene ADDITIVELY, build the 5 rigs + drivers + timelines. ----
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var sub = EditorSceneManager.OpenScene(SubPath, OpenSceneMode.Additive);
        EditorSceneManager.SetActiveScene(sub);

        var rigs = AcquireRigs(sub);
        if (rigs == null)
        {
            Debug.LogError(
                $"[AnimTest2] Could not acquire 5 rigs from '{SubPath}'. Expected two roots named " +
                $"'{SourceRigName}' (first run) or five '{string.Join("/", FeatureNames)}' roots (re-run). Aborting.");
            return;
        }

        BuildBlendTree1D(rigs[0], 0);
        BuildBlendTreeDirect(rigs[1], 1);
        BuildLayerWeight(rigs[2], 2);
        BuildInertialization(rigs[3], 3); // FIX A lives here
        BuildAdditiveRefPose(rigs[4], 4);

        ForceAlwaysAnimate(rigs);
        EditorSceneManager.MarkSceneDirty(sub);
        EditorSceneManager.SaveScene(sub);
        EditorSceneManager.CloseScene(sub, true);

        // ---- Pass 2: reopen the settled sub as SINGLE active so director playableAsset + generic
        // bindings serialize reliably (the same two-pass the Animation Showcase uses). ----
        var subSingle = EditorSceneManager.OpenScene(SubPath, OpenSceneMode.Single);
        foreach (var w in Wires)
        {
            WireCell(subSingle, w);
        }

        ForceAlwaysAnimate(subSingle.GetRootGameObjects());
        EditorSceneManager.MarkSceneDirty(subSingle);
        EditorSceneManager.SaveScene(subSingle);
        AssetDatabase.SaveAssets();

        // ---- Pass 3: reopen fresh and READ BACK FIX A so we know the write actually persisted. ----
        var subVerify = EditorSceneManager.OpenScene(SubPath, OpenSceneMode.Single);
        VerifyInertialization(subVerify);

        Debug.Log("[AnimTest2] built 5 feature rigs in " + SubPath);
    }

    // ---- Rig acquisition (idempotent) ----
    // First run: two pristine "Arvex_RIG2" roots exist -> reuse both + 3 duplicates = 5.
    // Re-run:   they were renamed to F1..F5 -> reuse those 5 as-is (their drivers are rebuilt per feature).
    private static GameObject[] AcquireRigs(Scene sub)
    {
        var arvex = sub.GetRootGameObjects().Where(g => g.name == SourceRigName).ToList();
        if (arvex.Count >= 2)
        {
            var a0 = arvex[0];
            var a1 = arvex[1];
            // Duplicate from the PRISTINE originals (before any renaming / driver is attached).
            return new[] { a0, a1, Duplicate(a0, sub), Duplicate(a1, sub), Duplicate(a0, sub) };
        }

        var existing = FeatureNames.Select(n => FindRoot(sub, n)).ToArray();
        return existing.All(g => g != null) ? existing : null;
    }

    private static GameObject Duplicate(GameObject src, Scene sub)
    {
        var clone = Object.Instantiate(src); // preserves the working 4 valid materials
        SceneManager.MoveGameObjectToScene(clone, sub);
        return clone;
    }

    // ---- Per-rig prep: unpack (so overrides serialize as plain data), rename, reposition, tame the Animator ----
    private static void PrepRig(GameObject rig, string name, int col)
    {
        if (PrefabUtility.IsPartOfPrefabInstance(rig))
        {
            PrefabUtility.UnpackPrefabInstance(rig, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
        }

        rig.name = name;
        rig.transform.position = new Vector3(ColX(col), 0f, 0f);
        rig.transform.rotation = Quaternion.identity;

        // Remove a stale driver from a previous run before we add a fresh one.
        var oldDriver = rig.transform.Find(name + "_Driver");
        if (oldDriver != null)
        {
            Object.DestroyImmediate(oldDriver.gameObject);
        }

        var animator = rig.GetComponentInChildren<Animator>(true);
        if (animator != null)
        {
            animator.runtimeAnimatorController = null;                 // avoid controller+fallback duplicate-buffer bake error
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;  // keep every bone animating
            EditorUtility.SetDirty(animator);
        }
    }

    private static PlayableDirector MakeDriverChild(GameObject rig, string name)
    {
        var go = new GameObject(name + "_Driver");
        go.transform.SetParent(rig.transform, false);
        var d = go.AddComponent<PlayableDirector>();
        d.playOnAwake = true;
        d.extrapolationMode = DirectorWrapMode.Loop;
        return d;
    }

    private static TimelineAsset NewTimeline(string path)
    {
        if (AssetDatabase.LoadAssetAtPath<Object>(path) != null)
        {
            AssetDatabase.DeleteAsset(path);
        }

        var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
        AssetDatabase.CreateAsset(timeline, path);
        return timeline;
    }

    // FIX A: edit the EXISTING (child) TimelineAnimationStateAuthoring via SerializedObject so the write lands
    // on the actor entity's component (co-located with RigDefinitionAuthoring), not a duplicate on the root.
    private static void SetInertialization(GameObject rig, float duration, bool zeroBlends)
    {
        var state = rig.GetComponentInChildren<TimelineAnimationStateAuthoring>(true);
        if (state == null)
        {
            Debug.LogWarning($"[AnimTest2] '{rig.name}' has no TimelineAnimationStateAuthoring — inertialization not set.");
            return;
        }

        var so = new SerializedObject(state);
        so.FindProperty("inertializationDuration").floatValue = duration;
        if (zeroBlends)
        {
            so.FindProperty("blendInDuration").floatValue = 0f;
            so.FindProperty("blendOutDuration").floatValue = 0f;
        }

        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(state);
    }

    // ---- Feature 1: Blend Tree 1D (Idle 0 / Walk 0.5 / Run 1.0, ClipValue param 0.6) ----
    private static void BuildBlendTree1D(GameObject rig, int col)
    {
        PrepRig(rig, FeatureNames[col], col);
        SetInertialization(rig, 0f, false);
        MakeDriverChild(rig, FeatureNames[col]);

        var path = TimelineFolder + "/" + FeatureNames[col] + ".playable";
        var timeline = NewTimeline(path);
        var track = timeline.CreateTrack<BlendTree1DTrack>(null, "Blend Tree 1D");
        track.LayerIndex = 0;
        track.trackOffset = TrackOffset.ApplyTransformOffsets;
        track.applyAvatarMask = true;
        track.Motions = new List<BlendTree1DTrack.BlendTree1DMotionEntry>
        {
            new BlendTree1DTrack.BlendTree1DMotionEntry { clip = LoadClip(IdleClip), threshold = 0f },
            new BlendTree1DTrack.BlendTree1DMotionEntry { clip = LoadClip(WalkClip), threshold = 0.5f },
            new BlendTree1DTrack.BlendTree1DMotionEntry { clip = LoadClip(RunClip), threshold = 1.0f },
        };

        var clip = track.CreateClip<BlendTree1DClip>();
        clip.displayName = "blend 0.6";
        clip.start = 0;
        clip.duration = 4.0;
        var a = (BlendTree1DClip)clip.asset;
        a.ReadKind = BlendDirectionReadKind.ClipValue;
        a.BlendParameter = 0.6f;
        a.removeStartOffset = true;
        a.applyFootIK = true;
        Dirty(a);

        FinishTimeline(timeline, track);
        Wires.Add(new Wire { RigName = FeatureNames[col], DriverName = FeatureNames[col] + "_Driver", TimelinePath = path });
    }

    // ---- Feature 2: Blend Tree Direct (static weights, normalize on) ----
    private static void BuildBlendTreeDirect(GameObject rig, int col)
    {
        PrepRig(rig, FeatureNames[col], col);
        SetInertialization(rig, 0f, false);
        MakeDriverChild(rig, FeatureNames[col]);

        var path = TimelineFolder + "/" + FeatureNames[col] + ".playable";
        var timeline = NewTimeline(path);
        var track = timeline.CreateTrack<BlendTreeDirectTrack>(null, "Blend Tree Direct");
        track.LayerIndex = 0;
        track.normalizeBlendValues = true;
        track.trackOffset = TrackOffset.ApplyTransformOffsets;
        track.applyAvatarMask = true;
        track.Motions = new List<BlendTreeDirectTrack.BlendTreeDirectMotionEntry>
        {
            new BlendTreeDirectTrack.BlendTreeDirectMotionEntry { clip = LoadClip(IdleClip), weight = 0.25f },
            new BlendTreeDirectTrack.BlendTreeDirectMotionEntry { clip = LoadClip(WalkClip), weight = 0.5f },
            new BlendTreeDirectTrack.BlendTreeDirectMotionEntry { clip = LoadClip(RunClip), weight = 1.0f },
        };

        var clip = track.CreateClip<BlendTreeDirectClip>();
        clip.displayName = "direct weights";
        clip.start = 0;
        clip.duration = 4.0;
        var a = (BlendTreeDirectClip)clip.asset;
        a.removeStartOffset = true;
        a.applyFootIK = true;
        Dirty(a);

        FinishTimeline(timeline, track);
        Wires.Add(new Wire { RigName = FeatureNames[col], DriverName = FeatureNames[col] + "_Driver", TimelinePath = path });
    }

    // ---- Feature 3: Layer Weight (base Run L0 + overlay Idle L1 + LayerWeight clip on L1, 1s ease) ----
    private static void BuildLayerWeight(GameObject rig, int col)
    {
        PrepRig(rig, FeatureNames[col], col);
        SetInertialization(rig, 0f, false);
        MakeDriverChild(rig, FeatureNames[col]);

        var path = TimelineFolder + "/" + FeatureNames[col] + ".playable";
        var timeline = NewTimeline(path);

        var baseTrack = timeline.CreateTrack<RukhankaAnimationTrack>(null, "Base Run L0");
        baseTrack.LayerIndex = 0;
        baseTrack.BlendMode = AnimationBlendingMode.Override;
        baseTrack.trackOffset = TrackOffset.ApplyTransformOffsets;
        baseTrack.applyAvatarMask = true;
        AddRukhankaClip(baseTrack, RunClip, 0.0, 4.0, "run base");

        var overlayTrack = timeline.CreateTrack<RukhankaAnimationTrack>(null, "Overlay Idle L1");
        overlayTrack.LayerIndex = 1;
        overlayTrack.BlendMode = AnimationBlendingMode.Override;
        overlayTrack.trackOffset = TrackOffset.ApplyTransformOffsets;
        overlayTrack.applyAvatarMask = true;
        AddRukhankaClip(overlayTrack, IdleClip, 0.0, 4.0, "idle overlay");

        // LayerWeight track drives layer 1's weight; the clip's ease in/out IS the weight curve.
        var lwTrack = timeline.CreateTrack<LayerWeightTrack>(null, "Layer Weight L1");
        lwTrack.LayerIndex = 1;
        var lwClip = lwTrack.CreateClip<LayerWeightClip>();
        lwClip.displayName = "L1 fade";
        lwClip.start = 0;
        lwClip.duration = 4.0;
        lwClip.easeInDuration = 1.0;  // 1s ease in
        lwClip.easeOutDuration = 1.0; // 1s ease out
        var lw = (LayerWeightClip)lwClip.asset;
        lw.maxMultiplier = 1f;
        Dirty(lw);

        FinishTimeline(timeline, baseTrack, overlayTrack, lwTrack);
        Wires.Add(new Wire { RigName = FeatureNames[col], DriverName = FeatureNames[col] + "_Driver", TimelinePath = path });
    }

    // ---- Feature 4: Inertialization (single track, Walk 0-2s then Run 2-4s HARD CUT, no ease) ----
    private static void BuildInertialization(GameObject rig, int col)
    {
        PrepRig(rig, FeatureNames[col], col);
        // The whole point: inertialization ON (0.2), zero crossfade so the cut relies on momentum decay.
        SetInertialization(rig, 0.2f, true);
        MakeDriverChild(rig, FeatureNames[col]);

        var path = TimelineFolder + "/" + FeatureNames[col] + ".playable";
        var timeline = NewTimeline(path);
        var track = timeline.CreateTrack<RukhankaAnimationTrack>(null, "Inertialization");
        track.LayerIndex = 0;
        track.BlendMode = AnimationBlendingMode.Override;
        track.trackOffset = TrackOffset.ApplyTransformOffsets;
        track.applyAvatarMask = true;

        // No ease on either clip => hard cut at t=2s; inertialization carries the momentum across it.
        AddRukhankaClip(track, WalkClip, 0.0, 2.0, "walk");
        AddRukhankaClip(track, RunClip, 2.0, 2.0, "run");

        FinishTimeline(timeline, track);
        Wires.Add(new Wire { RigName = FeatureNames[col], DriverName = FeatureNames[col] + "_Driver", TimelinePath = path });
    }

    // ---- Feature 5: Additive Reference Pose (base Run L0 + Additive track L1 walk, refPose = Idle) ----
    private static void BuildAdditiveRefPose(GameObject rig, int col)
    {
        PrepRig(rig, FeatureNames[col], col);
        SetInertialization(rig, 0f, false);
        MakeDriverChild(rig, FeatureNames[col]);

        var path = TimelineFolder + "/" + FeatureNames[col] + ".playable";
        var timeline = NewTimeline(path);

        var baseTrack = timeline.CreateTrack<RukhankaAnimationTrack>(null, "Base Run L0");
        baseTrack.LayerIndex = 0;
        baseTrack.BlendMode = AnimationBlendingMode.Override;
        baseTrack.trackOffset = TrackOffset.ApplyTransformOffsets;
        baseTrack.applyAvatarMask = true;
        AddRukhankaClip(baseTrack, RunClip, 0.0, 4.0, "run base");

        // Additive layer: (Walk - Idle) delta layered on top of the base run.
        var addTrack = timeline.CreateTrack<RukhankaAnimationTrack>(null, "Additive L1");
        addTrack.LayerIndex = 1;
        addTrack.BlendMode = AnimationBlendingMode.Additive;
        addTrack.trackOffset = TrackOffset.ApplyTransformOffsets;
        addTrack.applyAvatarMask = true;
        var addClip = AddRukhankaClip(addTrack, WalkClip, 0.0, 4.0, "additive walk");
        var addAsset = (RukhankaAnimationClip)addClip.asset;
        addAsset.additiveReferencePoseClip = LoadClip(IdleClip);
        addAsset.additiveReferencePoseTime = 0f;
        Dirty(addAsset);

        FinishTimeline(timeline, baseTrack, addTrack);
        Wires.Add(new Wire { RigName = FeatureNames[col], DriverName = FeatureNames[col] + "_Driver", TimelinePath = path });
    }

    private static TimelineClip AddRukhankaClip(RukhankaAnimationTrack track, string clipPath, double start, double dur, string name)
    {
        var clip = track.CreateClip<RukhankaAnimationClip>();
        clip.displayName = name;
        clip.start = start;
        clip.duration = dur;
        var a = (RukhankaAnimationClip)clip.asset;
        a.animationClipHolder = LoadClip(clipPath);
        a.removeStartOffset = true;
        a.applyFootIK = true;
        Dirty(a);
        return clip;
    }

    // ---- Wiring (pass 2, single active sub-scene) ----
    private static void WireCell(Scene scene, Wire w)
    {
        var rig = FindRoot(scene, w.RigName);
        if (rig == null)
        {
            Debug.LogError("[AnimTest2] rig missing for wire: " + w.RigName);
            return;
        }

        var driverT = rig.transform.Find(w.DriverName);
        var director = driverT != null ? driverT.GetComponent<PlayableDirector>() : null;
        if (director == null)
        {
            Debug.LogError($"[AnimTest2] driver '{w.DriverName}' missing under '{w.RigName}'.");
            return;
        }

        var timeline = AssetDatabase.LoadAssetAtPath<TimelineAsset>(w.TimelinePath);
        director.playableAsset = timeline;
        director.playOnAwake = true;
        director.extrapolationMode = DirectorWrapMode.Loop;

        // FIX B: choose a binding that ResolveRigDefinition can turn into the rig. Prefer the Animator that is
        // co-located with the RigDefinitionAuthoring (also keeps the editor Timeline preview working); if for
        // some reason it is not co-located, bind the RigDefinitionAuthoring component directly (first branch of
        // ResolveRigDefinition). Either way LayerWeightTrack.Bake resolves the rig -> bakes LayerWeightActorBakeRef
        // -> LayerWeightActorBakingSystem provisions the LayerWeightOverride buffer on the actor.
        var rigDef = rig.GetComponentInChildren<RigDefinitionAuthoring>(true);
        var animator = rigDef != null ? rigDef.GetComponent<Animator>() : null;
        if (animator == null)
        {
            animator = rig.GetComponentInChildren<Animator>(true);
        }

        Object bind;
        if (rigDef != null && animator != null && animator.GetComponent<RigDefinitionAuthoring>() == rigDef)
        {
            bind = animator;      // co-located: resolves the rig AND drives editor preview
        }
        else if (rigDef != null)
        {
            bind = rigDef;        // not co-located: bind the rig directly (guaranteed resolve)
        }
        else
        {
            bind = animator;      // last resort
        }

        // Bind EVERY (non-group) track, walking nested tracks too, so the LayerWeight track is never skipped.
        foreach (var track in AllTracks(timeline))
        {
            director.SetGenericBinding(track, bind);
        }

        // Verify the binding actually resolves (mirrors ResolveRigDefinition) so a silent no-buffer bake is loud.
        if (ResolveRig(bind) == null)
        {
            Debug.LogError(
                $"[AnimTest2] '{w.RigName}': binding '{(bind != null ? bind.GetType().Name : "null")}' does NOT " +
                "resolve to a RigDefinitionAuthoring — LayerWeightOverride / animation data will NOT bake.");
        }

        EditorUtility.SetDirty(director);
    }

    private static RigDefinitionAuthoring ResolveRig(Object binding)
    {
        switch (binding)
        {
            case RigDefinitionAuthoring rda:
                return rda;
            case Animator animator:
                return animator.GetComponent<RigDefinitionAuthoring>();
            case GameObject go:
                return go.GetComponent<RigDefinitionAuthoring>();
            default:
                return null;
        }
    }

    private static IEnumerable<TrackAsset> AllTracks(TimelineAsset timeline)
    {
        foreach (var root in timeline.GetRootTracks())
        {
            foreach (var t in WalkTracks(root))
            {
                yield return t;
            }
        }
    }

    private static IEnumerable<TrackAsset> WalkTracks(TrackAsset track)
    {
        if (!(track is GroupTrack))
        {
            yield return track;
        }

        foreach (var child in track.GetChildTracks())
        {
            foreach (var t in WalkTracks(child))
            {
                yield return t;
            }
        }
    }

    // ---- FIX A read-back (pass 3) ----
    private static void VerifyInertialization(Scene scene)
    {
        var f4 = FindRoot(scene, "F4_Inertialization");
        var state = f4 != null ? f4.GetComponentInChildren<TimelineAnimationStateAuthoring>(true) : null;
        if (state == null)
        {
            Debug.LogError("[AnimTest2] FIX A read-back: F4 TimelineAnimationStateAuthoring not found.");
            return;
        }

        var so = new SerializedObject(state);
        var dur = so.FindProperty("inertializationDuration").floatValue;
        Debug.Log($"[AnimTest2] FIX A read-back: F4 inertializationDuration = {dur} (expected 0.2), on GameObject '{state.gameObject.name}'.");
        if (Mathf.Abs(dur - 0.2f) > 0.0001f)
        {
            Debug.LogError("[AnimTest2] FIX A did NOT persist — inertializationDuration is not 0.2.");
        }
    }

    // ---- Helpers ----
    private static void FinishTimeline(TimelineAsset timeline, params TrackAsset[] tracks)
    {
        FixDuration(timeline);
        Dirty(timeline);
        Dirty(tracks);
        AssetDatabase.SaveAssets();
    }

    private static AnimationClip LoadClip(string path)
    {
        var assets = AssetDatabase.LoadAllAssetsAtPath(path);
        foreach (var o in assets)
        {
            if (o is AnimationClip c && !c.name.StartsWith("__preview"))
            {
                return c;
            }
        }

        return AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
    }

    private static GameObject FindRoot(Scene scene, string name)
    {
        foreach (var root in scene.GetRootGameObjects())
        {
            if (root.name == name)
            {
                return root;
            }
        }

        return null;
    }

    private static float ColX(int col)
    {
        return (col - 2f) * ColStep;
    }

    private static void FixDuration(TimelineAsset timeline)
    {
        var end = 0.0;
        foreach (var track in timeline.GetOutputTracks())
        {
            foreach (var clip in track.GetClips())
            {
                var e = clip.start + clip.duration;
                if (e > end)
                {
                    end = e;
                }
            }
        }

        timeline.durationMode = TimelineAsset.DurationMode.FixedLength;
        timeline.fixedDuration = end;
    }

    private static void ForceAlwaysAnimate(IEnumerable<GameObject> roots)
    {
        foreach (var root in roots)
        {
            foreach (var animator in root.GetComponentsInChildren<Animator>(true))
            {
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                EditorUtility.SetDirty(animator);
            }
        }
    }

    private static void EnsureFolders()
    {
        if (!AssetDatabase.IsValidFolder(RootFolder))
        {
            AssetDatabase.CreateFolder("Assets", "_AnimTest");
        }

        if (!AssetDatabase.IsValidFolder(TimelineFolder))
        {
            AssetDatabase.CreateFolder(RootFolder, "Timelines");
        }
    }

    private static void ResetTimelines()
    {
        if (!AssetDatabase.IsValidFolder(TimelineFolder))
        {
            return;
        }

        foreach (var guid in AssetDatabase.FindAssets("t:TimelineAsset", new[] { TimelineFolder }))
        {
            AssetDatabase.DeleteAsset(AssetDatabase.GUIDToAssetPath(guid));
        }
    }

    private static void Dirty(params Object[] objs)
    {
        foreach (var o in objs)
        {
            if (o != null)
            {
                EditorUtility.SetDirty(o);
            }
        }
    }
}
