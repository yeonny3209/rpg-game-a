using Game.Client.Core;
using Game.Shared.Protocol;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Client.UI
{
    /// <summary>마법사: mana bar, the one-time element picker (spec vol.2 5.2), and the flight status line (vol.4 1.5).</summary>
    public sealed partial class GameUI
    {
        GameObject _elementPanel;
        Text _elementHint;
        Button[] _elementButtons;
        string[] _elementIds;
        string _ePick = "";
        bool _elementSent;

        public bool ElementOpen => _elementPanel && _elementPanel.activeSelf;

        void BuildElementPicker()
        {
            var p = UiKit.Panel("ElementPicker", _root, UiKit.PanelBg);
            _elementPanel = p.gameObject;
            p.rectTransform.Place(new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 70), new Vector2(760, 250));
            var title = UiKit.Label("Title", p.transform, 24, TextAnchor.MiddleCenter, UiKit.Accent, true);
            title.rectTransform.Place(new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -14), new Vector2(720, 36));
            title.text = "어떤 속성의 마법사가 될래?";
            _elementHint = UiKit.Label("Hint", p.transform, 14, TextAnchor.MiddleCenter, UiKit.TextDim);
            _elementHint.rectTransform.Place(new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -52), new Vector2(720, 40));
            _elementHint.text = "속성에 따라 마법에 효과가 붙어. 한 번 고르면 지금은 바꿀 수 없어.";
            _elementPanel.SetActive(false);
        }

        void EnsureElementButtons()
        {
            if (_elementButtons != null) return;
            var job = _st.Data.Job("mage");
            if (job == null || job.mage == null) return;
            _elementIds = job.mage.elements;
            _elementButtons = new Button[_elementIds.Length];
            for (int i = 0; i < _elementIds.Length; i++)
            {
                int idx = i;
                var el = _st.Data.elements.Find(_elementIds[i]);
                var b = UiKit.Button("El" + i, _elementPanel.transform, el != null ? el.name : _elementIds[i], 20, () => PickElement(_elementIds[idx]));
                int col = i % 3, row = i / 3;
                b.GetComponent<RectTransform>().Place(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2((col - 1) * 220, 22 - row * 58), new Vector2(200, 48));
                var t = b.GetComponentInChildren<Text>();
                t.alignment = TextAnchor.MiddleCenter;
                t.rectTransform.offsetMin = Vector2.zero;
                _elementButtons[i] = b;
            }
            var ok = UiKit.Button("ElConfirm", _elementPanel.transform, "이 속성으로 확정", 20, ConfirmElement);
            ok.GetComponent<RectTransform>().Place(new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 16), new Vector2(300, 46));
            var ct = ok.GetComponentInChildren<Text>();
            ct.alignment = TextAnchor.MiddleCenter;
            ct.rectTransform.offsetMin = Vector2.zero;
        }

        void PickElement(string id)
        {
            _ePick = id;
            for (int i = 0; i < _elementIds.Length; i++)
            {
                var el = _st.Data.elements.Find(_elementIds[i]);
                var col = el != null ? Game.Client.World.Placeholders.ParseColor(el.color, Color.gray) : Color.gray;
                _elementButtons[i].image.color = _elementIds[i] == id ? col : new Color(0.22f, 0.24f, 0.3f, 0.95f);
            }
            var e = _st.Data.elements.Find(id);
            _elementHint.text = e != null ? e.name + ": " + ElementEffectText(e) : "";
        }

        static string ElementEffectText(Game.Shared.Data.ElementDef e)
        {
            switch (e.effect != null ? e.effect.type : "")
            {
                case "burn": return "맞은 적이 잠시 불타 계속 피해를 입는다.";
                case "slow": return "맞은 적의 움직임이 느려진다.";
                case "knockback": return "맞은 적을 뒤로 밀어낸다.";
                case "chain": return "근처의 다른 적에게 번개가 튄다.";
                case "root": return "맞은 적이 잠시 움직이지 못한다.";
                case "pierce": return "여러 적을 꿰뚫고 나간다.";
                default: return "";
            }
        }

        void ConfirmElement()
        {
            if (_elementSent || string.IsNullOrEmpty(_ePick)) return;
            _elementSent = true;
            _net.Send(new ChooseElement { ElementId = _ePick });
            _elementPanel.SetActive(false);
        }

        void RefreshMage()
        {
            var c = _st.Char;
            bool mage = _st.IsMage;
            bool trialMage = _st.TrialActive && _st.Job != null && _st.Job.mage != null && _st.Job.mage.enabled;
            _manaBar.Root.gameObject.SetActive(_st.UsesMana);
            if (_st.MaxMana > 0 && _st.Mana >= 0)
                _manaBar.Set(_st.Mana / (float)_st.MaxMana, "MP " + _st.Mana + " / " + _st.MaxMana + (mage && _st.Skills != null ? "  · " + _st.Skills.Circle + "서클" : "") + (_st.Infused ? "  · 주입 ON" : ""));

            if (mage && _st.NeedsElement && c != null && !c.Dead)
            {
                if (!ElementOpen && !_elementSent)
                {
                    EnsureElementButtons();
                    if (_elementButtons != null) { _elementPanel.SetActive(true); if (string.IsNullOrEmpty(_ePick)) PickElement(_elementIds[0]); }
                }
            }
            else
            {
                if (ElementOpen) _elementPanel.SetActive(false);
                if (!_st.NeedsElement) _elementSent = false;          // a later job change may ask again
            }
        }

        float _nextFlyUi;

        void UpdateMageUi()
        {
            if (Time.unscaledTime < _nextFlyUi) return;
            _nextFlyUi = Time.unscaledTime + 0.2f;
            if (!_st.CanFly) { _hudFly.text = _st.Infused ? "마나 주입 중 — 평타에 마나가 실린다" : ""; return; }
            var key = KeyBindings.KeyName(KeyBindings.Get(BindAction.Fly));
            if (_st.Flying) _hudFly.text = "비행 중 " + Mathf.Max(0f, _st.FlyEndAt - Time.time).ToString("0.0") + "초 · [" + key + "] 착지 · Space 상승 · Ctrl 하강";
            else if (Time.time < _st.FlyReadyAt) _hudFly.text = "비행 재사용까지 " + Mathf.CeilToInt(_st.FlyReadyAt - Time.time) + "초";
            else _hudFly.text = "[" + key + "] 비행 가능";
        }
    }
}
