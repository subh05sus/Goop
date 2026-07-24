using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Goop.EditorTools
{
    /// <summary>
    /// One-click pipeline: swap the Player prefab's visual to goop_character.fbx.
    /// Menu: Goop > Complete Character Swap. Steps:
    ///   1. force-reimport the FBX + set Walking/Running to loop
    ///   2. rebuild GoopCharacterAnimator.controller in place (Idle/Walk/Run + 18 pose states)
    ///   3. render pose preview thumbnails to Assets/_Goop/Resources/PosePreviews
    ///   4. replace the prefab's visual child and re-add all visual-side components
    /// Written as a menu tool because the live MCP session hit a wedged asset-database state that only
    /// an editor restart clears — run this once after restarting.
    /// </summary>
    public static class CharacterSwapTool
    {
        // Renamed from goop_character.fbx — the original path's Library/GUID entry got poisoned during
        // the 2026-07-24 asset-database wedge (importer permanently null at that path); a fresh path
        // forces a clean import.
        private const string FbxPath = "Assets/_Goop/Art/GoopChar.fbx";
        private const string CtrlPath = "Assets/_Goop/Prefabs/GoopCharacterAnimator.controller";
        private const string PrefabPath = "Assets/_Goop/Prefabs/Player.prefab";
        private const string PreviewDir = "Assets/_Goop/Resources/PosePreviews";
        private const string InputActionsPath = "Assets/InputSystem_Actions.inputactions";
        private const string ClipPrefix = "ChameleonMan|";

        private static readonly string[] PoseNames =
        {
            "A", "BackBend", "Bridge", "CrossLegged", "CrouchedFetal", "CurledUpSit",
            "FetalPose", "HandOnHip", "LayDown", "LeftHandUp", "MermaidSit", "OpenWide",
            "SideLying", "Sit", "Straight", "T", "Tree", "WideSquat"
        };

        [MenuItem("Goop/Complete Character Swap")]
        public static void Run()
        {
            // --- 1. import + loop flags ---
            AssetDatabase.ImportAsset(FbxPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            var importer = (ModelImporter)AssetImporter.GetAtPath(FbxPath);
            var clipSettings = importer.defaultClipAnimations;
            foreach (var c in clipSettings)
            {
                c.loopTime = c.name.Contains("Walking") || c.name.Contains("Running");
            }
            importer.clipAnimations = clipSettings;
            importer.SaveAndReimport();

            var all = AssetDatabase.LoadAllAssetsAtPath(FbxPath);
            AnimationClip Find(string shortName)
            {
                foreach (var a in all)
                    if (a is AnimationClip ac && ac.name == ClipPrefix + shortName) return ac;
                return null;
            }
            if (Find("Walking") == null)
            {
                Debug.LogError("[CharacterSwap] Clips still not imported — restart the editor and run again.");
                return;
            }

            // --- 2. rebuild controller in place ---
            var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(CtrlPath);
            var sm = ctrl.layers[0].stateMachine;
            foreach (var cs in sm.states) sm.RemoveState(cs.state);
            foreach (var p in ctrl.parameters) ctrl.RemoveParameter(p);
            ctrl.AddParameter("PoseIndex", AnimatorControllerParameterType.Int);
            ctrl.AddParameter("Speed", AnimatorControllerParameterType.Float);

            var idle = sm.AddState("Idle");
            idle.motion = Find("Pose_Straight");
            sm.defaultState = idle;
            var walk = sm.AddState("Walk");
            walk.motion = Find("Walking");
            var run = sm.AddState("Run");
            run.motion = Find("Running");

            void Trans(AnimatorState from, AnimatorState to, AnimatorConditionMode mode, float threshold)
            {
                var t = from.AddTransition(to);
                t.hasExitTime = false;
                t.duration = 0.15f;
                t.AddCondition(mode, threshold, "Speed");
            }
            Trans(idle, walk, AnimatorConditionMode.Greater, 0.1f);
            Trans(walk, idle, AnimatorConditionMode.Less, 0.08f);
            Trans(walk, run, AnimatorConditionMode.Greater, 0.8f);
            Trans(run, walk, AnimatorConditionMode.Less, 0.75f);

            for (int i = 0; i < PoseNames.Length; i++)
            {
                var clip = Find("Pose_" + PoseNames[i]);
                if (clip == null) { Debug.LogError("[CharacterSwap] Missing pose clip " + PoseNames[i]); continue; }
                var st = sm.AddState("Pose_" + PoseNames[i]);
                st.motion = clip;

                var enter = sm.AddAnyStateTransition(st);
                enter.hasExitTime = false;
                enter.duration = 0.15f;
                enter.canTransitionToSelf = false;
                enter.AddCondition(AnimatorConditionMode.Equals, i + 1, "PoseIndex");

                var exit = st.AddTransition(idle);
                exit.hasExitTime = false;
                exit.duration = 0.15f;
                exit.AddCondition(AnimatorConditionMode.Equals, 0, "PoseIndex");
            }
            EditorUtility.SetDirty(ctrl);

            // --- 3. pose preview thumbnails ---
            GeneratePreviews(Find);

            // --- 4. prefab visual swap ---
            SwapPrefabVisual(ctrl);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[CharacterSwap] DONE — controller rebuilt, previews generated, prefab visual swapped.");
        }

        private static void GeneratePreviews(System.Func<string, AnimationClip> find)
        {
            Directory.CreateDirectory(PreviewDir);

            var fbx = AssetDatabase.LoadAssetAtPath<GameObject>(FbxPath);
            var model = Object.Instantiate(fbx, new Vector3(4000f, 4000f, 4000f), Quaternion.identity);
            var camGo = new GameObject("PreviewCam");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.18f, 0.18f, 0.2f, 1f);
            var lightGo = new GameObject("PreviewLight");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            lightGo.transform.rotation = Quaternion.Euler(45f, -30f, 0f);

            var rt = new RenderTexture(128, 128, 24);
            try
            {
                for (int i = 0; i < PoseNames.Length; i++)
                {
                    var clip = find("Pose_" + PoseNames[i]);
                    if (clip == null) continue;
                    clip.SampleAnimation(model, clip.length * 0.99f);

                    Bounds b = new(model.transform.position, Vector3.one);
                    bool first = true;
                    foreach (var r in model.GetComponentsInChildren<Renderer>())
                    {
                        if (first) { b = r.bounds; first = false; }
                        else b.Encapsulate(r.bounds);
                    }

                    float radius = Mathf.Max(b.extents.x, b.extents.y, b.extents.z);
                    cam.transform.position = b.center + new Vector3(0.6f, 0.35f, 1f).normalized * radius * 2.6f;
                    cam.transform.LookAt(b.center);
                    cam.targetTexture = rt;
                    cam.Render();

                    var prevActive = RenderTexture.active;
                    RenderTexture.active = rt;
                    var tex = new Texture2D(128, 128, TextureFormat.RGBA32, false);
                    tex.ReadPixels(new Rect(0, 0, 128, 128), 0, 0);
                    tex.Apply();
                    RenderTexture.active = prevActive;

                    File.WriteAllBytes($"{PreviewDir}/pose_{i + 1:00}.png", tex.EncodeToPNG());
                    Object.DestroyImmediate(tex);
                }
            }
            finally
            {
                cam.targetTexture = null;
                Object.DestroyImmediate(rt);
                Object.DestroyImmediate(model);
                Object.DestroyImmediate(camGo);
                Object.DestroyImmediate(lightGo);
            }
            AssetDatabase.Refresh();
        }

        private static void SwapPrefabVisual(AnimatorController ctrl)
        {
            var root = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                var oldVisual = root.transform.Find("Visual_GoopGuy");
                if (oldVisual != null) Object.DestroyImmediate(oldVisual.gameObject);

                var fbx = AssetDatabase.LoadAssetAtPath<GameObject>(FbxPath);
                var visual = (GameObject)PrefabUtility.InstantiatePrefab(fbx, root.transform);
                visual.name = "Visual_GoopGuy"; // keep the name — several scripts Find() it
                visual.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);

                var animator = visual.GetComponent<Animator>();
                if (animator == null) animator = visual.AddComponent<Animator>();
                animator.runtimeAnimatorController = ctrl;

                var inputActions = AssetDatabase.LoadAssetAtPath<UnityEngine.InputSystem.InputActionAsset>(InputActionsPath);

                visual.AddComponent<Goop.Gameplay.PoseController>();
                visual.AddComponent<Goop.UI.PoseSelectorUI>();
                var skin = visual.AddComponent<Goop.Paint.PaintableSkin>();
                var so = new SerializedObject(skin);
                var prop = so.FindProperty("inputActions");
                if (prop != null) { prop.objectReferenceValue = inputActions; so.ApplyModifiedPropertiesWithoutUndo(); }
                visual.AddComponent<Goop.UI.PaletteUI>();
                visual.AddComponent<Goop.Paint.PaintModeController>();
                visual.AddComponent<Goop.Player.LocomotionAnimator>();

                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }
    }
}
