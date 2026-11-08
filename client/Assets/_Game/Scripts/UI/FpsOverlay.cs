using System.Text;
using UnityEngine;

namespace Game.Client.UI
{
    /// <summary>
    /// Performance overlay for the Iris Xe check: average FPS, frame ms, worst frame of the last 5 s,
    /// plus network/quality info. Text is ASCII on purpose (no Korean font in WebGL yet).
    /// </summary>
    public sealed class FpsOverlay : MonoBehaviour
    {
        public System.Func<string> ExtraInfo;
        public bool Visible = false; // F3 toggles (stage 1: hidden by default, the game HUD replaces it)

        float _accum;
        int _frames;
        float _fps;
        float _ms;
        float _worstMs;
        float _worstWindowMs;
        float _windowTimer;
        float _sampleTimer;

        GUIStyle _style;
        GUIStyle _shadow;
        readonly StringBuilder _sb = new StringBuilder(512);
        string _text = "";

        void Update()
        {
            float dt = Time.unscaledDeltaTime;
            _accum += dt;
            _frames++;
            _worstWindowMs = Mathf.Max(_worstWindowMs, dt * 1000f);
            _windowTimer += dt;
            _sampleTimer += dt;

            if (_sampleTimer >= 0.5f)
            {
                _fps = _frames / _accum;
                _ms = _accum / _frames * 1000f;
                _accum = 0; _frames = 0; _sampleTimer = 0;
                Rebuild();
            }
            if (_windowTimer >= 5f)
            {
                _worstMs = _worstWindowMs;
                _worstWindowMs = 0;
                _windowTimer = 0;
            }
        }

        public float Fps => _fps;

        void Rebuild()
        {
            _sb.Clear();
            _sb.Append("FPS ").Append(_fps.ToString("F1"))
               .Append("   frame ").Append(_ms.ToString("F1")).Append(" ms")
               .Append("   worst(5s) ").Append(_worstMs.ToString("F0")).Append(" ms\n");
            _sb.Append("screen ").Append(Screen.width).Append('x').Append(Screen.height);
            if (ExtraInfo != null) _sb.Append('\n').Append(ExtraInfo());
            _text = _sb.ToString();
        }

        void OnGUI()
        {
            if (!Visible) return;
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label) { fontSize = 15, richText = false };
                _style.normal.textColor = Color.white;
                _shadow = new GUIStyle(_style);
                _shadow.normal.textColor = new Color(0, 0, 0, 0.8f);
            }
            var r = new Rect(10, 8, 900, 200);
            GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), _text, _shadow);
            Color c = _fps >= 30f ? new Color(0.6f, 1f, 0.6f) : new Color(1f, 0.5f, 0.4f);
            _style.normal.textColor = c;
            GUI.Label(r, _text, _style);
        }
    }
}
