using System.Text;
using Game.Client.Core;
using Game.Shared.Protocol;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Client.UI
{
    /// <summary>Stat points in the character window (spec vol.2 2.1: 힘·민첩·지능·체력·정신에 직접 분배) and the derived combat stats (2.2 / 2.3).</summary>
    public sealed partial class GameUI
    {
        static readonly string[] StatNames = { "힘", "민첩", "지능", "체력", "정신" };
        static readonly string[] StatKeys = { "str", "dex", "int", "con", "spi" };

        Text[] _statLabels;
        Button[] _statPlus, _statMinus, _statPlus10, _statMax;
        readonly int[] _pend = new int[5];
        Text _statPoints, _statHint, _derived;
        Button _statApply, _statUndo;
        string _statSig = "";

        void BuildStatPanel(RectTransform w)
        {
            _statLabels = new Text[5];
            _statPlus = new Button[5];
            _statMinus = new Button[5];
            _statPlus10 = new Button[5];
            _statMax = new Button[5];
            for (int i = 0; i < 5; i++)
            {
                int idx = i;
                float y = -172 - i * 32;
                var label = UiKit.Label("Stat" + i, w, 15, TextAnchor.MiddleLeft, UiKit.TextMain);
                label.rectTransform.Place(new Vector2(0, 1), new Vector2(0, 1), new Vector2(18, y), new Vector2(160, 28));
                _statLabels[i] = label;
                _statPlus[i] = SmallButton(w, "+", new Vector2(182, y), () => { if (PointsLeft() > 0) { _pend[idx]++; RefreshStatPanel(); } });
                _statMinus[i] = SmallButton(w, "-", new Vector2(214, y), () => { if (_pend[idx] > 0) { _pend[idx]--; RefreshStatPanel(); } });
                _statPlus10[i] = StatWideButton(w, "+10", new Vector2(250, y), 44, () => { int n = Mathf.Min(10, PointsLeft()); if (n > 0) { _pend[idx] += n; RefreshStatPanel(); } });
                _statMax[i] = StatWideButton(w, "전부", new Vector2(298, y), 44, () => { int n = PointsLeft(); if (n > 0) { _pend[idx] += n; RefreshStatPanel(); } });
            }
            _statPoints = UiKit.Label("Points", w, 15, TextAnchor.MiddleLeft, UiKit.TextMain);
            _statPoints.rectTransform.Place(new Vector2(0, 1), new Vector2(0, 1), new Vector2(18, -336), new Vector2(230, 26));
            _statApply = UiKit.Button("Apply", w, "적용", 15, ApplyStats);
            _statApply.GetComponent<RectTransform>().Place(new Vector2(0, 1), new Vector2(0, 1), new Vector2(18, -366), new Vector2(104, 30));
            _statUndo = UiKit.Button("Undo", w, "되돌리기", 15, () => { System.Array.Clear(_pend, 0, 5); RefreshStatPanel(); });
            _statUndo.GetComponent<RectTransform>().Place(new Vector2(0, 1), new Vector2(0, 1), new Vector2(130, -366), new Vector2(104, 30));
            foreach (var b in new[] { _statApply, _statUndo }) Center(b);

            _statHint = UiKit.Label("Hint", w, 12, TextAnchor.UpperLeft, UiKit.TextDim);
            _statHint.rectTransform.Place(new Vector2(0, 1), new Vector2(0, 1), new Vector2(18, -410), new Vector2(226, 200));
            _statHint.text = "힘: 방어력 +0.5\n민첩: 치명타 확률 +0.05%p · 공격속도 +0.05%\n지능: 최대 마나 +8 · 마나 재생\n체력: 최대 HP +20 · 방어력 +1 · HP 재생\n정신: 쿨타임 감소 +0.03%p · CC 저항 +0.05%p\n\n<color=#f2c75a>직업의 주 스탯</color>은 1포인트당 공격력 +2 (전사는 체력: 공격력 +1, 방어력 +1).\n\n<color=#a8adb3>[+] 1점 · [+10] 10점 · [전부] 남은 점수 전부. 나눈 뒤 [적용]을 눌러야 저장돼.</color>";

            _derived = UiKit.Label("Derived", w, 14, TextAnchor.UpperLeft, UiKit.TextMain);
            _derived.rectTransform.Place(new Vector2(0, 1), new Vector2(0, 1), new Vector2(262, -338), new Vector2(300, 300));
        }

        static Button SmallButton(RectTransform parent, string text, Vector2 pos, System.Action click)
        {
            var b = UiKit.Button("Btn" + text, parent, text, 17, click);
            b.GetComponent<RectTransform>().Place(new Vector2(0, 1), new Vector2(0, 1), pos, new Vector2(28, 28));
            Center(b);
            return b;
        }

        static Button StatWideButton(RectTransform parent, string text, Vector2 pos, float width, System.Action click)
        {
            var b = UiKit.Button("Btn" + text, parent, text, 14, click);
            b.GetComponent<RectTransform>().Place(new Vector2(0, 1), new Vector2(0, 1), pos, new Vector2(width, 28));
            Center(b);
            return b;
        }

        static void Center(Button b)
        {
            var t = b.GetComponentInChildren<Text>();
            t.alignment = TextAnchor.MiddleCenter;
            t.rectTransform.offsetMin = Vector2.zero;
        }

        int Pending() { int n = 0; foreach (var v in _pend) n += v; return n; }
        int PointsLeft() { return _st.Char == null ? 0 : _st.Char.StatPoints - Pending(); }

        int StatValue(CharacterSync c, int i)
        {
            switch (i) { case 0: return c.Str; case 1: return c.Dex; case 2: return c.Int; case 3: return c.Con; default: return c.Spi; }
        }

        void ApplyStats()
        {
            if (Pending() <= 0 || _st.Char == null) return;
            _net.Send(new AllocateStats { Str = (ushort)_pend[0], Dex = (ushort)_pend[1], Int = (ushort)_pend[2], Con = (ushort)_pend[3], Spi = (ushort)_pend[4] });
            // Keep showing the pending numbers until the server's CharacterSync arrives (Refresh then clears them).
        }

        void RefreshStatPanel()
        {
            var c = _st.Char;
            if (c == null || _statLabels == null) return;
            string sig = $"{c.StatPoints}|{c.Str}|{c.Dex}|{c.Int}|{c.Con}|{c.Spi}";
            if (sig != _statSig) { _statSig = sig; System.Array.Clear(_pend, 0, 5); }   // the server answered: its numbers win

            var job = _st.Job;
            int left = PointsLeft();
            for (int i = 0; i < 5; i++)
            {
                int v = StatValue(c, i);
                string star = job != null && job.primaryStat == StatKeys[i] ? "  <color=#f2c75a>★</color>" : "";
                string change = _pend[i] > 0 ? $" <color=#9fd18b>→ {v + _pend[i]}</color>" : "";
                _statLabels[i].text = $"{StatNames[i]}  <b>{v}</b>{change}{star}";
                _statPlus[i].interactable = left > 0;
                _statMinus[i].interactable = _pend[i] > 0;
                _statPlus10[i].interactable = left > 0;
                _statMax[i].interactable = left > 0;
            }
            _statPoints.text = $"남은 스탯 포인트  <color=#9fd18b><b>{left}</b></color>";
            _statApply.interactable = Pending() > 0;
            _statUndo.interactable = Pending() > 0;

            var s = _st.Stats;
            var sb = new StringBuilder();
            if (s != null)
            {
                sb.Append("<color=#f2c75a><b>전투 능력치</b></color>\n");
                sb.Append($"공격력 <b>{s.Attack}</b>");
                if (s.TrainingBonus > 0.0001f) sb.Append($"  <color=#9fd18b>(훈련 +{s.TrainingBonus * 100:0.#}%)</color>");
                sb.Append($"\n방어력 <b>{s.Defense}</b>\n");
                sb.Append($"최대 HP <b>{s.MaxHp}</b>   최대 마나 <b>{s.MaxMana}</b>\n");
                sb.Append($"치명타 확률 <b>{s.CritChance * 100:0.##}%</b>   치명타 피해 <b>{s.CritDamage * 100:0}%</b>\n");
                sb.Append($"공격 속도 <b>{s.AttackSpeed * 100:0.##}%</b>   이동 속도 <b>{s.MoveSpeed * 100:0}%</b>\n");
                sb.Append($"쿨타임 감소 <b>{s.CdReduction * 100:0.##}%</b>   CC 저항 <b>{s.CcResist * 100:0.##}%</b>\n");
                sb.Append($"방어 관통 <b>{s.Penetration * 100:0.#}%</b>   흡혈 <b>{s.Lifesteal * 100:0.#}%</b>\n");
                sb.Append($"HP 재생(전투 중) <b>{s.HpRegen:0.#}/초</b>   마나 재생 <b>{s.ManaRegen:0.#}/초</b>\n");
                sb.Append($"버프·힐 효율 <b>+{s.HealPower * 100:0.#}%</b>");
            }
            _derived.text = sb.ToString();
        }
    }
}
