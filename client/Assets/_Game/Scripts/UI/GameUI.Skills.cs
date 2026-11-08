using System.Text;
using Game.Client.Core;
using Game.Client.Player;
using Game.Client.World;
using Game.Shared.Data;
using Game.Shared.Protocol;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Client.UI
{
    /// <summary>Skill bar (Q E T G Z X), archer magazine and charge bar, job-trial banner, skill window (K).</summary>
    public sealed partial class GameUI
    {
        sealed class SkillSlotView
        {
            public RectTransform Root;
            public Image Bg, Icon, Sweep;
            public Text Key, Name, Cd, Lock;
        }

        SkillSlotView[] _skillSlots;
        PlayerCombat _combat;

        // archer
        RectTransform _bowRoot;
        Text _ammoText;
        Bar _reloadBar, _chargeBar;
        Text _chargeLabel;
        Text _dodgeText;

        // trial
        Text _trialBanner;

        // skill window
        GameObject _skillWin;
        Text _skillTitle, _skillInfo;
        Text[] _skillRows;
        Image[] _skillRowIcons;

        public void BindCombat(PlayerCombat c) { _combat = c; }

        public bool SkillWindowOpen => _skillWin && _skillWin.activeSelf;

        // ───────────── build ─────────────

        void BuildSkillBar()
        {
            const float size = 58f, gap = 6f;
            float total = 6 * size + 5 * gap;
            var bar = UiKit.Rect("SkillBar", _root).Place(new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 14), new Vector2(total, size + 8));
            _skillSlots = new SkillSlotView[6];
            for (int i = 0; i < 6; i++)
            {
                var v = new SkillSlotView();
                v.Bg = UiKit.Panel("Slot" + i, bar, new Color(0.07f, 0.08f, 0.1f, 0.85f));
                v.Root = v.Bg.rectTransform.Place(new Vector2(0, 0), new Vector2(0, 0), new Vector2(i * (size + gap), 4), new Vector2(size, size));
                v.Icon = UiKit.Panel("Icon", v.Root, Color.white);
                v.Icon.raycastTarget = false;
                v.Icon.rectTransform.Fill(4);
                v.Name = UiKit.Label("Name", v.Root, 12, TextAnchor.MiddleCenter, Color.white, true);
                v.Name.rectTransform.Fill(2);
                v.Sweep = UiKit.Panel("Sweep", v.Root, new Color(0, 0, 0, 0.62f));
                v.Sweep.raycastTarget = false;
                v.Sweep.rectTransform.Fill(0);
                v.Cd = UiKit.Label("Cd", v.Root, 20, TextAnchor.MiddleCenter, Color.white, true);
                v.Cd.rectTransform.Fill();
                v.Key = UiKit.Label("Key", v.Root, 13, TextAnchor.UpperLeft, UiKit.Accent, true);
                v.Key.rectTransform.Fill(3);
                v.Lock = UiKit.Label("Lock", v.Root, 12, TextAnchor.LowerCenter, new Color(1f, 0.7f, 0.6f), true);
                v.Lock.rectTransform.Fill(3);
                _skillSlots[i] = v;
            }

            // Archer: magazine + reload, charge bar under the crosshair.
            _bowRoot = UiKit.Rect("Bow", _root).Place(new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 88), new Vector2(300, 40));
            _ammoText = UiKit.Label("Ammo", _bowRoot, 20, TextAnchor.MiddleCenter, Color.white, true);
            _ammoText.rectTransform.Place(new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, 0), new Vector2(300, 26));
            _reloadBar = new Bar("Reload", _bowRoot, new Color(0.95f, 0.75f, 0.25f), 11);
            _reloadBar.Root.Place(new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 0), new Vector2(180, 12));
            _bowRoot.gameObject.SetActive(false);

            _chargeBar = new Bar("Charge", _root, new Color(0.95f, 0.8f, 0.25f), 0);
            _chargeBar.Root.Place(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0, -78), new Vector2(240, 14));
            _chargeLabel = UiKit.Label("ChargeLabel", _root, 17, TextAnchor.MiddleCenter, Color.white, true);
            _chargeLabel.rectTransform.Place(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0, -102), new Vector2(240, 24));
            _chargeBar.Root.gameObject.SetActive(false);
            _chargeLabel.enabled = false;

            _dodgeText = UiKit.Label("Dodge", _root, 13, TextAnchor.MiddleCenter, UiKit.TextDim, true);
            _dodgeText.rectTransform.Place(new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 76), new Vector2(300, 18));

            _trialBanner = UiKit.Label("Trial", _root, 18, TextAnchor.MiddleCenter, new Color(1f, 0.9f, 0.55f), true);
            _trialBanner.rectTransform.Place(new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -56), new Vector2(900, 30));
            _trialBanner.enabled = false;
        }

        void BuildSkillWindow()
        {
            var w = Window("Skills", new Vector2(0.5f, 0.55f), new Vector2(640, 560), out _skillTitle, () => _skillWin.SetActive(false));
            _skillWin = w.gameObject;
            _skillTitle.text = "스킬";
            _skillInfo = UiKit.Label("Info", w, 15, TextAnchor.UpperLeft, UiKit.TextMain);
            _skillInfo.rectTransform.Place(new Vector2(0, 1), new Vector2(0, 1), new Vector2(18, -44), new Vector2(600, 86));
            _skillRows = new Text[6];
            _skillRowIcons = new Image[6];
            for (int i = 0; i < 6; i++)
            {
                float y = -138 - i * 68;
                var icon = UiKit.Panel("Icon" + i, w, Color.gray);
                icon.rectTransform.Place(new Vector2(0, 1), new Vector2(0, 1), new Vector2(18, y), new Vector2(54, 54));
                _skillRowIcons[i] = icon;
                var t = UiKit.Label("Row" + i, w, 14, TextAnchor.UpperLeft, UiKit.TextMain);
                t.rectTransform.Place(new Vector2(0, 1), new Vector2(0, 1), new Vector2(84, y + 2), new Vector2(536, 62));
                _skillRows[i] = t;
            }
            _skillWin.SetActive(false);
        }

        // ───────────── per frame ─────────────

        void UpdateSkillUi()
        {
            if (_skillSlots == null || _st.Char == null) return;
            var job = _st.Job;
            bool dead = _st.Char.Dead;

            for (int i = 0; i < 6; i++)
            {
                var v = _skillSlots[i];
                var s = _st.SkillAt(i);
                v.Key.text = KeyBindings.KeyName(KeyBindings.Skill(i));
                if (s == null)
                {
                    v.Icon.enabled = false; v.Name.text = ""; v.Sweep.enabled = false; v.Cd.text = ""; v.Lock.text = "";
                    v.Bg.color = new Color(0.07f, 0.08f, 0.1f, 0.45f);
                    continue;
                }
                bool unlocked = _st.SkillUnlocked(s);
                var col = Placeholders.ParseColor(s.color, Color.white);
                v.Icon.enabled = true;
                v.Icon.color = unlocked ? new Color(col.r, col.g, col.b, 0.6f) : new Color(0.3f, 0.3f, 0.32f, 0.5f);
                v.Name.text = s.name;
                v.Name.color = unlocked ? Color.white : new Color(0.7f, 0.7f, 0.7f);
                v.Lock.text = unlocked ? "" : s.circle > 0 ? (s.circle + "서클") : ("Lv." + s.unlockLevel);
                float left = _st.CooldownLeft(s.id);
                bool cooling = unlocked && left > 0.02f;
                v.Sweep.enabled = cooling;
                v.Cd.text = cooling ? (left >= 10f ? Mathf.CeilToInt(left).ToString() : left.ToString("0.0")) : "";
                if (cooling)
                {
                    // The dark cover drains from the top as the cooldown runs out.
                    var r = v.Sweep.rectTransform;
                    r.anchorMin = Vector2.zero; r.anchorMax = new Vector2(1, _st.CooldownRatio(s.id));
                    r.offsetMin = r.offsetMax = Vector2.zero;
                }
                v.Bg.color = dead ? new Color(0.07f, 0.08f, 0.1f, 0.4f) : new Color(0.07f, 0.08f, 0.1f, 0.85f);
            }

            var bow = job != null && job.bow != null && job.bow.enabled ? job.bow : null;
            _bowRoot.gameObject.SetActive(bow != null);
            if (bow != null) UpdateBowUi(bow);
            bool charging = bow != null && _combat != null && _combat.Charging;
            _chargeBar.Root.gameObject.SetActive(charging);
            _chargeLabel.enabled = charging;
            if (charging) UpdateChargeUi(bow);

            _dodgeText.enabled = true;
            if (bow == null)
            {
                float dl = _combat != null ? _combat.DodgeCooldownLeft : 0f;
                _dodgeText.text = dl > 0.05f ? $"구르기 (우클릭)  {dl:0.0}s" : "구르기 (우클릭)";
            }
            else _dodgeText.text = "누르고 있으면 차징 · 우클릭: 조준 확대";

            UpdateTrialBanner();
        }

        void UpdateBowUi(BowRules bow)
        {
            var a = _st.Ammo;
            int ammo = a != null ? a.Ammo : bow.magazine;
            int max = a != null ? Mathf.Max(1, a.Max) : bow.magazine;
            float reloadLeft = a != null && a.ReloadIn > 0 ? Mathf.Max(0f, a.ReloadIn - (Time.time - _st.AmmoAt)) : 0f;
            var sb = new StringBuilder();
            for (int i = 0; i < max; i++) sb.Append(i < ammo ? "<color=#ffe27a>●</color> " : "<color=#555a60>○</color> ");
            _ammoText.text = sb.ToString();
            bool reloading = reloadLeft > 0.01f;
            _reloadBar.Root.gameObject.SetActive(reloading);
            if (reloading) _reloadBar.Set(1f - reloadLeft / Mathf.Max(0.1f, bow.reloadSec), $"장전 {reloadLeft:0.0}s");
        }

        void UpdateChargeUi(BowRules bow)
        {
            int last = bow.tiers.Length - 1;
            float full = bow.tiers[last].minHold;
            float rate = _st.BuffValue("chargeRate");
            // The bar fills toward the last tier; the color tells which tier you would shoot right now.
            _chargeBar.Set(Mathf.Clamp01(_combat.ChargeHold / Mathf.Max(0.1f, full)));
            var fill = _chargeBar.Root.GetChild(0).GetComponent<Image>();
            if (fill) fill.color = _combat.ChargeTier >= last ? new Color(1f, 0.45f, 0.3f) : _combat.ChargeTier >= 1 ? new Color(1f, 0.8f, 0.25f) : new Color(0.85f, 0.85f, 0.7f);
            string pen = _combat.ChargeTier >= 0 && bow.tiers[_combat.ChargeTier].penetration > 0 ? $" · 관통 {bow.tiers[_combat.ChargeTier].penetration * 100:0}%" : "";
            _chargeLabel.text = $"{_combat.ChargeLabel}  {bow.tiers[_combat.ChargeTier].coefficient * 100:0}%{pen}";
        }

        void UpdateTrialBanner()
        {
            bool on = _st.TrialActive;
            _trialBanner.enabled = on;
            if (!on) return;
            float left = Mathf.Max(0f, _st.Trial.TimeLeft - (Time.time - _st.TrialAt));
            var job = _st.Data.Job(_st.Trial.JobId);
            _trialBanner.text = $"전직 시험 · {job?.name ?? ""}  —  시험관을 쓰러뜨려!  <color=#ffffff>{(int)left / 60}:{(int)left % 60:00}</color>";
        }

        // ───────────── skill window ─────────────

        void RefreshSkillWindow()
        {
            if (_skillWin == null || !_skillWin.activeSelf || _st.Char == null) return;
            var c = _st.Char;
            var job = _st.Job;
            var sb = new StringBuilder();
            if (job == null)
            {
                sb.Append("아직 직업이 없어.\n");
                sb.Append($"<color=#9fd18b>{_st.Data.character.jobChangeLevel}레벨이 되면</color> 마을 동쪽 수련장의 스승(검술 스승·궁술 스승)에게 전직 시험을 받을 수 있어.");
            }
            else
            {
                int tier = Mathf.Clamp(c.JobTier - 1, 0, 2);
                string tierName = job.tierNames != null && job.tierNames.Length > tier ? job.tierNames[tier] : job.name;
                sb.Append($"<b>{job.name}</b>  <color=#a8adb3>({tierName})</color>  ·  {job.role}\n{job.description}\n");
                if (job.training != null && job.training.enabled && _st.Stats != null && _st.Skills != null)
                {
                    int cap = job.training.dailyCap;
                    sb.Append($"<color=#f2c75a>훈련 단계 {_st.Stats.TrainingLevel}</color> (공격력 +{_st.Stats.TrainingBonus * 100:0.#}%)  ·  훈련량 {_st.Skills.Training}  ·  수련장 오늘 {_st.Skills.TrainingToday}/{cap}");
                }
            }
            _skillInfo.text = sb.ToString();

            int rules = _st.Data.skillRules.maxLevel;
            for (int i = 0; i < 6; i++)
            {
                var s = _st.SkillAt(i);
                if (s == null)
                {
                    _skillRows[i].text = $"<color=#666b70>{KeyBindings.KeyName(KeyBindings.Skill(i))}  (비어 있음)</color>";
                    _skillRowIcons[i].enabled = false;
                    continue;
                }
                bool unlocked = _st.SkillUnlocked(s);
                _skillRowIcons[i].enabled = true;
                var col = Placeholders.ParseColor(s.color, Color.white);
                _skillRowIcons[i].color = unlocked ? new Color(col.r, col.g, col.b, 0.65f) : new Color(0.3f, 0.3f, 0.32f, 0.6f);
                int level = _st.SkillLevel(s.id);
                int uses = _st.SkillUses(s.id);
                int need = _st.Data.skillRules.UsesToNext(level);
                string mastery = level >= rules ? "<color=#f2c75a>숙련 최대</color>" : $"숙련 {uses}/{need}";
                string head = unlocked
                    ? $"<b>[{KeyBindings.KeyName(KeyBindings.Skill(i))}] {s.name}</b>  <color=#f2c75a>Lv.{level}</color>  <color=#a8adb3>· 쿨타임 {s.cooldown:0.#}초{(s.manaCost > 0 ? " · 마나 " + s.manaCost : "")} · {mastery}</color>"
                    : $"<b>[{KeyBindings.KeyName(KeyBindings.Skill(i))}] {s.name}</b>  <color=#ff9a7a>{(s.circle > 0 ? s.circle + "서클에 배움" : "Lv." + s.unlockLevel + "에 배움")}</color>";
                float power = s.coefficient * (1f + _st.Data.skillRules.powerPerLevel * (level - 1)) * 100f;
                string hits = s.hits > 1 ? $" × {s.hits}" : s.projectiles > 1 ? $" × {s.projectiles}" : "";
                string body = s.kind == "buff" || s.kind == "stance" ? s.description : $"{s.description}  <color=#a8adb3>(위력 {power:0}%{hits})</color>";
                _skillRows[i].text = head + "\n" + body;
            }
        }
    }
}
