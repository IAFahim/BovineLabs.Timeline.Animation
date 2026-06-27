using System.Collections.Generic;
using System.Linq;
using BovineLabs.Timeline.Animation;
using BovineLabs.Timeline.Animation.Authoring;
using Rukhanka.Hybrid;
using Unity.Mathematics;
using Unity.Scenes;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using UnityEngine.Timeline;

/// <summary>
/// Builds the committed CharacterLookAt (AimIK) validation demo under Assets/Samples/LookAtValidation/.
/// Parent scene + SubScene containing: a Rukhanka-rigged humanoid (Arvex_RIG2 FBX) with
/// RigDefinitionAuthoring + CharacterLookAtRigAuthoring + AimIKAuthoring on the head bone,
/// a dedicated look-at target object, and a PlayableDirector bound to LookAt.playable
/// (one CharacterLookAtTrack, one CharacterLookAtClip in StaticWorld mode).
/// Mirrors SlowMoDemoSetup's open/build/save/restore pattern.
/// </summary>
public static class LookAtValidationSetup
{
    private const string Folder = "Assets/Samples/LookAtValidation";
    private const string ParentScenePath = Folder + "/LookAtValidation.unity";
    private const string SubScenePath = Folder + "/LookAtValidation_Sub.unity";
    private const string TimelinePath = Folder + "/LookAt.playable";
    private const string RigFbxPath = "Packages/com.bovinelabs.polygon/Arvex_RIG2.fbx";
    private const string ControllerPath = "Packages/com.bovinelabs.polygon/AC_Polygon_Masculine.controller";

    // Static world point the character should look at. The head rest-forward is +Z; this point
    // is forward + right + slightly up, ~40 deg off forward in yaw, comfortably inside the
    // +-80 deg aim cone so the head can actually reach it (a 90 deg point would clamp).
    // Head sits at ~(0,1.9,0); dir to this point ~= (0.62, 0.18, 0.76).
    private static readonly Vector3 LookPoint = new Vector3(2.2f, 2.55f, 2.7f);

    [MenuItem("Tools/LookAt Validation/Build")]
    public static void Build()
    {
        if (!AssetDatabase.IsValidFolder(Folder))
        {
            AssetDatabase.CreateFolder("Assets/Samples", "LookAtValidation");
        }

        var timeline = BuildTimeline(out var track);

        // 1. Parent scene with a SubScene + camera + light.
        var parent = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
        EditorSceneManager.SaveScene(parent, ParentScenePath);

        // 2. Author the SubScene contents in a fresh additive scene, then attach as a SubScene.
        var sub = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
        EditorSceneManager.SaveScene(sub, SubScenePath);
        EditorSceneManager.SetActiveScene(sub);

        var director = BuildSubSceneContents(sub, timeline, track);

        EditorSceneManager.MarkSceneDirty(sub);
        EditorSceneManager.SaveScene(sub);

        // Bind the director generic binding now (binding lives with the director GO in the SubScene).
        EditorSceneManager.SetActiveScene(parent);

        // 3. Wire the SubScene asset into the parent scene.
        var subAsset = AssetDatabase.LoadAssetAtPath<SceneAsset>(SubScenePath);
        var subGo = new GameObject("LookAtValidation SubScene");
        var subComp = subGo.AddComponent<SubScene>();
        subComp.SceneAsset = subAsset;
        subComp.AutoLoadScene = true;
        SceneManager.MoveGameObjectToScene(subGo, parent);

        // Frame the camera on the character.
        var cam = Camera.main;
        if (cam != null)
        {
            cam.transform.position = new Vector3(-1.5f, 2.0f, -4.0f);
            cam.transform.rotation = Quaternion.Euler(8f, 25f, 0f);
            cam.farClipPlane = 100f;
        }

        EditorSceneManager.MarkSceneDirty(parent);
        EditorSceneManager.SaveScene(parent);

        EditorSceneManager.CloseScene(sub, true);

        // Re-affirm the director binding + playable asset directly on the saved SubScene.
        // PlayableDirector.playableAsset + the generic binding only serialize reliably when the
        // SubScene is opened as the ACTIVE single scene; assigning on an inactive additive scene
        // (as above) silently fails to persist. This opens it Single, rebinds, and re-saves.
        RebindDirector(timeline, track);

        // Restore the parent scene as the open scene.
        EditorSceneManager.OpenScene(ParentScenePath, OpenSceneMode.Single);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log($"LookAtValidation: built. Parent={ParentScenePath} Sub={SubScenePath} Timeline={TimelinePath} Director={(director != null)}");
    }

