using System.Collections.Generic;
using System.Linq;
using BovineLabs.Timeline.Animation;
using BovineLabs.Timeline.Animation.Authoring;
using Rukhanka.Hybrid;
using TMPro;
using Unity.Scenes;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using UnityEngine.Timeline;

public static class AnimationShowcaseBuilder
{
    private const string SampleFolder = "Assets/Samples/AnimationShowcase";
    private const string TimelineFolder = SampleFolder + "/Timelines";
    private const string ParentPath = SampleFolder + "/AnimationShowcase.unity";
    private const string SubPath = SampleFolder + "/AnimationShowcase_Sub.unity";
    private const string GhostPrefabPath = SampleFolder + "/AfterImageGhost.prefab";

    private const string RigFbxPath = "Packages/com.bovinelabs.polygon/Arvex_RIG2.fbx";
    private const string ControllerPath = "Packages/com.bovinelabs.polygon/AC_Polygon_Masculine.controller";

    private const string IdleClip = "Packages/com.bovinelabs.polygon/Masculine/Idle/A_Idle_Standing_Masc.fbx";
    private const string RunFwd = "Packages/com.bovinelabs.polygon/Masculine/Locomotion/Run/A_Run_F_Masc.fbx";
    private const string WalkFwd = "Packages/com.bovinelabs.polygon/Masculine/Locomotion/Walk/A_Walk_F_Masc.fbx";
    private const string WalkBack = "Packages/com.bovinelabs.polygon/Masculine/Locomotion/Walk/A_Walk_BckStrafeB_Masc.fbx";
    private const string StrafeR = "Packages/com.bovinelabs.polygon/Masculine/Locomotion/Run/A_Run_FwdStrafeR_Masc.fbx";
    private const string StrafeL = "Packages/com.bovinelabs.polygon/Masculine/Locomotion/Run/A_Run_FwdStrafeL_Masc.fbx";

    private static readonly Color RukhankaColor = new Color(0.55f, 0.35f, 0.95f);
    private static readonly Color BlendColor = new Color(0.20f, 0.70f, 0.85f);
    private static readonly Color AfterImageColor = new Color(0.85f, 0.20f, 0.70f);
    private static readonly Color WeaponColor = new Color(0.40f, 0.85f, 0.45f);
    private static readonly Color LookAtColor = new Color(0.95f, 0.70f, 0.20f);
    private static readonly Color IdleColor = new Color(0.70f, 0.75f, 0.85f);
    private static readonly Color BannerColor = new Color(0.07f, 0.09f, 0.14f);

    private const float ColStep = 4.5f;
    private static readonly Vector3 CameraPos = new Vector3(0f, 3.4f, -9.5f);

    private sealed class Wire
    {
        public string DirectorName;
        public string TimelinePath;
        public string BindName;        // GameObject name whose Animator/RigDefinition binds the track
        public bool BindWeapon;        // if true, bind the SetGenericBinding to the weapon GO transform's Animator-less obj
        public string WeaponBindName;
        public PropertyName ExposedName;
        public bool HasExposed;
        public string ExposedBoneOwner; // GO that owns the bone transform to expose
        public string ExposedBonePath;  // child path to the bone under the owner
    }

    private static readonly List<Wire> Wires = new List<Wire>();

    private sealed class Caption
    {
        public string Title;
        public string Usage;
        public Vector3 Pos;
        public Color Color;
    }

    private static readonly List<Caption> Captions = new List<Caption>();

