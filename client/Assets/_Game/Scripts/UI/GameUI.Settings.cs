using Game.Client.Core;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Game.Client.UI
{
    /// <summary>Settings (spec vol.5 12.5: 설정 — 그래픽 품질·카메라·키). Graphics tier, camera (3rd person / quarter view) and key rebinding.</summary>
    public sealed partial class GameUI
    {
        GameObject _settingsWin;
        Button[] _qBtns, _camBtns, _keyBtns;
        Text _keyMsg;
        BindAction? _keyWait;
        GraphicsQuality _gq;

        public bool SettingsOpen => _settingsWin && _settingsWin.activeSelf;

        void BuildSettings()
        {
            var w = Window("Settings", new Vector2(0.5f, 0.55f), new Vector2(640, 540), out var title, () => CloseSettings());
            _settingsWin = w.gameObject;
            title.text = "설정";

            Section(w, "그래픽 품질", -46);
            string[] qn = { "낮음 (Low)", "보통 (Medium)", "높음 (High)" };
            _qBtns = new Button[3];
            for (int i = 0; i < 3; i++)
            {
                int idx = i;
                _qBtns[i] = WideButton(w, qn[i], new Vector2(18 + i * 202, -74), 190, () => { if (_gq) _gq.Apply((QualityTier)idx); RefreshSettings(); });
            }

            Section(w, "카메라", -124);
            string[] cn = { "3인칭 뒷시점 (마우스 잠금·조준점)", "쿼터뷰 (마우스 커서로 조준)" };
            _camBtns = new Button[2];
            for (int i = 0; i < 2; i++)
            {
                int idx = i;
                _camBtns[i] = WideButton(w, cn[i], new Vector2(18 + i * 303, -152), 292, () => { GameInput.Quarter = idx == 1; RefreshSettings(); });
            }

            Section(w, "키 설정  (버튼을 누른 뒤 바꿀 키를 눌러. 이동·점프·대시·Esc·Enter는 고정)", -206);
            int n = KeyBindings.Count;
            _keyBtns = new Button[n];
            for (int i = 0; i < n; i++)
            {
                var action = (BindAction)i;
                int col = i < 6 ? 0 : 1, row = i < 6 ? i : i - 6;
                float x = 18 + col * 310, y = -236 - row * 40;
                var lab = UiKit.Label("KeyLabel" + i, w, 15, TextAnchor.MiddleLeft, UiKit.TextMain);
                lab.rectTransform.Place(new Vector2(0, 1), new Vector2(0, 1), new Vector2(x, y), new Vector2(120, 32));
                lab.text = KeyBindings.Label(action);
                _keyBtns[i] = WideButton(w, "", new Vector2(x + 126, y), 150, () => { _keyWait = action; _keyMsg.text = $"<color=#f2c75a>{KeyBindings.Label(action)}</color>에 쓸 키를 눌러줘 (Esc: 취소)"; RefreshSettings(); });
            }
            var reset = WideButton(w, "기본 키로 되돌리기", new Vector2(18, -490), 190, () => { KeyBindings.ResetAll(); _keyMsg.text = "기본 키로 되돌렸어."; RefreshSettings(); });
            _keyMsg = UiKit.Label("KeyMsg", w, 14, TextAnchor.MiddleLeft, UiKit.TextDim);
            _keyMsg.rectTransform.Place(new Vector2(0, 1), new Vector2(0, 1), new Vector2(220, -490), new Vector2(400, 34));
            _settingsWin.SetActive(false);
        }

        static void Section(RectTransform w, string text, float y)
        {
            var t = UiKit.Label("Section", w, 15, TextAnchor.MiddleLeft, UiKit.Accent);
            t.rectTransform.Place(new Vector2(0, 1), new Vector2(0, 1), new Vector2(18, y), new Vector2(600, 24));
            t.text = text;
        }

        static Button WideButton(RectTransform w, string text, Vector2 pos, float width, System.Action click)
        {
            var b = UiKit.Button("Btn", w, text, 14, click);
            b.GetComponent<RectTransform>().Place(new Vector2(0, 1), new Vector2(0, 1), pos, new Vector2(width, 32));
            Center(b);
            return b;
        }

        void OpenSettings()
        {
            if (_gq == null) _gq = FindFirstObjectByType<GraphicsQuality>();
            _settingsWin.SetActive(true);
            _keyMsg.text = "";
            RefreshSettings();
            FreeMouse();
        }

        void CloseSettings()
        {
            _keyWait = null;
            _settingsWin.SetActive(false);
        }

        void RefreshSettings()
        {
            if (_settingsWin == null || !_settingsWin.activeSelf) return;
            int q = _gq ? (int)_gq.Tier : 0;
            for (int i = 0; i < 3; i++) _qBtns[i].image.color = i == q ? GenderOn : GenderOff;
            _camBtns[0].image.color = !GameInput.Quarter ? GenderOn : GenderOff;
            _camBtns[1].image.color = GameInput.Quarter ? GenderOn : GenderOff;
            for (int i = 0; i < _keyBtns.Length; i++)
            {
                bool waiting = _keyWait.HasValue && (int)_keyWait.Value == i;
                _keyBtns[i].GetComponentInChildren<Text>().text = waiting ? "키를 눌러줘…" : KeyBindings.KeyName(KeyBindings.Get((BindAction)i));
                _keyBtns[i].image.color = waiting ? GenderOn : GenderOff;
            }
        }

        /// <summary>While a key is being chosen, the next key press becomes the binding.</summary>
        void UpdateKeyCapture(Keyboard kb)
        {
            if (!_keyWait.HasValue || kb == null) return;
            if (kb.escapeKey.wasPressedThisFrame) { _keyWait = null; _keyMsg.text = "취소했어."; RefreshSettings(); return; }
            foreach (var kc in kb.allKeys)
            {
                if (!kc.wasPressedThisFrame) continue;
                var key = kc.keyCode;
                var action = _keyWait.Value;
                if (!KeyBindings.IsAllowed(key)) { _keyMsg.text = "그 키는 쓸 수 없어. 다른 키를 눌러줘."; return; }
                KeyBindings.Set(action, key);
                _keyWait = null;
                _keyMsg.text = $"<color=#9fd18b>{KeyBindings.Label(action)}</color> = {KeyBindings.KeyName(key)}";
                RefreshSettings();
                return;
            }
        }
    }
}
