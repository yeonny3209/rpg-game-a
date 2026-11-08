using Game.Shared.Data;

namespace Game.Server.Logic;

/// <summary>What the worn equipment adds to a character (spec vol.4 2.4 / 2.7): base stats × the grade's multiplier, plus the rolled options.</summary>
public sealed class GearBonus
{
    public int WeaponAttack;      // the weapon's attack × grade (0 = no weapon / unarmed)
    public int Attack, Defense, Hp;
    readonly Dictionary<string, float> _opts = new();

    public float Opt(string id) => _opts.TryGetValue(id, out var v) ? v : 0f;

    /// <param name="ignoreWeapon">During a job trial the lent weapon replaces the worn one.</param>
    public static GearBonus Sum(SharedData d, Inventory inv, bool ignoreWeapon)
    {
        var g = new GearBonus();
        for (int i = 0; i < inv.Equip.Length; i++)
        {
            var s = inv.Equip[i];
            if (s == null) continue;
            var def = d.Item(s.ItemId);
            if (def == null) continue;
            bool weapon = i == EquipLayout.Weapon;
            if (weapon && ignoreWeapon) continue;
            float mult = d.itemRules.Grade(s.Grade).statMult;
            if (weapon) g.WeaponAttack = (int)MathF.Round(def.weapon.attack * mult);
            g.Defense += (int)MathF.Round(def.defense * mult);
            g.Attack += (int)MathF.Round(def.bonusAttack * mult);
            g.Hp += (int)MathF.Round(def.bonusHp * mult);
            if (s.Opts != null)
                foreach (var o in s.Opts) g._opts[o.Id] = g.Opt(o.Id) + o.Value;
        }
        return g;
    }
}

public static class ItemFactory
{
    /// <summary>A new piece of equipment of the given grade with its random option lines rolled (distinct kinds).</summary>
    public static ItemStack Roll(SharedData d, string itemId, int grade, Random rng)
    {
        var g = d.itemRules.Grade(grade);
        var s = new ItemStack { ItemId = itemId, Count = 1, Grade = (byte)Math.Max(0, grade), Opts = new List<ItemOpt>() };
        var pool = d.itemRules.options.ToList();
        for (int i = 0; i < g.optionLines && pool.Count > 0; i++)
        {
            int k = rng.Next(pool.Count);
            var o = pool[k];
            pool.RemoveAt(k);
            float t = (float)rng.NextDouble();
            float v = (o.min + (o.max - o.min) * t) * g.rangeBoost;
            s.Opts.Add(new ItemOpt(o.id, MathF.Round(v, 4)));
        }
        return s;
    }

    /// <summary>Shop price of one item of that grade (gold).</summary>
    public static long Price(SharedData d, ItemDef def, int grade) => (long)Math.Round(def.value * d.itemRules.Grade(grade).valueMult);

    /// <summary>What an NPC pays for it (10% of the shop price, at least 1 gold when the item has any value).</summary>
    public static long SellPrice(SharedData d, ItemDef def, int grade)
    {
        long p = Price(d, def, grade);
        if (p <= 0) return 0;
        return Math.Max(1, (long)Math.Floor(p * d.itemRules.sellRatio));
    }
}