    [MenuItem("Showcase/Build Animation")]
    public static void Build()
    {
        Wires.Clear();
        Captions.Clear();
        EnsureFolders();
        ResetAssets();

        BuildGhostPrefab();

        var parent = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        EditorSceneManager.SaveScene(parent, ParentPath);
        var sub = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
        EditorSceneManager.SetActiveScene(sub);

        BuildGround();
        BuildRukhankaColumn(sub, 0);
        BuildBlendColumn(sub, 1);
        BuildAfterImageColumn(sub, 2);
        BuildWeaponColumn(sub, 3);
        BuildLookAtColumn(sub, 4);
        BuildIdleColumn(sub, 5);

        EditorSceneManager.SaveScene(sub, SubPath);
        EditorSceneManager.SetActiveScene(parent);
        EditorSceneManager.CloseScene(sub, true);

        // Two-pass wiring: reopen the settled sub-scene as SINGLE active so director
        // playableAsset + bindings + exposed references serialize reliably.
        var subSingle = EditorSceneManager.OpenScene(SubPath, OpenSceneMode.Single);
        foreach (var w in Wires)
        {
            WireCell(subSingle, w);
        }

        // Rukhanka's RigDefinitionBaker (FromAnimator mode) derives animationCulling from
        // Animator.cullingMode at bake (animationCulling = cullingMode != AlwaysAnimate). With the
        // Unity default (CullUpdateTransforms) every rig bakes a CullAnimationsTag, and without an
        // AnimationCullingConfig in the scene the AnimationCullingSystem errors out and never clears
        // it -> AnimationProcessSystem computes only the root bone -> the mesh stays at bind pose
        // (T-pose) and never animates. Forcing AlwaysAnimate on the settled subscene (the reliable
        // serialization point) makes the baker skip the cull tag so all bones animate.
        ForceAlwaysAnimate(subSingle);

        EditorSceneManager.MarkSceneDirty(subSingle);
        EditorSceneManager.SaveScene(subSingle);

        // Parent build pass.
        EditorSceneManager.OpenScene(ParentPath, OpenSceneMode.Single);
        var p = EditorSceneManager.GetActiveScene();
        BuildParent();
        EditorSceneManager.MarkSceneDirty(p);
        EditorSceneManager.SaveScene(p);

        AssetDatabase.SaveAssets();
        Debug.Log("AnimationShowcase: built grid at " + ParentPath);
    }

    // ---- Rig recipe (from the verified LookAtValidationSetup) ----
    private static GameObject BuildRig(Scene scene, string name, Vector3 pos)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(RigFbxPath);
        var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        PrefabUtility.UnpackPrefabInstance(go, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
        go.name = name;
        go.transform.position = pos;
        go.transform.rotation = Quaternion.identity;

        var animator = go.GetComponent<Animator>() ?? go.GetComponentInChildren<Animator>();
        var controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(ControllerPath);
        if (controller != null)
        {
            animator.runtimeAnimatorController = controller;
        }

        animator.applyRootMotion = false;
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

        var rig = go.GetComponent<RigDefinitionAuthoring>() ?? go.AddComponent<RigDefinitionAuthoring>();
        rig.applyRootMotion = false;
        rig.animationEngine = RigDefinitionAuthoring.AnimationEngine.CPU;
        rig.animationCulling = false;

        SceneManager.MoveGameObjectToScene(go, scene);
        return go;
    }

    private static void AddIdleFallback(GameObject rig)
    {
        var state = rig.GetComponent<TimelineAnimationStateAuthoring>() ?? rig.AddComponent<TimelineAnimationStateAuthoring>();
        state.fallbackAnimationClip = LoadClip(IdleClip);
        state.fallbackPlaybackMode = FallbackPlaybackMode.Loop;
        state.blendInDuration = 0.25f;
        state.blendOutDuration = 0.25f;
        state.applyFootIK = true;
    }

    private static PlayableDirector MakeDirector(Scene scene, string name, Vector3 pos)
    {
        var go = new GameObject(name);
        go.transform.position = pos;
        var d = go.AddComponent<PlayableDirector>();
        d.playOnAwake = true;
        d.extrapolationMode = DirectorWrapMode.Loop;
        SceneManager.MoveGameObjectToScene(go, scene);
        return d;
    }