    private static TimelineAsset BuildTimeline(out CharacterLookAtTrack track)
    {
        if (AssetDatabase.LoadAssetAtPath<TimelineAsset>(TimelinePath) != null)
        {
            AssetDatabase.DeleteAsset(TimelinePath);
        }

        var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
        AssetDatabase.CreateAsset(timeline, TimelinePath);

        track = timeline.CreateTrack<CharacterLookAtTrack>(null, "Look At");
        var clip = track.CreateClip<CharacterLookAtClip>();
        clip.start = 1.0;            // 1s of "no clip" so weight relaxes to ~0 at the start (relax proof baseline)
        clip.duration = 5.0;
        clip.displayName = "Look At Static Point";

        var asset = (CharacterLookAtClip)clip.asset;
        var so = new SerializedObject(asset);
        so.FindProperty("sourceMode").enumValueIndex = (int)PointSourceMode.StaticWorld;
        so.FindProperty("staticWorldPoint").vector3Value = LookPoint;
        so.FindProperty("weight").floatValue = 1f;
        so.FindProperty("angleLimitMin").floatValue = -80f;
        so.FindProperty("angleLimitMax").floatValue = 80f;
        so.ApplyModifiedPropertiesWithoutUndo();

        EditorUtility.SetDirty(asset);
        EditorUtility.SetDirty(timeline);
        AssetDatabase.SaveAssets();
        return timeline;
    }

    private static void RebindDirector(TimelineAsset timeline, CharacterLookAtTrack track)
    {
        // Open Single (active scene) so PlayableDirector.playableAsset + the generic binding
        // serialize reliably — assigning on an inactive additive scene does not persist.
        var sub = EditorSceneManager.OpenScene(SubScenePath, OpenSceneMode.Single);

        GameObject dirGo = null;
        GameObject charGo = null;
        foreach (var go in sub.GetRootGameObjects())
        {
            if (go.name == "LookAt Director") dirGo = go;
            if (go.name == "LookAt Character") charGo = go;
        }

        if (dirGo == null || charGo == null)
        {
            return;
        }

        // Reload the asset fresh from disk — the references passed in can be stale after the
        // AssetDatabase save/refresh + scene reload cycles above.
        var freshTimeline = AssetDatabase.LoadAssetAtPath<TimelineAsset>(TimelinePath);
        var freshTrack = freshTimeline.GetOutputTracks().First();

        var animator = charGo.GetComponent<Animator>() ?? charGo.GetComponentInChildren<Animator>();
        var director = dirGo.GetComponent<PlayableDirector>();
        director.playableAsset = freshTimeline;
        director.playOnAwake = true;
        director.extrapolationMode = DirectorWrapMode.Hold;
        director.SetGenericBinding(freshTrack, animator);
        EditorUtility.SetDirty(director);

        EditorSceneManager.MarkSceneDirty(sub);
        EditorSceneManager.SaveScene(sub);
    }

