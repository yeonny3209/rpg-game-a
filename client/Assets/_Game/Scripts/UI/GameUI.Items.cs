using System.Collections.Generic;
using System.Text;
using Game.Client.Core;
using Game.Client.World;
using Game.Shared.Data;
using Game.Shared.Protocol;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Game.Client.UI
{
    /// <summary>
    /// 3a 아이템 · 경제 UI: paged bag, equip / use / sell by right-click, grade colours + tooltips (grade, base stats, random options),
    /// bag expansion, the NPC shop window and the potion hotkeys.
    /// </summary>
    public sealed partial class GameUI
    {
        const int BagPageSize = 40;
        int _bagPage;
        Button _bagPrev, _bagNext, _expandBtn;
        Text _bagPageLabel, _bagGold;
        Text _potionHud;

        // tooltip
        RectTransform _tipPanel;
        Text _tipText;
        bool _tipVisible;
        Vector2 _tipSize = new Vector2(300, 100);

        // shop
        GameObject _shopWin;
        Text _shopTitle, _shopHint, _shopPageLabel;
        readonly List<ShopRow> _shopRows = new List<ShopRow>();
        Button _shopPrev, _shopNext;
        ShopOpen _shop;
        int _shopPage;
        const int ShopRowsPerPage = 8;

        sealed class ShopRow
        {
            public GameObject Root;
            public Image Icon;
            public Text Name, Price;
            public Button Buy;
            public SlotPointer Pointer;
            public int Index = -1;
        }

        public bool ShopOpen => _shopWin && _shopWin.activeSelf;

        // ───────────── build ─────────────

        void BuildItemsUi()
        {
            // bag paging + expansion + gold
            var inv = _invWin.GetComponent<RectTransform>();
            float y = -50 - 5 * 62 - 8;
            _bagPrev = StatSmall(inv, "<", new Vector2(15, y), () => { _bagPage = Mathf.Max(0, _bagPage - 1); RefreshInventoryPaged(); });
            _bagPageLabel = UiKit.Label("Page", inv, 14, TextAnchor.MiddleCenter, UiKit.TextMain);
            _bagPageLabel.rectTransform.Place(new Vector2(0, 1), new Vector2(0, 1), new Vector2(48, y), new Vector2(60, 28));
            _bagNext = StatSmall(inv, ">", new Vector2(112, y), () => { _bagPage++; RefreshInventoryPaged(); });
            _expandBtn = UiKit.Button("Expand", inv, "칸 늘리기", 14, () => _net.Send(new ExpandBag()));
            _expandBtn.GetComponent<RectTransform>().Place(new Vector2(0, 1), new Vector2(0, 1), new Vector2(160, y), new Vector2(150, 28));
            _bagGold = UiKit.Label("Gold", inv, 14, TextAnchor.MiddleRight, UiKit.Accent);
            _bagGold.rectTransform.Place(new Vector2(1, 1), new Vector2(1, 1), new Vector2(-16, y), new Vector2(200, 28));
            var hint = UiKit.Label("Hint", inv, 12, TextAnchor.MiddleLeft, UiKit.TextDim);
            hint.rectTransform.Place(new Vector2(0, 1), new Vector2(0, 1), new Vector2(15, y - 32), new Vector2(500, 20));
            hint.text = "우클릭(또는 더블클릭): 장비 착용 · 포션 사용 · 상점이 열려 있으면 판매 (Shift: 전부)";

            for (int i = 0; i < _bag.Length; i++)
            {
                int idx = i;
                _bag[i].Pointer.Clicked = e => OnBagClick(idx, e);
                _bag[i].Pointer.Hover = on => OnSlotHover(_bag[idx], on);
            }
            for (int i = 0; i < _equip.Length; i++)
            {
                int idx = i;
                _equip[i].Pointer.Clicked = e => { if (IsUse(e)) _net.Send(new UnequipItem { EquipSlot = (byte)idx }); };
                _equip[i].Pointer.Hover = on => OnSlotHover(_equip[idx], on);
            }

            // tooltip (top layer)
            var tp = UiKit.Panel("Tooltip", _root, new Color(0.04f, 0.05f, 0.07f, 0.96f));
            tp.raycastTarget = false;
            _tipPanel = tp.rectTransform;
            _tipPanel.Place(new Vector2(0.5f, 0.5f), new Vector2(0, 1), Vector2.zero, _tipSize);
            _tipText = UiKit.Label("Text", _tipPanel, 14, TextAnchor.UpperLeft, UiKit.TextMain);
            _tipText.rectTransform.Fill(8);
            _tipPanel.gameObject.SetActive(false);

            _potionHud = UiKit.Label("Potions", _root, 14, TextAnchor.MiddleLeft, UiKit.TextMain, true);
            _potionHud.rectTransform.Place(new Vector2(0, 0), new Vector2(0, 0), new Vector2(18, 150), new Vector2(420, 24));

            BuildShop();
        }

        Button StatSmall(RectTransform parent, string text, Vector2 pos, System.Action click)
        {
            var b = UiKit.Button("Btn" + text, parent, text, 16, click);
            b.GetComponent<RectTransform>().Place(new Vector2(0, 1), new Vector2(0, 1), pos, new Vector2(30, 28));
            var t = b.GetComponentInChildren<Text>();
            t.alignment = TextAnchor.MiddleCenter;
            t.rectTransform.offsetMin = Vector2.zero;
            return b;
        }

        void BuildShop()
        {
            var w = Window("Shop", new Vector2(0.5f, 0.55f), new Vector2(660, 150 + ShopRowsPerPage * 46), out _shopTitle, CloseShop);
            _shopWin = w.gameObject;
            for (int i = 0; i < ShopRowsPerPage; i++)
            {
                var row = new ShopRow();
                float y = -48 - i * 46;
                var bg = UiKit.Panel("Row" + i, w, new Color(0.14f, 0.15f, 0.18f, 0.9f));
                bg.rectTransform.Place(new Vector2(0, 1), new Vector2(0, 1), new Vector2(14, y), new Vector2(632, 42));
                row.Root = bg.gameObject;
                row.Pointer = bg.gameObject.AddComponent<SlotPointer>();
                row.Icon = UiKit.Panel("Icon", bg.transform, Color.gray);
                row.Icon.raycastTarget = false;
                row.Icon.rectTransform.Place(new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(6, 0), new Vector2(30, 30));
                row.Name = UiKit.Label("Name", bg.transform, 16, TextAnchor.MiddleLeft, UiKit.TextMain);
                row.Name.rectTransform.Place(new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(46, 0), new Vector2(340, 34));
                row.Price = UiKit.Label("Price", bg.transform, 15, TextAnchor.MiddleRight, UiKit.Accent);
                row.Price.rectTransform.Place(new Vector2(1, 0.5f), new Vector2(1, 0.5f), new Vector2(-110, 0), new Vector2(150, 34));
                int ri = i;
                row.Buy = UiKit.Button("Buy", bg.transform, "구매", 15, () => BuyRow(ri));
                row.Buy.GetComponent<RectTransform>().Place(new Vector2(1, 0.5f), new Vector2(1, 0.5f), new Vector2(-8, 0), new Vector2(84, 32));
                var bt = row.Buy.GetComponentInChildren<Text>();
                bt.alignment = TextAnchor.MiddleCenter;
                bt.rectTransform.offsetMin = Vector2.zero;
                row.Pointer.Hover = on => OnShopRowHover(ri, on);
                _shopRows.Add(row);
            }
            float by = -48 - ShopRowsPerPage * 46 - 4;
            _shopPrev = StatSmall(w, "<", new Vector2(14, by), () => { _shopPage = Mathf.Max(0, _shopPage - 1); RefreshShop(); });
            _shopPageLabel = UiKit.Label("Page", w, 14, TextAnchor.MiddleCenter, UiKit.TextMain);
            _shopPageLabel.rectTransform.Place(new Vector2(0, 1), new Vector2(0, 1), new Vector2(48, by), new Vector2(70, 28));
            _shopNext = StatSmall(w, ">", new Vector2(122, by), () => { _shopPage++; RefreshShop(); });
            _shopHint = UiKit.Label("Hint", w, 13, TextAnchor.MiddleLeft, UiKit.TextDim);
            _shopHint.rectTransform.Place(new Vector2(0, 1), new Vector2(0, 1), new Vector2(170, by), new Vector2(480, 28));
            _shopHint.text = "구매: Shift+클릭 = 10개(포션). 팔기: 가방의 물건을 우클릭 (Shift: 전부).";
            _shopWin.SetActive(false);
        }

        // ───────────── bag ─────────────

        Color GradeColor(int grade) => Placeholders.ParseColor(_st.Data.itemRules.Grade(grade).color, Color.gray);

        static bool IsUse(PointerEventData e) => e.button == PointerEventData.InputButton.Right || (e.button == PointerEventData.InputButton.Left && e.clickCount >= 2);

        void RefreshInventoryPaged()
        {
            if (_bag == null) return;
            int size = _st.Inv != null ? _st.Inv.BagSize : _bag.Length;
            int pages = Mathf.Max(1, Mathf.CeilToInt(size / (float)BagPageSize));
            _bagPage = Mathf.Clamp(_bagPage, 0, pages - 1);
            foreach (var s in _bag) s.Set(null, 0);
            foreach (var s in _equip) s.Set(null, 0);
            int used = 0;
            if (_st.Inv != null)
            {
                foreach (var it in _st.Inv.Items)
                {
                    var def = _st.Data.Item(it.ItemId);
                    if (it.Container == ItemContainer.Bag)
                    {
                        used++;
                        int local = it.Slot - _bagPage * BagPageSize;
                        if (local >= 0 && local < _bag.Length) _bag[local].Set(def, it.Count, GradeColor(it.Grade), it.Grade, it.Opts);
                    }
                    else if (it.Container == ItemContainer.Equipment && it.Slot < _equip.Length) _equip[it.Slot].Set(def, it.Count, GradeColor(it.Grade), it.Grade, it.Opts);
                }
            }
            _invTitle.text = $"가방  <size=14><color=#a8adb3>{used} / {size}칸</color></size>";
            _bagPageLabel.text = $"{_bagPage + 1} / {pages}";
            _bagPrev.interactable = _bagPage > 0;
            _bagNext.interactable = _bagPage < pages - 1;
            var rules = _st.Data.itemRules.bag;
            if (size >= rules.maxSlots) { _expandBtn.interactable = false; _expandBtn.GetComponentInChildren<Text>().text = "최대 칸"; }
            else
            {
                long cost = rules.baseCost << Mathf.Max(0, (size - _st.Data.character.bagSlots) / Mathf.Max(1, rules.step));
                _expandBtn.interactable = true;
                _expandBtn.GetComponentInChildren<Text>().text = $"+{rules.step}칸 · {cost:N0}G";
            }
            if (_st.Char != null) _bagGold.text = $"골드 {_st.Char.Gold:N0}";
            RefreshPotionHud();
            if (ShopOpen) RefreshShop();
        }

        void OnBagClick(int local, PointerEventData e)
        {
            if (!IsUse(e)) return;
            var s = _bag[local];
            if (s.Def == null) return;
            int slot = _bagPage * BagPageSize + local;
            bool shift = Keyboard.current != null && (Keyboard.current.leftShiftKey.isPressed || Keyboard.current.rightShiftKey.isPressed);
            if (ShopOpen)
            {
                _net.Send(new ShopSell { BagSlot = (ushort)slot, Count = (ushort)(shift ? s.Count : 1) });
                return;
            }
            if (!string.IsNullOrEmpty(s.Def.equipSlot)) _net.Send(new EquipItem { BagSlot = (ushort)slot });
            else if (s.Def.type == "consumable") _net.Send(new UseItem { BagSlot = (ushort)slot });
        }

        // ───────────── potions ─────────────

        /// <summary>The cheapest potion of the kind first (spec vol.4 2.9: no cooldown, so spamming is fine).</summary>
        void UsePotion(bool hp)
        {
            if (_st.Inv == null || _st.Char == null || _st.Char.Dead) return;
            ItemSlotState best = default; int bestVal = int.MaxValue; bool found = false;
            foreach (var it in _st.Inv.Items)
            {
                if (it.Container != ItemContainer.Bag) continue;
                var def = _st.Data.Item(it.ItemId);
                if (def == null || def.type != "consumable") continue;
                int v = hp ? def.heal : def.mana;
                if (v <= 0 || v >= bestVal) continue;
                best = it; bestVal = v; found = true;
            }
            if (found) _net.Send(new UseItem { BagSlot = best.Slot });
            else ShowCenter(hp ? "HP 포션이 없어." : "마나 포션이 없어.", true);
        }

        void RefreshPotionHud()
        {
            if (_st.Inv == null || _potionHud == null) return;
            int hp = 0, mp = 0;
            foreach (var it in _st.Inv.Items)
            {
                if (it.Container != ItemContainer.Bag) continue;
                var def = _st.Data.Item(it.ItemId);
                if (def == null || def.type != "consumable") continue;
                if (def.heal > 0) hp += it.Count;
                if (def.mana > 0) mp += it.Count;
            }
            _potionHud.text = $"[{KeyBindings.KeyName(KeyBindings.Get(BindAction.HpPotion))}] HP 포션 <b>{hp}</b>    [{KeyBindings.KeyName(KeyBindings.Get(BindAction.MpPotion))}] 마나 포션 <b>{mp}</b>";
        }

        // ───────────── tooltip ─────────────

        void OnSlotHover(SlotView s, bool on)
        {
            if (!on || s.Def == null) { _tipVisible = false; _tipPanel.gameObject.SetActive(false); return; }
            ShowTip(ItemTooltip(s.Def, s.Grade, s.Opts, s.Count));
        }

        void OnShopRowHover(int row, bool on)
        {
            int i = _shopPage * ShopRowsPerPage + row;
            if (!on || _shop == null || i >= _shop.Entries.Count) { _tipVisible = false; _tipPanel.gameObject.SetActive(false); return; }
            var e = _shop.Entries[i];
            ShowTip(ItemTooltip(_st.Data.Item(e.ItemId), e.Grade, "", 1, shopPreview: true));
        }

        void ShowTip(string text)
        {
            _tipText.text = text;
            _tipVisible = true;
            _tipPanel.gameObject.SetActive(true);
            float h = Mathf.Max(60, _tipText.preferredHeight + 18);
            _tipSize = new Vector2(300, h);
            _tipPanel.sizeDelta = _tipSize;
            MoveTip();
        }

        void MoveTip()
        {
            var mouse = Mouse.current;
            if (mouse == null) return;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(_root, mouse.position.ReadValue(), null, out var local))
            {
                var half = _root.rect.size * 0.5f;
                float x = Mathf.Clamp(local.x + 16, -half.x, half.x - _tipSize.x);
                float y = Mathf.Clamp(local.y - 8, -half.y + _tipSize.y, half.y);
                _tipPanel.anchoredPosition = new Vector2(x, y);
            }
            _tipPanel.SetAsLastSibling();
        }

        static string Hex(Color c) => ColorUtility.ToHtmlStringRGB(c);

        string ItemTooltip(ItemDef def, int grade, string opts, int count, bool shopPreview = false)
        {
            if (def == null) return "";
            var rules = _st.Data.itemRules;
            var g = rules.Grade(grade);
            bool gear = !string.IsNullOrEmpty(def.equipSlot);
            var sb = new StringBuilder();
            string gc = Hex(GradeColor(grade));
            sb.Append($"<b><color=#{gc}>{def.name}</color></b>");
            if (gear) sb.Append($"  <color=#{gc}>[{g.name}]</color>");
            sb.Append('\n');
            if (gear)
            {
                int slot = EquipLayout.FirstSlotFor(def.equipSlot);
                string slotName = def.equipSlot == "ring" ? "반지" : def.equipSlot == "artifact" ? "아티팩트" : slot >= 0 ? EquipLayout.SlotLabels[slot] : def.equipSlot;
                sb.Append($"<color=#a8adb3>{slotName}");
                if (def.equipSlot == "weapon") sb.Append($" · {WeaponKindName(def.weaponType)}");
                sb.Append("</color>\n");
                if (def.reqLevel > 0)
                {
                    bool ok = _st.Char != null && _st.Char.Level >= def.reqLevel;
                    sb.Append(ok ? "" : "<color=#ff7a7a>").Append($"필요 레벨 {def.reqLevel}").Append(ok ? "" : "</color>").Append('\n');
                }
                if (def.equipSlot == "weapon") sb.Append($"공격력 <b>{Mathf.RoundToInt(def.weapon.attack * g.statMult)}</b>\n");
                if (def.defense > 0) sb.Append($"방어력 <b>{Mathf.RoundToInt(def.defense * g.statMult)}</b>\n");
                if (def.bonusAttack > 0) sb.Append($"공격력 <b>+{Mathf.RoundToInt(def.bonusAttack * g.statMult)}</b>\n");
                if (def.bonusHp > 0) sb.Append($"최대 HP <b>+{Mathf.RoundToInt(def.bonusHp * g.statMult)}</b>\n");
                if (!shopPreview && !string.IsNullOrEmpty(opts))
                {
                    sb.Append("<color=#7fe0ff>");
                    foreach (var part in opts.Split(';'))
                    {
                        int c = part.IndexOf(':');
                        if (c <= 0) continue;
                        var od = rules.Option(part.Substring(0, c));
                        if (od == null || !float.TryParse(part.Substring(c + 1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v)) continue;
                        sb.Append($"{od.name} +{(od.percent ? (v * 100f).ToString("0.#") + "%" : v.ToString("0.#") + "/초")}\n");
                    }
                    sb.Append("</color>");
                }
                else if (shopPreview && g.optionLines > 0) sb.Append($"<color=#7fe0ff>랜덤 옵션 {g.optionLines}줄 (구매할 때 정해짐)</color>\n");
            }
            if (def.heal > 0) sb.Append($"HP <b>+{def.heal}</b> 회복\n");
            if (def.mana > 0) sb.Append($"마나 <b>+{def.mana}</b> 회복\n");
            if (!string.IsNullOrEmpty(def.description)) sb.Append($"<color=#a8adb3>{def.description}</color>\n");
            if (def.value > 0 && !shopPreview)
            {
                long sell = System.Math.Max(1L, (long)Mathf.Floor(def.value * g.valueMult * rules.sellRatio));
                sb.Append($"<color=#f2c75a>판매가 {sell:N0}G</color>");
                if (count > 1) sb.Append($" <color=#a8adb3>(×{count})</color>");
            }
            return sb.ToString().TrimEnd('\n');
        }

        static string WeaponKindName(string t)
        {
            switch (t)
            {
                case "sword": return "도"; case "longsword": return "장검"; case "bow": return "활"; case "staff": return "지팡이"; case "dagger": return "단검";
                case "mace": return "한손 무기"; case "holy": return "성물"; case "club": return "몽둥이"; default: return t;
            }
        }

        // ───────────── shop ─────────────

        void HandleShop(ShopOpen s)
        {
            _shop = s;
            _shopPage = 0;
            _shopTitle.text = s.Name;
            _shopWin.SetActive(true);
            _invWin.SetActive(true);
            FreeMouse();
            RefreshShop();
        }

        void CloseShop()
        {
            _shopWin.SetActive(false);
            _shop = null;
            _tipVisible = false;
            _tipPanel.gameObject.SetActive(false);
        }

        void RefreshShop()
        {
            if (_shop == null) return;
            int pages = Mathf.Max(1, Mathf.CeilToInt(_shop.Entries.Count / (float)ShopRowsPerPage));
            _shopPage = Mathf.Clamp(_shopPage, 0, pages - 1);
            long gold = _st.Char != null ? _st.Char.Gold : 0;
            for (int i = 0; i < _shopRows.Count; i++)
            {
                var row = _shopRows[i];
                int idx = _shopPage * ShopRowsPerPage + i;
                row.Root.SetActive(idx < _shop.Entries.Count);
                if (idx >= _shop.Entries.Count) { row.Index = -1; continue; }
                var e = _shop.Entries[idx];
                var def = _st.Data.Item(e.ItemId);
                row.Index = idx;
                var col = Placeholders.ParseColor(def != null ? def.color : "#999999", Color.gray);
                col.a = 0.7f;
                row.Icon.color = col;
                string gname = e.Grade > 0 ? $" <size=12>[{_st.Data.itemRules.Grade(e.Grade).name}]</size>" : "";
                row.Name.text = $"<color=#{Hex(GradeColor(e.Grade))}>{(def != null ? def.name : e.ItemId)}</color>{gname}" + (def != null && def.reqLevel > 0 ? $"  <size=12><color=#a8adb3>Lv.{def.reqLevel}</color></size>" : "");
                row.Price.text = $"{e.Price:N0} G";
                row.Price.color = gold >= e.Price ? UiKit.Accent : new Color(1f, 0.5f, 0.45f);
                row.Buy.interactable = gold >= e.Price;
            }
            _shopPageLabel.text = $"{_shopPage + 1} / {pages}";
            _shopPrev.interactable = _shopPage > 0;
            _shopNext.interactable = _shopPage < pages - 1;
        }

        void BuyRow(int row)
        {
            if (_shop == null) return;
            int idx = _shopPage * ShopRowsPerPage + row;
            if (idx >= _shop.Entries.Count) return;
            var def = _st.Data.Item(_shop.Entries[idx].ItemId);
            bool shift = Keyboard.current != null && (Keyboard.current.leftShiftKey.isPressed || Keyboard.current.rightShiftKey.isPressed);
            int count = shift && def != null && string.IsNullOrEmpty(def.equipSlot) ? 10 : 1;
            _net.Send(new ShopBuy { Index = (ushort)idx, Count = (ushort)count });
        }

        // ───────────── per frame ─────────────

        void UpdateItemsUi()
        {
            if (_tipVisible) MoveTip();
            if (ShopOpen)
            {
                // walked away from the shopkeeper: close (the server checks the distance too)
                var npc = _shop != null && _ents != null ? _ents.Get(_shop.NpcEntityId) : null;
                if (npc == null || (npc.transform.position - _player.position).sqrMagnitude > 7f * 7f) CloseShop();
            }
        }
    }
}
