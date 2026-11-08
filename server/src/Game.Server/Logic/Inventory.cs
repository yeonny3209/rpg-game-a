using System.Globalization;
using Game.Shared.Data;
using Game.Shared.Protocol;

namespace Game.Server.Logic;

/// <summary>One rolled random option (spec vol.4 2.7). Id is an OptionDef id; Value is a fraction for percent options.</summary>
public readonly record struct ItemOpt(string Id, float Value);

public sealed class ItemStack
{
    public string ItemId;
    public int Count;
    /// <summary>Index into itemRules.grades (0 = common). Only equipment has a grade other than 0.</summary>
    public byte Grade;
    public List<ItemOpt> Opts;

    /// <summary>Plain stacks (materials, potions) merge; equipment with a grade or options never does.</summary>
    public bool IsPlain => Grade == 0 && (Opts == null || Opts.Count == 0);

    public string OptsString() => OptionText.Write(Opts);
}

public static class OptionText
{
    public static string Write(List<ItemOpt> opts)
    {
        if (opts == null || opts.Count == 0) return "";
        return string.Join(";", opts.Select(o => o.Id + ":" + o.Value.ToString("0.####", CultureInfo.InvariantCulture)));
    }

    public static List<ItemOpt> Read(string text)
    {
        var list = new List<ItemOpt>();
        if (string.IsNullOrEmpty(text)) return list;
        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int c = part.IndexOf(':');
            if (c <= 0) continue;
            if (float.TryParse(part.AsSpan(c + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) && float.IsFinite(v))
                list.Add(new ItemOpt(part[..c], v));
        }
        return list;
    }
}

/// <summary>Bag (fixed slots, stacking up to the item's maxStack) + equipment slots. Pure logic, no I/O.</summary>
public sealed class Inventory
{
    readonly SharedData _data;
    public ItemStack[] Bag { get; private set; }
    public ItemStack[] Equip { get; } = new ItemStack[EquipLayout.Count];

    public Inventory(SharedData data, int bagSlots)
    {
        _data = data;
        Bag = new ItemStack[bagSlots];
    }

    public static Inventory FromRows(SharedData data, int bagSlots, IEnumerable<ItemRow> rows)
    {
        var inv = new Inventory(data, bagSlots);
        foreach (var r in rows)
        {
            if (data.Item(r.ItemId) == null || r.Count <= 0) continue; // item removed from the tables: drop silently
            var arr = r.Container == ItemContainer.Equipment ? inv.Equip : inv.Bag;
            var stack = new ItemStack { ItemId = r.ItemId, Count = r.Count, Grade = r.Grade, Opts = OptionText.Read(r.Opts) };
            if (r.Slot < 0 || r.Slot >= arr.Length || arr[r.Slot] != null)
            {
                if (stack.IsPlain) inv.Add(r.ItemId, r.Count); else inv.AddInstance(stack);
                continue;
            }
            arr[r.Slot] = stack;
        }
        return inv;
    }

    public List<ItemRow> ToRows()
    {
        var rows = new List<ItemRow>();
        for (int i = 0; i < Bag.Length; i++) if (Bag[i] != null) rows.Add(Row(ItemContainer.Bag, i, Bag[i]));
        for (int i = 0; i < Equip.Length; i++) if (Equip[i] != null) rows.Add(Row(ItemContainer.Equipment, i, Equip[i]));
        return rows;
    }

    static ItemRow Row(ItemContainer c, int slot, ItemStack s) => new(c, slot, s.ItemId, s.Count, s.Grade, s.OptsString());

    public int Count(string itemId)
    {
        int n = 0;
        foreach (var s in Bag) if (s != null && s.ItemId == itemId) n += s.Count;
        foreach (var s in Equip) if (s != null && s.ItemId == itemId) n += s.Count;
        return n;
    }

    public bool Owns(string itemId) => Count(itemId) > 0;

    public int FreeSlots()
    {
        int n = 0;
        foreach (var s in Bag) if (s == null) n++;
        return n;
    }