    // ---- UC1: Rukhanka single clip (run, looping) ----
    private static void BuildRukhankaColumn(Scene scene, int col)
    {
        var x = ColX(col);
        var rig = BuildRig(scene, "UC1_Hero", new Vector3(x, 0f, 0f));
        AddIdleFallback(rig);
        MakeDirector(scene, "UC1_Director", new Vector3(x, 0f, -1.5f));

        var path = TimelineFolder + "/UC1_Rukhanka.playable";
        var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
        AssetDatabase.CreateAsset(timeline, path);
        var track = timeline.CreateTrack<RukhankaAnimationTrack>(null, "Rukhanka Clip");
        track.LayerIndex = 0;
        track.trackOffset = TrackOffset.ApplyTransformOffsets;
        track.applyAvatarMask = true;

        var run = LoadClip(RunFwd);
        var clip = track.CreateClip<RukhankaAnimationClip>();
        clip.displayName = "Run Forward";
        clip.start = 0;
        clip.duration = run != null ? run.length : 1.0;
        var a = (RukhankaAnimationClip)clip.asset;
        a.animationClipHolder = run;
        a.removeStartOffset = true;
        a.applyFootIK = true;
        Dirty(a);

        FixDuration(timeline);
        Dirty(timeline, track);
        AssetDatabase.SaveAssets();

        Wires.Add(new Wire { DirectorName = "UC1_Director", TimelinePath = path, BindName = "UC1_Hero" });
        Captions.Add(new Caption { Title = "UC1  Rukhanka Clip", Usage = "RukhankaAnimationTrack + clip\nsingle clip (run) loops on the rig", Pos = new Vector3(x, 3.0f, 0f), Color = RukhankaColor });
    }

    // ---- UC2: BlendTree2D (ClipValue self-contained, blend sweeps idle->run->strafe) ----
    private static void BuildBlendColumn(Scene scene, int col)
    {
        var x = ColX(col);
        var rig = BuildRig(scene, "UC2_Hero", new Vector3(x, 0f, 0f));
        AddIdleFallback(rig);
        MakeDirector(scene, "UC2_Director", new Vector3(x, 0f, -1.5f));

        var path = TimelineFolder + "/UC2_BlendTree2D.playable";
        var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
        AssetDatabase.CreateAsset(timeline, path);
        var track = timeline.CreateTrack<BlendTree2DTrack>(null, "Blend Tree 2D");
        track.BlendTreeType = Rukhanka.MotionBlob.Type.BlendTree2DFreeformCartesian;
        track.LayerIndex = 0;
        track.trackOffset = TrackOffset.ApplyTransformOffsets;
        track.applyAvatarMask = true;
        track.Motions = new List<BlendTree2DTrack.BlendTree2DMotionEntry>
        {
            Motion(IdleClip, 0f, 0f),
            Motion(RunFwd, 0f, 1f),
            Motion(StrafeR, 90f, 1f),
            Motion(StrafeL, -90f, 1f),
            Motion(WalkBack, 180f, 1f),
        };

        // ClipValue mode: self-contained. Sweep the 2D blend parameter so the pose
        // travels idle -> forward -> strafe-right -> back over the loop.
        AddBlendClip(track, 0.0, 1.2, "idle", new Vector2(0f, 0f));
        AddBlendClip(track, 1.2, 1.4, "run fwd", new Vector2(0f, 1f));
        AddBlendClip(track, 2.6, 1.4, "strafe R", new Vector2(1f, 0.2f));
        AddBlendClip(track, 4.0, 1.4, "walk back", new Vector2(0f, -1f));

        FixDuration(timeline);
        Dirty(timeline, track);
        AssetDatabase.SaveAssets();

        Wires.Add(new Wire { DirectorName = "UC2_Director", TimelinePath = path, BindName = "UC2_Hero" });
        Captions.Add(new Caption { Title = "UC2  Blend Tree 2D", Usage = "BlendTree2DTrack (ClipValue)\nblend param sweeps idle/run/strafe/back", Pos = new Vector3(x, 3.0f, 0f), Color = BlendColor });
    }

    private static BlendTree2DTrack.BlendTree2DMotionEntry Motion(string clipPath, float deg, float range)
    {
        var rad = deg * Mathf.Deg2Rad;
        return new BlendTree2DTrack.BlendTree2DMotionEntry
        {
            clip = LoadClip(clipPath),
            degreeCalc = deg,
            rangeCalc = range,
            directionCalc = new Vector2(Mathf.Sin(rad) * range, Mathf.Cos(rad) * range),
        };
    }

    private static void AddBlendClip(BlendTree2DTrack track, double start, double dur, string name, Vector2 param)
    {
        var clip = track.CreateClip<BlendTree2DClip>();
        clip.start = start;
        clip.duration = dur;
        clip.displayName = name;
        clip.blendInDuration = 0.35;
        var a = (BlendTree2DClip)clip.asset;
        a.ReadKind = BlendDirectionReadKind.ClipValue;
        a.BlendParameter = new Unity.Mathematics.float2(param.x, param.y);
        a.removeStartOffset = true;
        a.applyFootIK = true;
        Dirty(a);
    }

