using System.IO;
using Game.Client.Core;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Game.Client.Editor
{
    /// <summary>
    /// One-click project setup so nothing has to be configured by hand (and nothing hand-authored lives in scene YAML).
    /// Menu: RPG > 1. 프로젝트 설정 적용 / RPG > 2. WebGL 빌드.
    /// </summary>
    public static class ProjectSetup
    {
        const string ScenePath = "Assets/_Game/Scenes/Main.unity";
        const string MatDir = "Assets/_Game/Resources/Materials";
        const string UrpDir = "Assets/_Game/Settings";
        static string BuildDir => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "builds", "webgl"));

        [MenuItem("RPG/1. 프로젝트 설정 적용", priority = 1)]
        public static void ApplyAll()
        {
            EnsureUrp();
            CreateMaterials();
            HeroSetup.Apply();
            CreatureSetup.Apply();
            WeaponSetup.Apply();
            ConfigurePlayer();
            CreateScene();
            AssetDatabase.SaveAssets();
            Debug.Log("[setup] 완료. Main 씬이 열렸어. ▶ Play로 에디터에서 바로 테스트할 수 있어 (서버 먼저 실행).");
        }

        [MenuItem("RPG/2. WebGL 빌드 (builds\\webgl)", priority = 2)]
        public static void BuildWebGL() { Build(false); }

        [MenuItem("RPG/3. WebGL 개발 빌드 (프로파일러 연결용)", priority = 3)]
        public static void BuildWebGLDev() { Build(true); }

        // Called from the command line: Unity -batchmode -quit -projectPath client -executeMethod Game.Client.Editor.ProjectSetup.CommandLineBuild
        public static void CommandLineBuild()
        {
            ApplyAll();
            Build(false);
        }

        static void EnsureUrp()
        {
            var urp = GraphicsSettings.defaultRenderPipeline as UniversalRenderPipelineAsset;
            if (urp == null)
            {
                Directory.CreateDirectory(UrpDir);
                var rendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
                AssetDatabase.CreateAsset(rendererData, $"{UrpDir}/RPG_Renderer.asset");
                urp = UniversalRenderPipelineAsset.Create(rendererData);
                AssetDatabase.CreateAsset(urp, $"{UrpDir}/RPG_URP.asset");
                GraphicsSettings.defaultRenderPipeline = urp;
                Debug.Log("[setup] URP 에셋을 새로 만들어 연결했어.");
            }
            // Every quality level uses the same asset; GraphicsQuality changes a runtime copy.
            int current = QualitySettings.GetQualityLevel();
            for (int i = 0; i < QualitySettings.names.Length; i++)
            {
                QualitySettings.SetQualityLevel(i, false);
                QualitySettings.renderPipeline = urp;
            }
            QualitySettings.SetQualityLevel(current, false);
            urp.supportsCameraDepthTexture = false;
            urp.supportsCameraOpaqueTexture = false;
            EditorUtility.SetDirty(urp);
        }

        static void CreateMaterials()
        {
            Directory.CreateDirectory(MatDir);
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) { Debug.LogError("[setup] URP Lit 셰이더가 없어. URP 패키지를 확인해 줘."); return; }

            Mat("Ground", shader, new Color(0.36f, 0.48f, 0.27f), 0.1f);
            Mat("Trunk", shader, new Color(0.38f, 0.27f, 0.18f), 0.1f);
            Mat("Leaves", shader, new Color(0.2f, 0.42f, 0.2f), 0.15f);
            Mat("Local", shader, new Color(0.2f, 0.45f, 0.95f), 0.35f);
            Mat("Remote", shader, new Color(0.95f, 0.55f, 0.15f), 0.35f);
            Mat("Wing", shader, new Color(0.92f, 0.92f, 0.95f), 0.2f);
            Mat("Nose", shader, new Color(0.1f, 0.1f, 0.12f), 0.3f);
            Mat("Tint", shader, Color.white, 0.2f); // cloned per color at runtime (Placeholders.Tinted)
            var unlit = Shader.Find("Universal Render Pipeline/Unlit");
            if (unlit != null) Mat("Fx", unlit, Color.white, 0f); // skill effects / projectiles, cloned per color (Placeholders.FxMat)
        }

        static void Mat(string name, Shader shader, Color color, float smoothness)
        {
            string path = $"{MatDir}/{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                m = new Material(shader);
                AssetDatabase.CreateAsset(m, path);
            }
            m.shader = shader;
            m.SetColor("_BaseColor", color);
            m.SetFloat("_Smoothness", smoothness);
            m.enableInstancing = true;
            EditorUtility.SetDirty(m);
        }

        static void ConfigurePlayer()
        {
            PlayerSettings.companyName = "Seyun";
            PlayerSettings.productName = "RPG";
            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.runInBackground = true;
            PlayerSettings.SetGraphicsAPIs(BuildTarget.WebGL, new[] { GraphicsDeviceType.OpenGLES3 });
            PlayerSettings.WebGL.template = "PROJECT:RpgTemplate";
            // Gzip: the server sends the .gz files with Content-Encoding: gzip, which every browser unpacks itself (also over plain http).
            // Uncompressed the build is ~75 MB, and through the tunnel every visitor pulls it from the planner's PC upload.
            // (Brotli is smaller but browsers only accept it over https, which would break http://localhost.)
            PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Gzip;
            PlayerSettings.WebGL.decompressionFallback = false;
            PlayerSettings.WebGL.dataCaching = true;
            PlayerSettings.WebGL.exceptionSupport = WebGLExceptionSupport.ExplicitlyThrownExceptionsOnly;

            // New Input System must be active (0 = old, 1 = new, 2 = both).
            var ps = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset");
            if (ps != null && ps.Length > 0)
            {
                var so = new SerializedObject(ps[0]);
                var prop = so.FindProperty("activeInputHandler");
                if (prop != null && prop.intValue == 0)
                {
                    prop.intValue = 1;
                    so.ApplyModifiedProperties();
                    Debug.LogWarning("[setup] 입력 방식을 Input System으로 바꿨어. 에디터를 한 번 재시작해 줘.");
                }
            }
        }

        static void CreateScene()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var go = new GameObject("Bootstrap");
            go.AddComponent<Bootstrap>();
            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        }

        static void Build(bool development)
        {
            if (!File.Exists(ScenePath)) ApplyAll();
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.WebGL)
                EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.WebGL, BuildTarget.WebGL);

            Directory.CreateDirectory(BuildDir);
            var opts = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = BuildDir,
                target = BuildTarget.WebGL,
                options = development ? BuildOptions.Development | BuildOptions.ConnectWithProfiler : BuildOptions.None,
            };
            var report = BuildPipeline.BuildPlayer(opts);
            if (report.summary.result == BuildResult.Succeeded)
                Debug.Log($"[build] 성공 ({report.summary.totalSize / (1024f * 1024f):F1} MB) → {BuildDir}\n서버를 (다시) 실행하고 http://localhost:7777 접속.");
            else
                Debug.LogError($"[build] 실패: {report.summary.result}");
        }
    }
}
