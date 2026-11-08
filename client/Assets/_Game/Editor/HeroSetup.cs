using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Game.Client.Editor
{
    /// <summary>
    /// Wires up the protagonist model (docs/art/fantasy_hero.blend → Resources/Hero/fantasy_hero.fbx):
    /// Generic rig import with looping Idle/Walk, URP materials per Blender material, and the Animator controller.
    /// Runs as part of "RPG > 1. 프로젝트 설정 적용" so nothing is configured by hand.
    /// </summary>
    public static class HeroSetup
    {
        public const string FbxPath = "Assets/_Game/Resources/Hero/fantasy_hero.fbx";
        public const string FbxFemalePath = "Assets/_Game/Resources/Hero/fantasy_hero_female.fbx";
        const string ControllerPath = "Assets/_Game/Resources/Hero/HeroAnimator.controller";
        const string MatDir = "Assets/_Game/Resources/Materials";

        // Base colors straight from the .blend (linear values) and a lowered metal so they read without reflections.
        struct M { public string Name; public float R, G, B, Metal, Smooth; public bool TwoSided; }
        static readonly M[] Mats =
        {
            new M { Name = "Skin",    R = 0.620f, G = 0.380f, B = 0.280f, Metal = 0f,    Smooth = 0.30f },
            new M { Name = "Cloth",   R = 0.060f, G = 0.060f, B = 0.080f, Metal = 0f,    Smooth = 0.10f },
            new M { Name = "Leather", R = 0.200f, G = 0.100f, B = 0.045f, Metal = 0f,    Smooth = 0.25f },
            new M { Name = "Steel",   R = 0.620f, G = 0.640f, B = 0.670f, Metal = 0.45f, Smooth = 0.55f },
            new M { Name = "Gold",    R = 0.800f, G = 0.550f, B = 0.200f, Metal = 0.45f, Smooth = 0.50f },
            new M { Name = "Eye",     R = 0.020f, G = 0.018f, B = 0.015f, Metal = 0f,    Smooth = 0.15f },
            new M { Name = "Hair",    R = 0.035f, G = 0.022f, B = 0.014f, Metal = 0f,    Smooth = 0.03f, TwoSided = true },
            new M { Name = "Cape",    R = 0.280f, G = 0.025f, B = 0.025f, Metal = 0f,    Smooth = 0.10f, TwoSided = true },
            // Other players get an orange cape so they are easy to tell from you.
            new M { Name = "CapeRemote", R = 0.800f, G = 0.280f, B = 0.030f, Metal = 0f, Smooth = 0.10f, TwoSided = true },
        };

        public static void Apply()
        {
            if (!File.Exists(FbxPath)) { Debug.LogWarning($"[hero] {FbxPath} 가 없어서 주인공 설정을 건너뛰어."); return; }
            ConfigureImporter(FbxPath);
            if (File.Exists(FbxFemalePath)) ConfigureImporter(FbxFemalePath);
            CreateMaterials();
            CreateController();
        }

        static void ConfigureImporter(string path)
        {
            var imp = (ModelImporter)AssetImporter.GetAtPath(path);
            bool dirty = false;
            if (imp.animationType != ModelImporterAnimationType.Generic) { imp.animationType = ModelImporterAnimationType.Generic; dirty = true; }
            if (!imp.importAnimation) { imp.importAnimation = true; dirty = true; }
            if (imp.animationCompression != ModelImporterAnimationCompression.Off) { imp.animationCompression = ModelImporterAnimationCompression.Off; dirty = true; }

            var clips = imp.defaultClipAnimations;
            foreach (var c in clips)
            {
                bool loop = c.name.EndsWith("Idle") || c.name.EndsWith("Walk");
                if (c.loopTime != loop) { c.loopTime = loop; dirty = true; }
                c.lockRootHeightY = false;
            }
            imp.clipAnimations = clips;
            if (dirty) imp.SaveAndReimport();
        }

        static void CreateMaterials()
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) { Debug.LogError("[hero] URP Lit 셰이더가 없어."); return; }
            Directory.CreateDirectory(MatDir);
            foreach (var d in Mats)
            {
                string path = $"{MatDir}/Hero_{d.Name}.mat";
                var m = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (m == null) { m = new Material(shader); AssetDatabase.CreateAsset(m, path); }
                m.shader = shader;
                // Blender base colors are linear; material color fields are authored in gamma.
                m.SetColor("_BaseColor", new Color(d.R, d.G, d.B).gamma);
                m.SetFloat("_Metallic", d.Metal);
                m.SetFloat("_Smoothness", d.Smooth);
                if (d.TwoSided) m.SetFloat("_Cull", 0f); // render both faces (the cape is a thin sheet)
                m.enableInstancing = true;
                EditorUtility.SetDirty(m);
            }
        }

        static void CreateController()
        {
            var clips = new Dictionary<string, AnimationClip>();
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(FbxPath))
                if (o is AnimationClip c && !c.name.StartsWith("__preview__"))
                    foreach (var key in new[] { "Idle", "Walk", "Slash" })
                        if (c.name.EndsWith(key)) clips[key] = c;
            foreach (var key in new[] { "Idle", "Walk", "Slash" })
                if (!clips.ContainsKey(key)) { Debug.LogError($"[hero] FBX에 '{key}' 애니메이션이 없어."); return; }

            if (File.Exists(ControllerPath)) AssetDatabase.DeleteAsset(ControllerPath);
            var ctrl = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
            ctrl.AddParameter("Moving", AnimatorControllerParameterType.Bool);
            ctrl.AddParameter("Slash", AnimatorControllerParameterType.Trigger);
            ctrl.AddParameter("SlashSpeed", AnimatorControllerParameterType.Float);
            ctrl.AddParameter("WalkSpeed", AnimatorControllerParameterType.Float);
            var ps = ctrl.parameters;
            for (int i = 0; i < ps.Length; i++)
            {
                if (ps[i].name == "SlashSpeed") ps[i].defaultFloat = 1f;
                if (ps[i].name == "WalkSpeed") ps[i].defaultFloat = 1.5f;
            }
            ctrl.parameters = ps;

            var sm = ctrl.layers[0].stateMachine;
            var idle = sm.AddState("Idle");
            var walk = sm.AddState("Walk");
            var slash = sm.AddState("Slash");
            idle.motion = clips["Idle"];
            walk.motion = clips["Walk"];
            slash.motion = clips["Slash"];
            walk.speedParameterActive = true; walk.speedParameter = "WalkSpeed";
            slash.speedParameterActive = true; slash.speedParameter = "SlashSpeed";
            sm.defaultState = idle;

            var toWalk = idle.AddTransition(walk);
            toWalk.hasExitTime = false; toWalk.duration = 0.12f;
            toWalk.AddCondition(AnimatorConditionMode.If, 0, "Moving");
            var toIdle = walk.AddTransition(idle);
            toIdle.hasExitTime = false; toIdle.duration = 0.12f;
            toIdle.AddCondition(AnimatorConditionMode.IfNot, 0, "Moving");

            var any = sm.AddAnyStateTransition(slash);
            any.hasExitTime = false; any.duration = 0.05f; any.canTransitionToSelf = true;
            any.AddCondition(AnimatorConditionMode.If, 0, "Slash");
            var back = slash.AddTransition(idle);
            back.hasExitTime = true; back.exitTime = 0.9f; back.duration = 0.1f;

            EditorUtility.SetDirty(ctrl);
            Debug.Log($"[hero] 설정 완료: 클립 Idle {clips["Idle"].length:F2}s / Walk {clips["Walk"].length:F2}s / Slash {clips["Slash"].length:F2}s");
        }

        /// <summary>Batch check: facing direction, size, bones. Run with -executeMethod Game.Client.Editor.HeroSetup.Report.</summary>
        public static void Report()
        {
            Apply();
            AssetDatabase.SaveAssets();
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(FbxPath);
            var go = (GameObject)Object.Instantiate(prefab);
            var smr = go.GetComponentInChildren<SkinnedMeshRenderer>();
            var b = smr.bounds;
            Debug.Log($"[hero-report] bounds center {b.center} size {b.size} animator={(go.GetComponent<Animator>() != null)} mats={smr.sharedMaterials.Length}");
            foreach (var m in smr.sharedMaterials) Debug.Log($"[hero-report] slot material '{(m ? m.name : "null")}'");
            // Facing: the eyes sit on the front of the head. Compare the Eye submesh center with the head center in Z.
            var mesh = smr.sharedMesh;
            int eyeSlot = -1;
            for (int i = 0; i < smr.sharedMaterials.Length; i++) if (smr.sharedMaterials[i] != null && smr.sharedMaterials[i].name.Contains("Eye")) eyeSlot = i;
            if (eyeSlot >= 0)
            {
                var tris = mesh.GetTriangles(eyeSlot); var v = mesh.vertices; var sum = Vector3.zero;
                foreach (var t in tris) sum += v[t];
                sum /= tris.Length;
                var hair = Vector3.zero; int hs = -1;
                for (int i = 0; i < smr.sharedMaterials.Length; i++) if (smr.sharedMaterials[i] != null && smr.sharedMaterials[i].name.Contains("Hair")) hs = i;
                if (hs >= 0) { var ht = mesh.GetTriangles(hs); foreach (var t in ht) hair += v[t]; hair /= ht.Length; }
                Debug.Log($"[hero-report] eye center (mesh space) {sum}  hair center {hair}  → face points toward {(sum.z - hair.z > 0 ? "+Z (good)" : "-Z (needs 180° turn)")}");
            }
            foreach (var a in go.GetComponentsInChildren<Transform>()) if (a.name == "Hips" || a.name == "Head" || a.name == "Hand.R") Debug.Log($"[hero-report] bone {a.name} world {a.position}");
            Object.DestroyImmediate(go);
        }

        /// <summary>Batch render (needs graphics, so no -nographics): follow-camera view and close-ups, saved as PNGs next to the logs.</summary>
        public static void RenderProbe()
        {
            Apply();
            CreatureSetup.Apply();
            AssetDatabase.SaveAssets();
            string outDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "tools", "logs"));
            string json = File.ReadAllText(Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "data", "shared", "maps", "dawn_village.json")));
            var map = JsonUtility.FromJson<Game.Shared.Data.MapDef>(json);
            Game.Client.World.VillageBuilder.Build(map, out _);
            bool female = System.Environment.GetEnvironmentVariable("HERO_GENDER") == "f";
            string prefix = female ? "probe_f_" : "probe_";
            string modelEnv = System.Environment.GetEnvironmentVariable("HERO_MODEL");
            if (!string.IsNullOrEmpty(modelEnv)) prefix = "probe_" + modelEnv + "_";
            var view = !string.IsNullOrEmpty(modelEnv)
                ? Game.Client.World.Placeholders.CreateCreature("Probe", new Game.Shared.Data.VisualDef { model = modelEnv, scale = 1f })
                : Game.Client.World.Placeholders.CreateHero("ProbeHero", false, female ? Game.Shared.Protocol.Gender.Female : Game.Shared.Protocol.Gender.Male);
            var go = view.gameObject;
            string weaponEnv = System.Environment.GetEnvironmentVariable("HERO_WEAPON");
            if (!string.IsNullOrEmpty(weaponEnv)) { view.SetWeaponKind(weaponEnv); prefix += weaponEnv + "_"; }
            var terrain = new Game.Shared.World.MapTerrain(map);
            go.transform.position = new Vector3(map.playerSpawn.x, terrain.Height(map.playerSpawn.x, map.playerSpawn.z), map.playerSpawn.z);

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.55f, 0.62f, 0.72f);
            RenderSettings.ambientEquatorColor = new Color(0.42f, 0.45f, 0.45f);
            RenderSettings.ambientGroundColor = new Color(0.25f, 0.24f, 0.2f);
            var sun = new GameObject("Sun", typeof(Light)).GetComponent<Light>();
            sun.type = LightType.Directional; sun.intensity = 1.2f; sun.color = new Color(1f, 0.96f, 0.88f);
            sun.transform.rotation = Quaternion.Euler(48f, -35f, 0);
            var cam = new GameObject("Cam", typeof(Camera)).GetComponent<Camera>();
            cam.nearClipPlane = 0.2f; cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.62f, 0.74f, 0.86f);

            void Shot(string name, Vector3 pos, Vector3 look, int w, int h)
            {
                cam.transform.position = pos; cam.transform.LookAt(look);
                var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
                cam.targetTexture = rt; cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0); tex.Apply();
                File.WriteAllBytes(Path.Combine(outDir, name + ".png"), tex.EncodeToJPG(90).Length > 0 ? tex.EncodeToPNG() : null);
                RenderTexture.active = null; cam.targetTexture = null;
                Object.DestroyImmediate(rt); Object.DestroyImmediate(tex);
            }

            if (!string.IsNullOrEmpty(modelEnv))
            {
                float h = view.Height, d = Mathf.Max(1.8f, h * 2.2f);
                var at = go.transform.position + new Vector3(0, h * 0.5f, 0);
                Shot(prefix + "front", at + new Vector3(0, h * 0.15f, d), at, 600, 600);
                Shot(prefix + "side", at + new Vector3(d, h * 0.15f, 0), at, 600, 600);
                Debug.Log($"[render] {modelEnv} height {h:F2}");
                return;
            }

            // Exactly the follow camera: yaw 0, pitch 15, distance 6, pivot 1.6 up and 0.6 to the right.
            var rot = Quaternion.Euler(15f, 0f, 0f);
            var pivot = go.transform.position + Vector3.up * 1.6f + rot * Vector3.right * 0.6f;
            Shot(prefix + "follow", pivot + rot * Vector3.back * 6f, pivot + rot * Vector3.forward * 10f, 849, 833);

            // HERO_JUMP=rise|fall|tuck|crouch: the procedural jump pose on top of the Idle clip.
            string jumpEnv = System.Environment.GetEnvironmentVariable("HERO_JUMP");
            if (!string.IsNullOrEmpty(jumpEnv))
            {
                var bodyGo = go.GetComponentInChildren<Animator>().gameObject;
                foreach (var o in AssetDatabase.LoadAllAssetsAtPath(FbxPath))
                    if (o is AnimationClip cc && cc.name.EndsWith("Idle")) cc.SampleAnimation(bodyGo, 0.2f);
                bodyGo.GetComponent<Animator>().enabled = false;
                view.DebugPose(jumpEnv == "rise" || jumpEnv == "fall" ? 1f : 0f, jumpEnv == "tuck" ? 1f : 0f, jumpEnv == "crouch" ? 1f : 0f, jumpEnv == "rise" ? 1f : 0f);
                foreach (var tt in go.GetComponentsInChildren<Transform>(true)) if (tt.name == "UpperArm.R") Debug.Log("[pose2] UpperArm.R " + tt.GetInstanceID() + " " + tt.rotation.eulerAngles + " parent " + tt.parent.name);
                foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>(true)) Debug.Log("[pose2] smr " + smr.name + " bones " + smr.bones.Length + " root " + (smr.rootBone ? smr.rootBone.name : "null") + " updOff " + smr.updateWhenOffscreen);
                var at3 = go.transform.position + new Vector3(0, 1.0f, 0);
                Shot(prefix + "jump_" + jumpEnv + "_front", at3 + new Vector3(0, 0.2f, 3.0f), at3, 500, 600);
                Shot(prefix + "jump_" + jumpEnv + "_side", at3 + new Vector3(3.0f, 0.2f, 0), at3, 500, 600);
                return;
            }

            // Any clip at any time: HERO_CLIP=Idle|Walk|Slash HERO_TIME=seconds (what the Animator really shows in game).
            string clipEnv = System.Environment.GetEnvironmentVariable("HERO_CLIP");
            if (!string.IsNullOrEmpty(clipEnv))
            {
                float tEnv = float.TryParse(System.Environment.GetEnvironmentVariable("HERO_TIME"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var tv) ? tv : 0.3f;
                var bodyGo = go.GetComponentInChildren<Animator>().gameObject;
                foreach (var o in AssetDatabase.LoadAllAssetsAtPath(FbxPath))
                    if (o is AnimationClip cc && cc.name.EndsWith(clipEnv)) cc.SampleAnimation(bodyGo, tEnv);
                var at2 = go.transform.position + new Vector3(0, 1.0f, 0);
                Shot(prefix + clipEnv + "_front", at2 + new Vector3(0, 0.2f, 3.0f), at2, 600, 700);
                Shot(prefix + clipEnv + "_side", at2 + new Vector3(3.0f, 0.2f, 0), at2, 600, 700);
                Shot(prefix + clipEnv + "_back", at2 + new Vector3(0, 0.6f, -3.0f), at2, 600, 700);
                Debug.Log("[render] clip " + clipEnv + " t=" + tEnv);
                return;
            }

            // Look from the front at the hero standing and mid-slash.
            var body = go.GetComponentInChildren<Animator>() ? go.GetComponentInChildren<Animator>().gameObject : go;
            Shot(prefix + "front", go.transform.position + new Vector3(0, 1.2f, 3.2f), go.transform.position + new Vector3(0, 0.95f, 0), 600, 800);
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(FbxPath))
                if (o is AnimationClip c && c.name.EndsWith("Slash"))
                {
                    c.SampleAnimation(body, c.length * 0.45f);
                    Shot(prefix + "slash", go.transform.position + new Vector3(3f, 1.2f, 1.5f), go.transform.position + new Vector3(0, 0.95f, 0.4f), 600, 800);
                }
            Debug.Log("[render] saved to " + outDir);
        }
    }
}