    // ---- UC3: AfterImage (short clips spawn the ghost prefab in series) ----
    private static void BuildAfterImageColumn(Scene scene, int col)
    {
        var x = ColX(col);
        var rig = BuildRig(scene, "UC3_Hero", new Vector3(x, 0f, 0f));
        AddIdleFallback(rig);
        // Give the source a run so the ghost freezes a dynamic pose.
        MakeDirector(scene, "UC3_Director", new Vector3(x, 0f, -1.5f));

        var path = TimelineFolder + "/UC3_AfterImage.playable";
        var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
        AssetDatabase.CreateAsset(timeline, path);

        // A run track underneath so the source actually moves while ghosts spawn.
        var runTrack = timeline.CreateTrack<RukhankaAnimationTrack>(null, "Source Run");
        runTrack.LayerIndex = 0;
        runTrack.trackOffset = TrackOffset.ApplyTransformOffsets;
        runTrack.applyAvatarMask = true;
        var runRef = LoadClip(RunFwd);
        var rc = runTrack.CreateClip<RukhankaAnimationClip>();
        rc.displayName = "Run (source)";
        rc.start = 0;
        rc.duration = 4.0;
        var rca = (RukhankaAnimationClip)rc.asset;
        rca.animationClipHolder = runRef;
        rca.removeStartOffset = true;
        rca.applyFootIK = true;
        Dirty(rca);

        var ghost = AssetDatabase.LoadAssetAtPath<GameObject>(GhostPrefabPath);
        var track = timeline.CreateTrack<AfterImageTrack>(null, "After Image");
        track.afterImagePrefab = ghost;

        // Several short clips in series => a trail of frozen poses.
        for (int i = 0; i < 8; i++)
        {
            var clip = track.CreateClip<AfterImageClip>();
            clip.start = 0.3 + i * 0.45;
            clip.duration = 0.18;
            clip.displayName = "ghost " + i;
        }

        FixDuration(timeline);
        Dirty(timeline, track, runTrack);
        AssetDatabase.SaveAssets();

        Wires.Add(new Wire { DirectorName = "UC3_Director", TimelinePath = path, BindName = "UC3_Hero" });
        Captions.Add(new Caption { Title = "UC3  After Image", Usage = "AfterImageTrack spawns ghost prefab\nfrozen pose copies while clips active", Pos = new Vector3(x, 3.0f, 0f), Color = AfterImageColor });
    }

    // ---- UC4: WeaponAnchor (sword bound, anchored to a hand bone) ----
    private static void BuildWeaponColumn(Scene scene, int col)
    {
        var x = ColX(col);
        var rig = BuildRig(scene, "UC4_Hero", new Vector3(x, 0f, 0f));
        AddIdleFallback(rig);

        var animator = rig.GetComponent<Animator>();
        var hand = animator.GetBoneTransform(HumanBodyBones.RightHand);
        var handPath = ChildPath(rig.transform, hand);

        // The weapon: a sword-like cube. Pure mesh (no classic collider), with the
        // WeaponAnchorTargetAuthoring so it gets the sample buffer.
        var sword = GameObject.CreatePrimitive(PrimitiveType.Cube);
        sword.name = "UC4_Sword";
        Object.DestroyImmediate(sword.GetComponent<Collider>());
        sword.transform.position = new Vector3(x + 0.6f, 1.0f, 0f);
        sword.transform.localScale = new Vector3(0.08f, 0.08f, 0.9f);
        sword.GetComponent<MeshRenderer>().sharedMaterial = MakeMaterial("Sword", WeaponColor);
        sword.AddComponent<WeaponAnchorTargetAuthoring>();
        SceneManager.MoveGameObjectToScene(sword, scene);

        MakeDirector(scene, "UC4_Director", new Vector3(x, 0f, -1.5f));

        var path = TimelineFolder + "/UC4_WeaponAnchor.playable";
        var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
        AssetDatabase.CreateAsset(timeline, path);

        // Trackless WeaponAnchorClip on a stock Timeline PlayableTrack (generic clip host).
        var track = timeline.CreateTrack<PlayableTrack>(null, "Weapon Anchor");
        var exposed = new PropertyName(System.Guid.NewGuid().ToString());
        var clip = track.CreateClip<WeaponAnchorClip>();
        clip.displayName = "Anchor to Right Hand";
        clip.start = 0;
        clip.duration = 4.0;
        var a = (WeaponAnchorClip)clip.asset;
        a.bone = new ExposedReference<Transform> { exposedName = exposed };
        a.localPosition = new Vector3(0f, 0f, 0.35f);
        a.localRotationEuler = Vector3.zero;
        Dirty(a);

        FixDuration(timeline);
        Dirty(timeline, track);
        AssetDatabase.SaveAssets();

        Wires.Add(new Wire
        {
            DirectorName = "UC4_Director",
            TimelinePath = path,
            BindWeapon = true,
            WeaponBindName = "UC4_Sword",
            ExposedName = exposed,
            HasExposed = true,
            ExposedBoneOwner = "UC4_Hero",
            ExposedBonePath = handPath,
        });
        Captions.Add(new Caption { Title = "UC4  Weapon Anchor", Usage = "WeaponAnchorClip binds the SWORD\nblends sword onto right-hand bone", Pos = new Vector3(x, 3.0f, 0f), Color = WeaponColor });
    }

