using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Game.Client.Editor
{
    /// <summary>
    /// Wires up the village creature models (docs/art/village → Resources/Creatures/*.fbx): Generic rig import, URP materials
    /// (Resources/Materials/Cr_*), and one Animator controller per model (Resources/Creatures/&lt;model&gt;_ctrl).
    /// All controllers share the same parameter names so CreatureView can drive any of them:
    /// Moving (bool), Slash (attack trigger), SlashSpeed, Dead (bool), Talk (trigger), WalkSpeed.
    /// Runs as part of "RPG &gt; 1. 프로젝트 설정 적용".
    /// </summary>
    public static class CreatureSetup
    {
        const string Dir = "Assets/_Game/Resources/Creatures";
        const string MatDir = "Assets/_Game/Resources/Materials";

        struct Spec
        {
            public string Model;
            public string Idle, Move, Attack, Death, Talk, Hit;   // clip name suffixes ("" = none)
            public float MoveSpeed;
        }

        static readonly Spec[] Specs =
        {
            new Spec { Model = "village_chief", Idle = "Idle", Talk = "Talk" },
            new Spec { Model = "village_guard", Idle = "Idle", Move = "Walk", Attack = "Thrust", MoveSpeed = 1.5f },
            new Spec { Model = "wolf",          Idle = "Idle", Move = "Run",  Attack = "Bite", Death = "Death", MoveSpeed = 1f },
            new Spec { Model = "rabbit",        Idle = "Idle", Move = "Hop",  Death = "Death", MoveSpeed = 1f },
            // 전직관 7명 + 허수아비 (docs/art/npc, the planner's models): Idle + Talk, the scarecrow Idle + Hit.
            new Spec { Model = "master_sword_east", Idle = "Idle", Talk = "Talk" },
            new Spec { Model = "master_sword_west", Idle = "Idle", Talk = "Talk" },
            new Spec { Model = "master_mage",       Idle = "Idle", Talk = "Talk" },
            new Spec { Model = "master_assassin",   Idle = "Idle", Talk = "Talk" },
            new Spec { Model = "master_warrior",    Idle = "Idle", Talk = "Talk" },
            new Spec { Model = "master_archer",     Idle = "Idle", Talk = "Talk" },
            new Spec { Model = "master_priest",     Idle = "Idle", Talk = "Talk" },
            new Spec { Model = "scarecrow",         Idle = "Idle", Hit = "Hit" },
        };

        // Base colors straight from the .blend (linear). Metal is lowered and emission kept for the glowing bits.
        struct M { public string Name; public float R, G, B, Metal, Smooth; public float ER, EG, EB; }
        static M Mt(string n, float r, float g, float b, float metal, float rough, float er = 0, float eg = 0, float eb = 0)
            => new M { Name = n, R = r, G = g, B = b, Metal = Mathf.Min(metal, 0.45f), Smooth = Mathf.Clamp01((1f - rough) * 0.8f), ER = er, EG = eg, EB = eb };

        static readonly M[] Mats =
        {
            Mt("Rabbit_eye", 0.020f, 0.015f, 0.012f, 0, 0.10f),
            Mt("Rabbit_fur", 0.360f, 0.260f, 0.170f, 0, 0.95f),
            Mt("Rabbit_pink", 0.750f, 0.380f, 0.380f, 0, 0.60f),
            Mt("Rabbit_white", 0.850f, 0.840f, 0.820f, 0, 0.95f),
            Mt("VillageChief_eye", 0.020f, 0.018f, 0.015f, 0, 0.15f),
            Mt("VillageChief_gem", 0.100f, 0.450f, 0.300f, 0.2f, 0.15f, 0.10f * 0.6f, 0.45f * 0.6f, 0.30f * 0.6f),
            Mt("VillageChief_leather", 0.200f, 0.100f, 0.045f, 0, 0.70f),
            Mt("VillageChief_robe", 0.170f, 0.120f, 0.070f, 0, 0.95f),
            Mt("VillageChief_sash", 0.350f, 0.060f, 0.040f, 0, 0.80f),
            Mt("VillageChief_skin", 0.580f, 0.370f, 0.280f, 0, 0.60f),
            Mt("VillageChief_white", 0.780f, 0.760f, 0.720f, 0, 0.80f),
            Mt("VillageChief_wood", 0.240f, 0.140f, 0.070f, 0, 0.85f),
            Mt("VillageGuard_blue", 0.050f, 0.120f, 0.380f, 0, 0.75f),
            Mt("VillageGuard_cloth", 0.100f, 0.120f, 0.200f, 0, 0.90f),
            Mt("VillageGuard_eye", 0.020f, 0.018f, 0.015f, 0, 0.15f),
            Mt("VillageGuard_gold", 0.800f, 0.550f, 0.200f, 1f, 0.35f),
            Mt("VillageGuard_leather", 0.180f, 0.100f, 0.050f, 0, 0.65f),
            Mt("VillageGuard_skin", 0.550f, 0.340f, 0.240f, 0, 0.55f),
            Mt("VillageGuard_steel", 0.550f, 0.570f, 0.600f, 1f, 0.35f),
            Mt("VillageGuard_wood", 0.260f, 0.150f, 0.070f, 0, 0.85f),
            Mt("Wolf_chin", 0.300f, 0.280f, 0.250f, 0, 0.95f),
            Mt("Wolf_dark", 0.040f, 0.035f, 0.030f, 0, 0.50f),
            Mt("Wolf_eye", 0.850f, 0.550f, 0.080f, 0, 0.20f, 0.85f * 1.5f, 0.55f * 1.5f, 0.08f * 1.5f),
            Mt("Wolf_fur", 0.075f, 0.070f, 0.065f, 0, 0.95f),
        };

        public static void Apply()
        {
            CreateMaterials();
            foreach (var s in Specs)
            {
                string fbx = $"{Dir}/{s.Model}.fbx";
                if (!File.Exists(fbx)) { Debug.LogWarning($"[creature] {fbx} 가 없어서 건너뛰어."); continue; }
                ConfigureImporter(fbx, s);
                if (s.Model.StartsWith("master_") || s.Model == "scarecrow") MaterialsFromFbx(fbx);
                CreateController(fbx, s);
            }
        }

        static bool Is(string clip, string suffix) => !string.IsNullOrEmpty(suffix) && clip.EndsWith("|" + suffix) || clip == suffix;

        static void ConfigureImporter(string path, Spec s)
        {
            var imp = (ModelImporter)AssetImporter.GetAtPath(path);
            bool dirty = false;
            if (imp.animationType != ModelImporterAnimationType.Generic) { imp.animationType = ModelImporterAnimationType.Generic; dirty = true; }
            if (!imp.importAnimation) { imp.importAnimation = true; dirty = true; }
            if (imp.animationCompression != ModelImporterAnimationCompression.Off) { imp.animationCompression = ModelImporterAnimationCompression.Off; dirty = true; }
            var clips = imp.defaultClipAnimations;
            foreach (var c in clips)
            {
                bool loop = Is(c.name, s.Idle) || Is(c.name, s.Move);
                if (c.loopTime != loop) { c.loopTime = loop; dirty = true; }
            }
            imp.clipAnimations = clips;
            if (dirty) imp.SaveAndReimport();
        }

        /// <summary>
        /// The new models bring their colors inside the FBX (Blender material base colors). Copy each imported material into
        /// Resources/Materials/Cr_&lt;name&gt; as a URP Lit one so CreatureView picks it up by name. Metal-looking names get a metallic look.
        /// </summary>
        static void MaterialsFromFbx(string fbx)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            Directory.CreateDirectory(MatDir);
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(fbx))
            {
                if (!(o is Material src)) continue;
                string path = $"{MatDir}/Cr_{src.name}.mat";
                var m = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (m == null) { m = new Material(shader); AssetDatabase.CreateAsset(m, path); }
                m.shader = shader;
                Color c = src.HasProperty("_BaseColor") ? src.GetColor("_BaseColor") : src.HasProperty("_Color") ? src.GetColor("_Color") : Color.gray;
                string n = src.name.ToLowerInvariant();
                bool metal = n.Contains("steel") || n.Contains("silver") || n.Contains("iron") || n.Contains("metal") || n.Contains("armor") || n.Contains("gold") || n.Contains("brass");
                bool glow = n.Contains("crystal") || n.Contains("halo") || n.Contains("light") || n.Contains("gem");
                bool eye = n.EndsWith("_eye");
                m.SetColor("_BaseColor", new Color(c.r, c.g, c.b, 1f));
                m.SetFloat("_Metallic", metal ? 0.45f : 0f);
                m.SetFloat("_Smoothness", metal ? 0.6f : eye ? 0.4f : 0.25f);
                if (glow) { m.EnableKeyword("_EMISSION"); m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None; m.SetColor("_EmissionColor", new Color(c.r, c.g, c.b) * 0.6f); }
                m.SetFloat("_Cull", 0f);
                m.enableInstancing = true;
                EditorUtility.SetDirty(m);
                Debug.Log($"[creature] material Cr_{src.name} color {c}");
            }
            AssetDatabase.SaveAssets();
        }

        static void CreateMaterials()
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) { Debug.LogError("[creature] URP Lit 셰이더가 없어."); return; }
            Directory.CreateDirectory(MatDir);
            foreach (var d in Mats)
            {
                string path = $"{MatDir}/Cr_{d.Name}.mat";
                var m = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (m == null) { m = new Material(shader); AssetDatabase.CreateAsset(m, path); }
                m.shader = shader;
                m.SetColor("_BaseColor", new Color(d.R, d.G, d.B).gamma); // Blender colors are linear; material colors are authored in gamma
                m.SetFloat("_Metallic", d.Metal);
                m.SetFloat("_Smoothness", d.Smooth);
                if (d.ER + d.EG + d.EB > 0f)
                {
                    m.EnableKeyword("_EMISSION");
                    m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
                    m.SetColor("_EmissionColor", new Color(d.ER, d.EG, d.EB));
                }
                m.SetFloat("_Cull", 0f); // thin parts (ears, robe hems) must not vanish from behind
                m.enableInstancing = true;
                EditorUtility.SetDirty(m);
            }
        }

        static void CreateController(string fbx, Spec s)
        {
            var clips = new Dictionary<string, AnimationClip>();
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(fbx))
                if (o is AnimationClip c && !c.name.StartsWith("__preview__")) clips[c.name] = c;
            AnimationClip Find(string suffix)
            {
                if (string.IsNullOrEmpty(suffix)) return null;
                foreach (var kv in clips) if (Is(kv.Key, suffix)) return kv.Value;
                Debug.LogError($"[creature] {s.Model}: '{suffix}' 애니메이션이 없어. (있는 것: {string.Join(", ", clips.Keys)})");
                return null;
            }

            string ctrlPath = $"{Dir}/{s.Model}_ctrl.controller";
            if (File.Exists(ctrlPath)) AssetDatabase.DeleteAsset(ctrlPath);
            var ctrl = AnimatorController.CreateAnimatorControllerAtPath(ctrlPath);
            ctrl.AddParameter("Moving", AnimatorControllerParameterType.Bool);
            ctrl.AddParameter("Slash", AnimatorControllerParameterType.Trigger);
            ctrl.AddParameter("SlashSpeed", AnimatorControllerParameterType.Float);
            ctrl.AddParameter("Dead", AnimatorControllerParameterType.Bool);
            ctrl.AddParameter("Talk", AnimatorControllerParameterType.Trigger);
            ctrl.AddParameter("Hit", AnimatorControllerParameterType.Trigger);
            ctrl.AddParameter("WalkSpeed", AnimatorControllerParameterType.Float);
            var ps = ctrl.parameters;
            for (int i = 0; i < ps.Length; i++)
            {
                if (ps[i].name == "SlashSpeed") ps[i].defaultFloat = 1f;
                if (ps[i].name == "WalkSpeed") ps[i].defaultFloat = s.MoveSpeed > 0 ? s.MoveSpeed : 1f;
            }
            ctrl.parameters = ps;

            var sm = ctrl.layers[0].stateMachine;
            var idle = sm.AddState("Idle");
            idle.motion = Find(s.Idle);
            sm.defaultState = idle;

            if (!string.IsNullOrEmpty(s.Move))
            {
                var move = sm.AddState("Move");
                move.motion = Find(s.Move);
                move.speedParameterActive = true; move.speedParameter = "WalkSpeed";
                var a = idle.AddTransition(move); a.hasExitTime = false; a.duration = 0.12f; a.AddCondition(AnimatorConditionMode.If, 0, "Moving");
                var b = move.AddTransition(idle); b.hasExitTime = false; b.duration = 0.12f; b.AddCondition(AnimatorConditionMode.IfNot, 0, "Moving");
            }
            if (!string.IsNullOrEmpty(s.Attack))
            {
                var atk = sm.AddState("Attack");
                atk.motion = Find(s.Attack);
                atk.speedParameterActive = true; atk.speedParameter = "SlashSpeed";
                var any = sm.AddAnyStateTransition(atk); any.hasExitTime = false; any.duration = 0.05f; any.canTransitionToSelf = true;
                any.AddCondition(AnimatorConditionMode.If, 0, "Slash");
                var back = atk.AddTransition(idle); back.hasExitTime = true; back.exitTime = 0.9f; back.duration = 0.1f;
            }
            if (!string.IsNullOrEmpty(s.Talk))
            {
                var talk = sm.AddState("Talk");
                talk.motion = Find(s.Talk);
                var any = sm.AddAnyStateTransition(talk); any.hasExitTime = false; any.duration = 0.1f; any.canTransitionToSelf = true;
                any.AddCondition(AnimatorConditionMode.If, 0, "Talk");
                var back = talk.AddTransition(idle); back.hasExitTime = true; back.exitTime = 0.95f; back.duration = 0.15f;
            }
            if (!string.IsNullOrEmpty(s.Hit))
            {
                var hit = sm.AddState("Hit");
                hit.motion = Find(s.Hit);
                var any = sm.AddAnyStateTransition(hit); any.hasExitTime = false; any.duration = 0.03f; any.canTransitionToSelf = true;
                any.AddCondition(AnimatorConditionMode.If, 0, "Hit");
                var back = hit.AddTransition(idle); back.hasExitTime = true; back.exitTime = 0.95f; back.duration = 0.1f;
            }
            if (!string.IsNullOrEmpty(s.Death))
            {
                var death = sm.AddState("Death");
                death.motion = Find(s.Death);
                var any = sm.AddAnyStateTransition(death); any.hasExitTime = false; any.duration = 0.05f;
                any.AddCondition(AnimatorConditionMode.If, 0, "Dead");
                var back = death.AddTransition(idle); back.hasExitTime = false; back.duration = 0.05f; back.AddCondition(AnimatorConditionMode.IfNot, 0, "Dead");
            }
            EditorUtility.SetDirty(ctrl);
            Debug.Log($"[creature] {s.Model} 설정 완료 (클립: {string.Join(", ", clips.Keys)})");
        }
    }
}