    private static PlayableDirector BuildSubSceneContents(Scene scene, TimelineAsset timeline, CharacterLookAtTrack track)
    {
        // --- The rigged character ---
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(RigFbxPath);
        var rigGo = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        PrefabUtility.UnpackPrefabInstance(rigGo, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
        rigGo.name = "LookAt Character";
        rigGo.transform.position = Vector3.zero;
        rigGo.transform.rotation = Quaternion.identity;

        var animator = rigGo.GetComponent<Animator>();
        if (animator == null)
        {
            animator = rigGo.GetComponentInChildren<Animator>();
        }

        // Rukhanka requires an AnimatorController to bake the rig definition + bone entities.
        var controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(ControllerPath);
        if (controller != null)
        {
            animator.runtimeAnimatorController = controller;
        }

        animator.applyRootMotion = false;

        // RigDefinitionAuthoring (FromAnimator + BoneEntityStrippingMode.None defaults keep all bone entities).
        // Root motion is disabled: the masculine controller's default state drives root motion that
        // produces a NaN root transform on a stationary demo character, poisoning the bone LTW chain.
        var rigDef = rigGo.GetComponent<RigDefinitionAuthoring>();
        if (rigDef == null)
        {
            rigDef = rigGo.AddComponent<RigDefinitionAuthoring>();
        }

        rigDef.applyRootMotion = false;

        // CPU animation engine: the GPU engine computes bone poses on-GPU and does NOT write the
        // IK-corrected pose back to entity LocalTransform, so the head aim is unreadable from ECS.
        // CPU mode writes the final (animation + AimIK) pose to each bone entity's LocalTransform,
        // making the head's world rotation measurable for validation.
        rigDef.animationEngine = RigDefinitionAuthoring.AnimationEngine.CPU;

        var neck = animator.GetBoneTransform(HumanBodyBones.Neck);
        var head = animator.GetBoneTransform(HumanBodyBones.Head);

        // Dedicated per-character look-at target, parented under the rig root.
        var targetGo = new GameObject("LookAt Character_LookAtTarget");
        targetGo.transform.SetParent(rigGo.transform, false);
        targetGo.transform.localPosition = new Vector3(0f, 1.6f, 2f);

        // CharacterLookAtRigAuthoring (wizard component): its baker adds CharacterLookAtTarget to the animator entity.
        var rig = rigGo.GetComponent<CharacterLookAtRigAuthoring>();
        if (rig == null)
        {
            rig = rigGo.AddComponent<CharacterLookAtRigAuthoring>();
        }

        rig.neckBone = neck;
        rig.headBone = head;
        rig.neckWeight = 0.4f;
        rig.headWeight = 0.6f;
        rig.forwardVector = Vector3.forward; // verified: this Mixamo head's +Z aligns with character forward (dot=1.0)
        rig.angleLimitMin = -80f;
        rig.angleLimitMax = 80f;
        rig.lookAtTarget = targetGo.transform;

        // AimIKAuthoring on the HEAD bone (what the "Build Look-At Rig" button writes).
        var aim = head.GetComponent<AimIKAuthoring>();
        if (aim == null)
        {
            aim = head.gameObject.AddComponent<AimIKAuthoring>();
        }

        aim.target = targetGo.transform;
        aim.forwardVector = rig.forwardVector;
        aim.angleLimitMin = rig.angleLimitMin;
        aim.angleLimitMax = rig.angleLimitMax;
        aim.weight = 0f; // starts relaxed; the track drives it

        var bones = new List<WeightedTransform>();
        if (neck != null)
        {
            bones.Add(new WeightedTransform { bone = neck, weight = rig.neckWeight });
        }

        bones.Add(new WeightedTransform { bone = head, weight = rig.headWeight });
        aim.affectedBones = bones.ToArray();

        // --- Director ---
        var dirGo = new GameObject("LookAt Director");
        var director = dirGo.AddComponent<PlayableDirector>();
        director.playableAsset = timeline;
        director.playOnAwake = true;
        director.extrapolationMode = DirectorWrapMode.Hold;
        director.SetGenericBinding(track, animator);

        SceneManager.MoveGameObjectToScene(rigGo, scene);
        SceneManager.MoveGameObjectToScene(dirGo, scene);

        Debug.Log($"LookAtValidation: rig neck={(neck != null ? neck.name : "null")} head={(head != null ? head.name : "null")} aimBones={aim.affectedBones.Length} forward={rig.forwardVector}");
        return director;
    }
}
