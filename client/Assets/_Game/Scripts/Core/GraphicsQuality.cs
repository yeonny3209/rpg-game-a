using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Game.Client.Core
{
    public enum QualityTier { Low = 0, Medium = 1, High = 2 }

    /// <summary>
    /// Low/Medium/High graphics (spec vol.1 4.2). Low targets 30 fps on Intel Iris Xe.
    /// Works on a runtime copy of the URP asset so editor play mode never dirties the project asset.
    /// </summary>
    public sealed class GraphicsQuality : MonoBehaviour
    {
        // Tech presets (not game-design numbers). Tune here after measuring on the Iris Xe PC.
        static readonly float[] RenderScale = { 0.6f, 0.8f, 1.0f };
        static readonly float[] ShadowDistance = { 0f, 40f, 80f };
        static readonly int[] Msaa = { 1, 1, 2 };
        static readonly float[] FarClip = { 180f, 260f, 400f };
        static readonly float[] FogEnd = { 170f, 240f, 380f };

        const string PrefKey = "gfx_tier";

        UniversalRenderPipelineAsset _urp;
        Light _sun;
        Camera _cam;

        public QualityTier Tier { get; private set; } = QualityTier.Low;
        public bool HasUrp => _urp != null;

        public void Init(Light sun, Camera cam)
        {
            _sun = sun;
            _cam = cam;
            var src = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (src != null)
            {
                _urp = Instantiate(src);
                QualitySettings.renderPipeline = _urp;
            }
            else
            {
                Debug.LogError("URP asset not found. The project must use the Universal Render Pipeline.");
            }
            Apply((QualityTier)PlayerPrefs.GetInt(PrefKey, (int)QualityTier.Low));
        }

        public void Cycle() { Apply((QualityTier)(((int)Tier + 1) % 3)); }

        public void Apply(QualityTier tier)
        {
            Tier = tier;
            int i = (int)tier;
            PlayerPrefs.SetInt(PrefKey, i);

            if (_urp != null)
            {
                // The page passes the real devicePixelRatio (needed for correct UI), so divide it back out of
                // the 3D resolution: a 2560×1600 laptop at DPR 1.5 renders 3D like a 1707×1067 screen, UI stays sharp.
                _urp.renderScale = Mathf.Max(0.25f, RenderScale[i] / DisplayScale());
                _urp.shadowDistance = Mathf.Max(1f, ShadowDistance[i]);
                _urp.msaaSampleCount = Msaa[i];
                _urp.supportsHDR = tier == QualityTier.High;
            }
            if (_sun != null)
                _sun.shadows = ShadowDistance[i] <= 0 ? LightShadows.None : (tier == QualityTier.High ? LightShadows.Soft : LightShadows.Hard);
            if (_cam != null)
            {
                _cam.farClipPlane = FarClip[i];
                var data = _cam.GetUniversalAdditionalCameraData();
                if (data != null) data.renderPostProcessing = tier == QualityTier.High;
            }
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogStartDistance = FogEnd[i] * 0.45f;
            RenderSettings.fogEndDistance = FogEnd[i];
        }

        static float DisplayScale()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            float d = Screen.dpi / 96f; // WebGL reports 96 × devicePixelRatio
            return d >= 1f && d <= 4f ? d : 1f;
#else
            return 1f;
#endif
        }

        float _lastDisplayScale;

        void Update()
        {
            // Browser zoom / moving to another monitor changes the ratio.
            float d = DisplayScale();
            if (Mathf.Abs(d - _lastDisplayScale) > 0.01f) { _lastDisplayScale = d; Apply(Tier); }
        }

        public float CurrentRenderScale => _urp != null ? _urp.renderScale : 1f;
    }
}
