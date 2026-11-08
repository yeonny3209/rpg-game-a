using Game.Server.Data;
using Game.Server.Logic;
using Game.Server.Net;
using Game.Shared.Data;
using Game.Shared.Protocol;

namespace Game.Server.World;

/// <summary>
/// 3a 아이템 · 경제 (spec vol.4 2 / 3): equipping gear, grades and random options, potions, bag expansion, NPC shops.
/// Everything is decided here; the client only asks.
/// </summary>
public sealed partial class Zone
{
    void Notify(PlayerEntity p, string text, bool error = true) =>
        p.Conn.Send(new Notice { Kind = error ? NoticeKind.Error : NoticeKind.Info, Text = text });

    void InventoryChanged(PlayerEntity p, bool stats)
    {
        p.InvDirty = p.SaveDirty = true;
        if (stats) RecalcStats(p);
    }

    // ───────────── drops and grades ─────────────

    int RollGrade(Dictionary<string, int> weights)
    {
        int total = 0;
        foreach (var w in weights.Values) total += Math.Max(0, w);
        if (total <= 0) return 0;
        int roll = _rng.Next(total);
        foreach (var kv in weights)
        {
            roll -= Math.Max(0, kv.Value);
            if (roll < 0) return Math.Max(0, _d.itemRules.GradeIndex(kv.Key));
        }
        return 0;
    }

    /// <summary>A piece of gear of the given grade with fresh random options goes into the bag.</summary>
    void GiveGear(PlayerEntity p, string itemId, int grade)
    {
        var stack = ItemFactory.Roll(_d, itemId, grade, _rng);
        if (p.Inv.AddInstance(stack)) Gain(p, GainKind.Item, itemId, 1);
        else Gain(p, GainKind.ItemLost, itemId, 1);
        p.InvDirty = p.SaveDirty = true;
    }

    // ───────────── equip / unequip ─────────────

    void OnEquipItem(PlayerEntity p, EquipItem m)
    {
        if (p.Dead) return;
        if ((p.Rec.Unlocks & UnlockFlags.Equipment) == 0) { Notify(p, "장비 시스템이 아직 잠겨 있어."); return; }
        if (p.TrialJob != null) { Notify(p, "시험 중에는 장비를 바꿀 수 없어."); return; }
        if (m.BagSlot >= p.Inv.Bag.Length) return;
        var stack = p.Inv.Bag[m.BagSlot];
        var def = stack == null ? null : _d.Item(stack.ItemId);
        if (def == null || string.IsNullOrEmpty(def.equipSlot) || EquipLayout.FirstSlotFor(def.equipSlot) < 0) { Notify(p, "장비할 수 없는 물건이야."); return; }
        if (def.equipSlot == "artifact") { Notify(p, "아티팩트는 아직 쓸 수 없어."); return; }
        if (p.Rec.Level < def.reqLevel) { Notify(p, $"{def.name}은(는) Lv.{def.reqLevel}부터 쓸 수 있어."); return; }
        if (def.equipSlot == "weapon")
        {
            var job = _d.Job(p.Rec.Job);
            string need = job != null ? job.weaponType : "club";
            if (def.weaponType != need) { Notify(p, job != null ? $"{job.name}은(는) {WeaponTypeName(need)} 계열 무기만 쓸 수 있어." : "직업이 없으면 몽둥이만 쓸 수 있어."); return; }
        }
        int slot = p.Inv.SlotFor(def);
        var old = p.Inv.Equip[slot];
        p.Inv.Equip[slot] = stack;
        p.Inv.Bag[m.BagSlot] = old;                 // swap: what was worn takes the freed bag slot
        InventoryChanged(p, stats: true);
        Audit(p, "equip", new { item = def.id, grade = stack.Grade, slot });
    }

    void OnUnequipItem(PlayerEntity p, UnequipItem m)
    {
        if (p.Dead) return;
        if (p.TrialJob != null) { Notify(p, "시험 중에는 장비를 바꿀 수 없어."); return; }
        if (m.EquipSlot >= p.Inv.Equip.Length) return;
        var stack = p.Inv.Equip[m.EquipSlot];
        if (stack == null) return;
        if (!p.Inv.AddInstance(stack)) { Notify(p, "가방이 가득 차서 벗을 수 없어."); return; }
        p.Inv.Equip[m.EquipSlot] = null;
        InventoryChanged(p, stats: true);
    }

    static string WeaponTypeName(string t) => t switch
    {
        "sword" => "도", "longsword" => "장검", "bow" => "활", "staff" => "지팡이", "dagger" => "단검", "mace" => "한손 무기", "holy" => "성물", "club" => "몽둥이", _ => t,
    };

    // ───────────── potions ─────────────

    void OnUseItem(PlayerEntity p, UseItem m)
    {
        if (p.Dead || m.BagSlot >= p.Inv.Bag.Length) return;
        var stack = p.Inv.Bag[m.BagSlot];
        var def = stack == null ? null : _d.Item(stack.ItemId);
        if (def == null || def.type != "consumable" || (def.heal <= 0 && def.mana <= 0)) return;
        bool needHp = def.heal > 0 && p.Rec.Hp < p.Stats.MaxHp;
        bool needMp = def.mana > 0 && p.Mana < p.Stats.MaxMana;
        if (!needHp && !needMp) { Notify(p, def.heal > 0 ? "이미 체력이 가득해." : "이미 마나가 가득해."); return; }
        if (needHp)
        {
            int healed = Math.Min(def.heal, p.Stats.MaxHp - p.Rec.Hp);
            p.Rec.Hp += healed;
            p.CharDirty = true;
            Broadcast(new CombatEvent { AttackerId = p.EntityId, TargetId = p.EntityId, Damage = healed, Flags = HitFlags.Heal, TargetHp = p.Rec.Hp, TargetMaxHp = p.Stats.MaxHp });
        }
        if (needMp)
        {
            p.Mana = Math.Min(p.Stats.MaxMana, p.Mana + def.mana);
            SendMana(p);
        }
        if (--stack.Count <= 0) p.Inv.Bag[m.BagSlot] = null;
        InventoryChanged(p, stats: false);
    }

