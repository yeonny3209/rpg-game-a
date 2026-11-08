using System.IO;
using UnityEditor;
using UnityEngine;

namespace Game.Client.Editor
{
    /// <summary>
    /// The nine class weapons (docs/art/weapons, from the planner's AI models): FBX with LODs + BaseColor / Normal / MetallicSmoothness textures.
    /// Sets texture import rules (normal map, no sRGB on the metal map, 512 px to keep the WebGL download small) and one URP Lit material per weapon.
    /// Runs as part of "RPG > 1. 프로젝트 설정 적용".
    /// </summary>
    public static class WeaponSetup
    {
        public const string Dir = "Assets/_Game/Resources/Weapons";
        public static readonly string[] Names = { "katana", "longsword", "staff", "dagger", "mace", "shield", "bow", "arrow", "scepter" };

        static string Cap(string n) => char.ToUpper(n[0]) + n.Substring(1);

        public static void Apply()
        {
            if (!Directory.Exists(Dir)) return;
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) { Debug.LogError("[weapons] URP Lit 셰이더가 없어."); return; }
            foreach (var n in Names)
            {
                string c = Cap(n);
                Tex($"{Dir}/Textures/{c}_BaseColor.png", true, false);
                Tex($"{Dir}/Textures/{c}_Normal.png", false, true);
                Tex($"{Dir}/Textures/{c}_MetallicSmoothness.png", false, false);

                string fbx = $"{Dir}/{n}.fbx";
                if (AssetImporter.GetAtPath(fbx) is ModelImporter mi)
                {
                    bool dirty = false;
                    if (mi.importAnimation) { mi.importAnimation = false; dirty = true; }
                    if (mi.materialImportMode != ModelImporterMaterialImportMode.None) { mi.materialImportMode = ModelImporterMaterialImportMode.None; dirty = true; }
                    if (mi.meshCompression != ModelImporterMeshCompression.Medium) { mi.meshCompression = ModelImporterMeshCompression.Medium; dirty = true; }
                    if (dirty) mi.SaveAndReimport();
                }

                string matPath = $"{Dir}/Mat_{n}.mat";
                var m = AssetDatabase.LoadAssetAtPath<Material>(matPath);
                if (m == null) { m = new Material(shader); AssetDatabase.CreateAsset(m, matPath); }
                m.shader = shader;
                m.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>($"{Dir}/Textures/{c}_BaseColor.png"));
                m.SetTexture("_BumpMap", AssetDatabase.LoadAssetAtPath<Texture2D>($"{Dir}/Textures/{c}_Normal.png"));
                m.SetTexture("_MetallicGlossMap", AssetDatabase.LoadAssetAtPath<Texture2D>($"{Dir}/Textures/{c}_MetallicSmoothness.png"));
                m.SetColor("_BaseColor", Color.white);
                m.SetFloat("_Smoothness", 1f);                 // the map's alpha carries the real value
                m.SetFloat("_SmoothnessTextureChannel", 0f);   // Metallic alpha
                m.EnableKeyword("_NORMALMAP");
                m.EnableKeyword("_METALLICSPECGLOSSMAP");
                if (n == "arrow") m.SetFloat("_Cull", 0f);     // the fletching is a single sheet
                m.enableInstancing = true;
                EditorUtility.SetDirty(m);
            }
            AssetDatabase.SaveAssets();
        }

        static void Tex(string path, bool srgb, bool normal)
        {
            if (!(AssetImporter.GetAtPath(path) is TextureImporter ti)) return;
            bool dirty = false;
            var type = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
            if (ti.textureType != type) { ti.textureType = type; dirty = true; }
            if (!normal && ti.sRGBTexture != srgb) { ti.sRGBTexture = srgb; dirty = true; }
            if (ti.maxTextureSize != 512) { ti.maxTextureSize = 512; dirty = true; }
            if (ti.mipmapEnabled == false) { ti.mipmapEnabled = true; dirty = true; }
            if (ti.crunchedCompression) { ti.crunchedCompression = false; dirty = true; }
            if (dirty) ti.SaveAndReimport();
        }

        /// <summary>Batch: size and orientation of each model. Run with -executeMethod Game.Client.Editor.WeaponSetup.Report.</summary>
        public static void Report()
        {
            AssetDatabase.Refresh();
            Apply();
            foreach (var n in Names)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"{Dir}/{n}.fbx");
                if (prefab == null) { Debug.LogWarning($"[weapons-report] {n}: FBX를 못 읽었어"); continue; }
                var go = (GameObject)Object.Instantiate(prefab);
                var rs = go.GetComponentsInChildren<Renderer>(true);
                var lod = go.GetComponent<LODGroup>();
                var b = new Bounds(); bool first = true;
                foreach (var r in rs)
                {
                    if (!r.name.EndsWith("LOD0") && lod != null) continue;
                    if (first) { b = r.bounds; first = false; } else b.Encapsulate(r.bounds);
                }
                Debug.Log($"[weapons-report] {n}: renderers {rs.Length} lodGroup={(lod != null ? lod.lodCount.ToString() : "none")} LOD0 bounds center {b.center} size {b.size} min {b.min} max {b.max}");
                foreach (var r in rs) Debug.Log($"[weapons-report]   {r.name} mats={r.sharedMaterials.Length} rot={r.transform.localEulerAngles} scale={r.transform.lossyScale}");
                Object.DestroyImmediate(go);
            }
        }
    }
}