    // ---- UC5: Look At (StaticWorld sweep left<->right + idle so the body is alive) ----
    private static void BuildLookAtColumn(Scene scene, int col)
    {
        var x = ColX(col);
        var rig = BuildRig(scene, "UC5_Hero", new Vector3(x, 0f, 0f));
        AddIdleFallback(rig);

        var animator = rig.GetComponent<Animator>();
        var neck = animator.GetBoneTransform(HumanBodyBones.Neck);
        var head = animator.GetBoneTransform(HumanBodyBones.Head);

        var target = new GameObject("UC5_LookTarget");
        target.transform.SetParent(rig.transform, false);
        target.transform.localPosition = new Vector3(0f, 1.6f, 2f);

        var rigLook = rig.GetComponent<CharacterLookAtRigAuthoring>() ?? rig.AddComponent<CharacterLookAtRigAuthoring>();
        rigLook.neckBone = neck;
        rigLook.headBone = head;
        rigLook.neckWeight = 0.4f;
        rigLook.headWeight = 0.6f;
        rigLook.forwardVector = Vector3.forward;
        rigLook.angleLimitMin = -80f;
        rigLook.angleLimitMax = 80f;
        rigLook.lookAtTarget = target.transform;

        // AimIKAuthoring on the head bone (what the rig wizard writes; required for the IK).
        var aimType = System.Type.GetType("Rukhanka.Hybrid.AimIKAuthoring, Rukhanka.Hybrid");
        if (aimType != null)
        {
            var aim = head.GetComponent(aimType) ?? head.gameObject.AddComponent(aimType);
            var so = new SerializedObject(aim);
            var tp = so.FindProperty("target");
            if (tp != null) tp.objectReferenceValue = target.transform;
            var fv = so.FindProperty("forwardVector");
            if (fv != null) fv.vector3Value = Vector3.forward;
            var lo = so.FindProperty("angleLimitMin");
            if (lo != null) lo.floatValue = -80f;
            var hi = so.FindProperty("angleLimitMax");
            if (hi != null) hi.floatValue = 80f;
            var w = so.FindProperty("weight");
            if (w != null) w.floatValue = 0f;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(aim);
        }

        MakeDirector(scene, "UC5_Director", new Vector3(x, 0f, -1.5f));

        var path = TimelineFolder + "/UC5_LookAt.playable";
        var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
        AssetDatabase.CreateAsset(timeline, path);
        var track = timeline.CreateTrack<CharacterLookAtTrack>(null, "Look At");

        // Sweep: look right, then left, then forward-up. Head visibly turns each loop.
        AddLookClip(track, 0.0, 1.6, "look right", new Vector3(2.2f, 2.4f, 2.4f));
        AddLookClip(track, 1.8, 1.6, "look left", new Vector3(-2.2f, 2.4f, 2.4f));
        AddLookClip(track, 3.6, 1.4, "look up", new Vector3(0f, 3.2f, 2.4f));

        FixDuration(timeline);
        Dirty(timeline, track);
        AssetDatabase.SaveAssets();

        Wires.Add(new Wire { DirectorName = "UC5_Director", TimelinePath = path, BindName = "UC5_Hero" });
        Captions.Add(new Caption { Title = "UC5  Look At", Usage = "CharacterLookAtTrack (StaticWorld)\nhead/neck AimIK sweeps R/L/up", Pos = new Vector3(x, 3.0f, 0f), Color = LookAtColor });
    }