    // ───────────── bag expansion ─────────────

    long ExpandCost(PlayerEntity p)
    {
        var rules = _d.itemRules.bag;
        int steps = Math.Max(0, (p.Inv.Bag.Length - _d.character.bagSlots) / Math.Max(1, rules.step));
        return rules.baseCost << Math.Min(steps, 30);
    }

    void OnExpandBag(PlayerEntity p)
    {
        var rules = _d.itemRules.bag;
        if (p.Dead) return;
        if (p.Inv.Bag.Length >= rules.maxSlots) { Notify(p, "가방은 더 늘릴 수 없어."); return; }
        long cost = ExpandCost(p);
        if (p.Rec.Gold < cost) { Notify(p, $"가방을 늘리려면 {cost:N0}골드가 필요해."); return; }
        p.Rec.Gold -= cost;
        p.Inv.Grow(Math.Min(rules.maxSlots, p.Inv.Bag.Length + rules.step));
        p.CharDirty = true;
        InventoryChanged(p, stats: false);
        Audit(p, "bag_expand", new { size = p.Inv.Bag.Length, cost });
        Notify(p, $"가방이 {p.Inv.Bag.Length}칸이 됐어. (다음 확장 {(p.Inv.Bag.Length >= rules.maxSlots ? "없음" : ExpandCost(p).ToString("N0") + "골드")})", error: false);
    }

    // ───────────── shops ─────────────

    /// <summary>Dialogue action "shop": open the NPC's shop for this player (weapons are filtered to the job's kind).</summary>
    string OpenShop(PlayerEntity p, string shopId)
    {
        if (!_data.Shops.TryGetValue(shopId, out var shop)) return "상점을 열 수 없어.";
        if (p.DialogueNpc == 0) return "상점을 열 수 없어.";
        var job = _d.Job(p.Rec.Job);
        p.ShopView.Clear();
        var open = new ShopOpen { NpcEntityId = p.DialogueNpc, Name = shop.name };
        foreach (var e in shop.entries)
        {
            var def = _d.Item(e.item);
            if (def == null) continue;
            if (def.equipSlot == "weapon" && (job == null || def.weaponType != job.weaponType)) continue;
            int grade = Math.Max(0, _d.itemRules.GradeIndex(e.grade));
            p.ShopView.Add((e.item, grade));
            open.Entries.Add(new ShopEntryState { ItemId = e.item, Grade = (byte)grade, Price = (int)Math.Min(int.MaxValue, ItemFactory.Price(_d, def, grade)) });
        }
        p.ShopNpc = p.DialogueNpc;
        p.Conn.Send(open);
        return null;
    }

    bool ShopReachable(PlayerEntity p)
    {
        if (p.Dead || p.ShopNpc == 0 || !_npcs.TryGetValue(p.ShopNpc, out var n)) return false;
        if (!NpcInRange(p, n, 5f)) { p.ShopNpc = 0; return false; }
        return true;
    }

    void OnShopBuy(PlayerEntity p, ShopBuy m)
    {
        if (!ShopReachable(p) || m.Index >= p.ShopView.Count || m.Count < 1 || m.Count > 99) return;
        var (itemId, grade) = p.ShopView[m.Index];
        var def = _d.Item(itemId);
        if (def == null) return;
        bool gear = !string.IsNullOrEmpty(def.equipSlot);
        int count = gear ? 1 : m.Count;
        long total = ItemFactory.Price(_d, def, grade) * count;
        if (p.Rec.Gold < total) { Notify(p, "골드가 모자라."); return; }
        if (gear ? p.Inv.FreeSlots() < 1 : p.Inv.Room(itemId) < count) { Notify(p, "가방이 가득 찼어."); return; }
        p.Rec.Gold -= total;
        p.CharDirty = true;
        if (gear) p.Inv.AddInstance(ItemFactory.Roll(_d, itemId, grade, _rng));
        else p.Inv.Add(itemId, count);
        if (p.Quests.Tracks(itemId)) p.QuestDirty = true;
        InventoryChanged(p, stats: false);
        Audit(p, "shop_buy", new { item = itemId, grade, count, total });
    }

    void OnShopSell(PlayerEntity p, ShopSell m)
    {
        if (!ShopReachable(p) || m.BagSlot >= p.Inv.Bag.Length || m.Count < 1) return;
        var stack = p.Inv.Bag[m.BagSlot];
        var def = stack == null ? null : _d.Item(stack.ItemId);
        if (def == null) return;
        long unit = ItemFactory.SellPrice(_d, def, stack.Grade);
        if (unit <= 0) { Notify(p, "이건 사 주지 않아."); return; }
        int count = Math.Min(m.Count, stack.Count);
        stack.Count -= count;
        if (stack.Count <= 0) p.Inv.Bag[m.BagSlot] = null;
        p.Rec.Gold += unit * count;
        p.CharDirty = true;
        if (p.Quests.Tracks(def.id)) p.QuestDirty = true;
        InventoryChanged(p, stats: false);
        Audit(p, "shop_sell", new { item = def.id, grade = stack.Grade, count, gold = unit * count });
    }
}