    /// <summary>Adds a plain stack to the bag. Returns how many did NOT fit.</summary>
    public int Add(string itemId, int count)
    {
        var def = _data.Item(itemId);
        if (def == null || count <= 0) return count;
        int max = Math.Max(1, def.maxStack);
        // Top up existing stacks first.
        for (int i = 0; i < Bag.Length && count > 0; i++)
        {
            var s = Bag[i];
            if (s == null || s.ItemId != itemId || !s.IsPlain || s.Count >= max) continue;
            int add = Math.Min(count, max - s.Count);
            s.Count += add;
            count -= add;
        }
        for (int i = 0; i < Bag.Length && count > 0; i++)
        {
            if (Bag[i] != null) continue;
            int add = Math.Min(count, max);
            Bag[i] = new ItemStack { ItemId = itemId, Count = add };
            count -= add;
        }
        return count;
    }

    /// <summary>Puts a piece of equipment (with its grade and options) into the first free bag slot. false = bag full.</summary>
    public bool AddInstance(ItemStack stack)
    {
        for (int i = 0; i < Bag.Length; i++)
            if (Bag[i] == null) { Bag[i] = stack; return true; }
        return false;
    }

    /// <summary>How many of this item would fit right now.</summary>
    public int Room(string itemId)
    {
        var def = _data.Item(itemId);
        if (def == null) return 0;
        int max = Math.Max(1, def.maxStack), room = 0;
        foreach (var s in Bag)
        {
            if (s == null) room += max;
            else if (s.ItemId == itemId && s.IsPlain) room += Math.Max(0, max - s.Count);
        }
        return room;
    }

    /// <summary>Removes plain stacks from the bag only if there are enough. Equipped items are never consumed.</summary>
    public bool Remove(string itemId, int count)
    {
        int have = 0;
        foreach (var s in Bag) if (s != null && s.ItemId == itemId && s.IsPlain) have += s.Count;
        if (have < count) return false;
        for (int i = Bag.Length - 1; i >= 0 && count > 0; i--)
        {
            var s = Bag[i];
            if (s == null || s.ItemId != itemId || !s.IsPlain) continue;
            int take = Math.Min(count, s.Count);
            s.Count -= take;
            count -= take;
            if (s.Count == 0) Bag[i] = null;
        }
        return true;
    }

    /// <summary>One more row of slots (the bag only ever grows).</summary>
    public void Grow(int newSize)
    {
        if (newSize <= Bag.Length) return;
        var b = new ItemStack[newSize];
        Array.Copy(Bag, b, Bag.Length);
        Bag = b;
    }

    /// <summary>Puts one item straight into its equipment slot if that slot is free. Returns false otherwise.</summary>
    public bool TryEquipNew(string itemId)
    {
        var def = _data.Item(itemId);
        int slot = def == null ? -1 : EquipLayout.FirstSlotFor(def.equipSlot);
        if (slot < 0) return false;
        for (int i = slot; i < EquipLayout.Count; i++)
        {
            if (EquipLayout.SlotKeys[i] != def.equipSlot) break;
            if (Equip[i] == null) { Equip[i] = new ItemStack { ItemId = itemId, Count = 1 }; return true; }
        }
        return false;
    }

    /// <summary>Puts the weapon in the weapon slot; whatever was there goes to the bag (and is lost only if the bag is full).</summary>
    public bool EquipReplacing(string itemId)
    {
        var def = _data.Item(itemId);
        if (def == null || def.equipSlot != "weapon") return false;
        var old = Equip[EquipLayout.Weapon];
        Equip[EquipLayout.Weapon] = new ItemStack { ItemId = itemId, Count = 1 };
        if (old != null)
        {
            if (old.IsPlain) Add(old.ItemId, 1); else AddInstance(old);
        }
        return true;
    }

    /// <summary>The equipment slot a piece of gear goes to: the first free one of its kind, else the first of its kind (rings: slot 1).</summary>
    public int SlotFor(ItemDef def)
    {
        int first = EquipLayout.FirstSlotFor(def.equipSlot);
        if (first < 0) return -1;
        for (int i = first; i < EquipLayout.Count && EquipLayout.SlotKeys[i] == def.equipSlot; i++)
            if (Equip[i] == null) return i;
        return first;
    }

    public ItemDef Weapon => Equip[EquipLayout.Weapon] == null ? null : _data.Item(Equip[EquipLayout.Weapon].ItemId);

    public InventorySync ToSync()
    {
        var m = new InventorySync { BagSize = (ushort)Bag.Length };
        foreach (var r in ToRows())
            m.Items.Add(new ItemSlotState { Container = r.Container, Slot = (ushort)r.Slot, ItemId = r.ItemId, Count = r.Count, Grade = r.Grade, Opts = r.Opts });
        return m;
    }
}