    private static void AddLookClip(CharacterLookAtTrack track, double start, double dur, string name, Vector3 point)
    {
        var clip = track.CreateClip<CharacterLookAtClip>();
        clip.start = start;
        clip.duration = dur;
        clip.displayName = name;
        clip.blendInDuration = 0.4;
        var a = (CharacterLookAtClip)clip.asset;
        var so = new SerializedObject(a);
        so.FindProperty("sourceMode").enumValueIndex = (int)PointSourceMode.StaticWorld;
        so.FindProperty("staticWorldPoint").vector3Value = point;
        so.FindProperty("weight").floatValue = 1f;
        so.FindProperty("angleLimitMin").floatValue = -80f;
        so.FindProperty("angleLimitMax").floatValue = 80f;
        so.ApplyModifiedPropertiesWithoutUndo();
        Dirty(a);
    }

    // ---- UC6: Default idle via TimelineAnimationStateAuthoring (NO active clip) ----
    private static void BuildIdleColumn(Scene scene, int col)
    {
        var x = ColX(col);
        var rig = BuildRig(scene, "UC6_Hero", new Vector3(x, 0f, 0f));
        // The whole point: just TimelineAnimationStateAuthoring fallback, no director clip.
        AddIdleFallback(rig);
        Captions.Add(new Caption { Title = "UC6  Default Idle", Usage = "TimelineAnimationStateAuthoring\nidle fallback (no active timeline clip)", Pos = new Vector3(x, 3.0f, 0f), Color = IdleColor });
    }

    // ---- Ghost prefab for UC3 (same FBX + rig, CPU engine, shares the avatar) ----
    private static void BuildGhostPrefab()
    {
        if (AssetDatabase.LoadAssetAtPath<GameObject>(GhostPrefabPath) != null)
        {
            return;
        }

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(RigFbxPath);
        var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        PrefabUtility.UnpackPrefabInstance(go, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
        go.name = "AfterImageGhost";

        var animator = go.GetComponent<Animator>() ?? go.GetComponentInChildren<Animator>();
        animator.applyRootMotion = false;
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

        var rig = go.GetComponent<RigDefinitionAuthoring>() ?? go.AddComponent<RigDefinitionAuthoring>();
        rig.applyRootMotion = false;
        rig.animationEngine = RigDefinitionAuthoring.AnimationEngine.CPU;
        rig.animationCulling = false;

        PrefabUtility.SaveAsPrefabAsset(go, GhostPrefabPath);
        Object.DestroyImmediate(go);
    }

    private static void ForceAlwaysAnimate(Scene scene)
    {
        foreach (var root in scene.GetRootGameObjects())
        {
            var animator = root.GetComponent<Animator>();
            if (animator == null)
            {
                continue;
            }

            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            EditorUtility.SetDirty(animator);
        }
    }

    // ---- Wiring (single active sub-scene) ----
    private static void WireCell(Scene scene, Wire w)
    {
        var dirGo = Find(scene, w.DirectorName);
        if (dirGo == null) { Debug.LogError("AnimationShowcase: director missing " + w.DirectorName); return; }
        var director = dirGo.GetComponent<PlayableDirector>();
        var timeline = AssetDatabase.LoadAssetAtPath<TimelineAsset>(w.TimelinePath);
        director.playableAsset = timeline;
        director.playOnAwake = true;
        director.extrapolationMode = DirectorWrapMode.Loop;

        // Bind every DOTSTrack: animation tracks -> the rig's Animator; weapon -> sword.
        foreach (var track in timeline.GetOutputTracks())
        {
            if (w.BindWeapon)
            {
                var sword = Find(scene, w.WeaponBindName);
                director.SetGenericBinding(track, sword.transform);
            }
            else
            {
                var rig = Find(scene, w.BindName);
                var animator = rig.GetComponent<Animator>() ?? rig.GetComponentInChildren<Animator>();
                director.SetGenericBinding(track, animator);
            }
        }

        if (w.HasExposed)
        {
            var owner = Find(scene, w.ExposedBoneOwner);
            var bone = string.IsNullOrEmpty(w.ExposedBonePath) ? owner.transform : owner.transform.Find(w.ExposedBonePath);
            director.SetReferenceValue(w.ExposedName, bone);
        }

        EditorUtility.SetDirty(director);
    }

    // ---- Helpers ----
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

    private static string ChildPath(Transform root, Transform child)
    {
        if (child == null || child == root)
        {
            return string.Empty;
        }
        var stack = new List<string>();
        var t = child;
        while (t != null && t != root)
        {
            stack.Add(t.name);
            t = t.parent;
        }
        stack.Reverse();
        return string.Join("/", stack);
    }

    private static GameObject Find(Scene scene, string name)
    {
        foreach (var root in scene.GetRootGameObjects())
        {
            if (root.name == name) return root;
        }
        return null;
    }

    private static float ColX(int col)
    {
        return (col - 2.5f) * ColStep;
    }

    private static void FixDuration(TimelineAsset timeline)
    {
        var end = 0.0;
        foreach (var track in timeline.GetOutputTracks())
        {
            foreach (var clip in track.GetClips())
            {
                var e = clip.start + clip.duration;
                if (e > end) end = e;
            }
        }
        timeline.durationMode = TimelineAsset.DurationMode.FixedLength;
        timeline.fixedDuration = end;
    }

    private static void BuildGround()
    {
        var ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
        ground.name = "Ground";
        Object.DestroyImmediate(ground.GetComponent<Collider>());
        ground.transform.position = new Vector3(0f, -0.05f, 1.0f);
        ground.transform.localScale = new Vector3(30f, 0.1f, 8f);
        ground.GetComponent<MeshRenderer>().sharedMaterial = MakeMaterial("Ground", new Color(0.30f, 0.32f, 0.37f));
    }

    private static void BuildParent()
    {
        FrameCamera();
        RenderSettings.fog = false;

        MakeBanner("Title_Banner", new Vector3(0f, 5.6f, 0f), new Vector3(22f, 1.6f, 0.1f));
        MakeWorldLabel("Title", "ANIMATION TIMELINE GRID", new Vector3(0f, 5.7f, -0.3f), 22f, Color.white, 4.2f, TextAlignmentOptions.Center);
        MakeWorldLabel("Subtitle", "Rukhanka-driven DOTS animation  ·  com.bovinelabs.timeline.animation", new Vector3(0f, 4.9f, -0.3f), 22f, new Color(0.85f, 0.9f, 1f), 1.6f, TextAlignmentOptions.Center);

        foreach (var c in Captions)
        {
            MakeCaption(c.Title, c.Usage, c.Pos, c.Color);
        }

        MakeBanner("Usage_Banner", new Vector3(0f, 0.55f, -3.4f), new Vector3(26f, 1.3f, 0.1f));
        MakeWorldLabel("Usage", "one rigged Hero (Arvex_RIG2) per use case  ·  each director loops (FixedLength)  ·  every Hero idles via TimelineAnimationStateAuthoring between clips", new Vector3(0f, 0.55f, -3.6f), 24f, new Color(0.96f, 0.97f, 1f), 1.35f, TextAlignmentOptions.Center);

        var sceneAsset = AssetDatabase.LoadAssetAtPath<SceneAsset>(SubPath);
        if (sceneAsset == null)
        {
            Debug.LogError("AnimationShowcase: sub-scene asset missing at " + SubPath);
            return;
        }
        var subGo = new GameObject("Showcase SubScene");
        var subScene = subGo.AddComponent<SubScene>();
        subScene.SceneAsset = sceneAsset;
        subScene.AutoLoadScene = true;
        EditorUtility.SetDirty(subScene);
    }

    private static void MakeCaption(string title, string usage, Vector3 pos, Color color)
    {
        MakeBanner("CapBanner_" + title, pos + new Vector3(0f, 0f, 0.06f), new Vector3(4.0f, 1.5f, 0.05f));
        MakeWorldLabel("Cap_" + title, "<b>" + title + "</b>", pos + new Vector3(0f, 0.42f, 0f), 3.9f, color, 1.7f, TextAlignmentOptions.Center);
        MakeWorldLabel("Use_" + title, usage, pos + new Vector3(0f, -0.35f, 0f), 3.9f, new Color(0.95f, 0.96f, 1f), 1.0f, TextAlignmentOptions.Center);
    }

    private static void FrameCamera()
    {
        var required = GameObject.Find("Required In Scene");
        Transform camTransform = null;
        if (required != null)
        {
            camTransform = required.transform.Find("Main Camera");
        }
        if (camTransform == null)
        {
            var camGo = GameObject.Find("Main Camera");
            if (camGo != null) camTransform = camGo.transform;
        }
        if (camTransform == null)
        {
            var anyCam = Object.FindFirstObjectByType<Camera>();
            if (anyCam != null) camTransform = anyCam.transform;
        }
        if (camTransform == null) return;

        camTransform.position = CameraPos;
        camTransform.rotation = Quaternion.Euler(12f, 0f, 0f);
        var cam = camTransform.GetComponent<Camera>();
        if (cam != null)
        {
            cam.fieldOfView = 60f;
            cam.farClipPlane = 200f;
            EditorUtility.SetDirty(cam);
        }
        EditorUtility.SetDirty(camTransform);
    }

    private static void MakeBanner(string name, Vector3 pos, Vector3 size)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        Object.DestroyImmediate(go.GetComponent<Collider>());
        go.transform.position = pos;
        go.transform.rotation = Quaternion.LookRotation(pos - CameraPos, Vector3.up);
        go.transform.localScale = size;
        go.GetComponent<MeshRenderer>().sharedMaterial = MakeMaterial(name, BannerColor);
    }

    private static void MakeWorldLabel(string name, string text, Vector3 pos, float width, Color color, float fontSize, TextAlignmentOptions alignment)
    {
        var holder = new GameObject(name);
        holder.transform.position = pos;
        holder.transform.rotation = Quaternion.LookRotation(pos - CameraPos, Vector3.up);
        var go = new GameObject("Text");
        go.transform.SetParent(holder.transform, false);
        var tmp = go.AddComponent<TextMeshPro>();
        tmp.text = text;
        tmp.fontSize = fontSize;
        tmp.color = color;
        tmp.alignment = alignment;
        tmp.rectTransform.sizeDelta = new Vector2(width, 4f);
        tmp.rectTransform.localPosition = Vector3.zero;
        tmp.fontStyle = FontStyles.Bold;
    }

    private static Material MakeMaterial(string name, Color color)
    {
        var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        var mat = new Material(shader) { name = name + "_Mat" };
        mat.color = color;
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
        return mat;
    }

    private static void EnsureFolders()
    {
        if (!AssetDatabase.IsValidFolder("Assets/Samples")) AssetDatabase.CreateFolder("Assets", "Samples");
        if (!AssetDatabase.IsValidFolder(SampleFolder)) AssetDatabase.CreateFolder("Assets/Samples", "AnimationShowcase");
        if (!AssetDatabase.IsValidFolder(TimelineFolder)) AssetDatabase.CreateFolder(SampleFolder, "Timelines");
    }

    private static void ResetAssets()
    {
        if (AssetDatabase.IsValidFolder(TimelineFolder))
        {
            foreach (var guid in AssetDatabase.FindAssets("t:TimelineAsset", new[] { TimelineFolder }))
            {
                AssetDatabase.DeleteAsset(AssetDatabase.GUIDToAssetPath(guid));
            }
        }
        foreach (var p in new[] { ParentPath, SubPath })
        {
            if (AssetDatabase.LoadAssetAtPath<Object>(p) != null) AssetDatabase.DeleteAsset(p);
        }
    }

    private static void Dirty(params Object[] objs)
    {
        foreach (var o in objs)
        {
            if (o != null) EditorUtility.SetDirty(o);
        }
    }
}
