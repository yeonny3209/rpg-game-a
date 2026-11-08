using Game.Server.Logic;
using Game.Shared.Protocol;
using Xunit;

namespace Game.Server.Tests;

/// <summary>Stage 3a: equipment (grades, random options, slots), potions, bag expansion, NPC shops.</summary>
public class Stage3aTests
{
    static TestPlayer Admin(ZoneHarness h, int level = 25)
    {
        var p = h.Join("세윤");
        p.Chat($"/레벨 {level}");
        p.Tick(2);
        return p;
    }

    static int BagSlotOf(TestPlayer p, string itemId) => p.Inv.Items.First(i => i.Container == ItemContainer.Bag && i.ItemId == itemId).Slot;
    static ItemSlotState Worn(TestPlayer p, int slot) => p.Inv.Items.First(i => i.Container == ItemContainer.Equipment && i.Slot == slot);

    // ───────────── grades and options ─────────────

    [Fact]
    public void The_grade_decides_how_many_option_lines_and_they_are_all_different()
    {
        var h = new ZoneHarness(seed: 100);
        var rng = new Random(1);
        for (int grade = 0; grade < h.Data.Shared.itemRules.grades.Length; grade++)
        {
            var g = h.Data.Shared.itemRules.grades[grade];
            for (int k = 0; k < 20; k++)
            {
                var s = ItemFactory.Roll(h.Data.Shared, "armor_t3", grade, rng);
                Assert.Equal(g.optionLines, s.Opts.Count);
                Assert.Equal(s.Opts.Count, s.Opts.Select(o => o.Id).Distinct().Count());
                foreach (var o in s.Opts)
                {
                    var def = h.Data.Shared.itemRules.Option(o.Id);
                    Assert.InRange(o.Value, def.min - 0.0001f, def.max * g.rangeBoost + 0.0001f);
                }
            }
        }
    }

    [Fact]
    public void Item_rows_keep_grade_and_options_through_save_and_load()
    {
        var h = new ZoneHarness(seed: 101);
        var inv = new Inventory(h.Data.Shared, 40);
        var s = ItemFactory.Roll(h.Data.Shared, "ring_t2", 3, new Random(7));
        inv.AddInstance(s);
        var rows = inv.ToRows();
        var back = Inventory.FromRows(h.Data.Shared, 40, rows);
        var b = back.Bag[0];
        Assert.Equal(3, b.Grade);
        Assert.Equal(s.Opts.Count, b.Opts.Count);
        for (int i = 0; i < s.Opts.Count; i++) { Assert.Equal(s.Opts[i].Id, b.Opts[i].Id); Assert.Equal(s.Opts[i].Value, b.Opts[i].Value, 3); }
        Assert.Equal(3, back.ToSync().Items[0].Grade);
    }

    [Fact]
    public void Prices_follow_the_spec_numbers()
    {
        var h = new ZoneHarness(seed: 102);
        var d = h.Data.Shared;
        long P(string id, string grade) => ItemFactory.Price(d, d.Item(id), d.itemRules.GradeIndex(grade));
        Assert.Equal(6000, P("sword_t2", "rare"));       // 레어 무기 (20레벨대) 6,000
        Assert.Equal(120000, P("sword_t3", "epic"));     // 에픽 무기 (30레벨대) 120,000
        Assert.Equal(60000, P("armor_t3", "epic"));      // 에픽 방어구 1부위 60,000
        Assert.Equal(30, P("hp_potion_low", "common"));
        Assert.Equal(400, P("hp_potion_high", "common"));
        Assert.Equal(3, ItemFactory.SellPrice(d, d.Item("hp_potion_low"), 0));   // 10%
    }

    // ───────────── equipping ─────────────

    [Fact]
    public void Gear_is_locked_until_the_first_quest_unlocks_the_equipment_system()
    {
        var h = new ZoneHarness(seed: 103);
        var p = h.Join("세윤");
        p.Chat("/아이템 helmet_t1");
        p.Tick(2);
        p.Equip(BagSlotOf(p, "helmet_t1"));
        Assert.DoesNotContain(p.Inv.Items, i => i.Container == ItemContainer.Equipment && i.ItemId == "helmet_t1");
        Assert.Contains(p.Notices, n => n.Text.Contains("잠겨"));
    }

    [Fact]
    public void Equipping_armor_adds_defense_scaled_by_grade_and_swapping_returns_the_old_piece()
    {
        var h = new ZoneHarness(seed: 104);
        var p = Admin(h);
        int baseDef = p.Stats.Defense;
        p.Chat("/아이템 armor_t2 common");
        p.Tick(2);
        p.Equip(BagSlotOf(p, "armor_t2"));
        Assert.Equal("armor_t2", Worn(p, 2).ItemId);
        Assert.Equal(baseDef + 15, p.Stats.Defense);                 // common x1.0

        p.Chat("/아이템 armor_t2 epic");                              // epic x1.45 = 22 (+ options may add defense%)
        p.Tick(2);
        int epicSlot = p.Inv.Items.First(i => i.Container == ItemContainer.Bag && i.ItemId == "armor_t2" && i.Grade == 2).Slot;
        p.Equip(epicSlot);
        Assert.Equal(2, Worn(p, 2).Grade);
        Assert.True(p.Stats.Defense >= baseDef + 22, $"{p.Stats.Defense}");
        // the common one went back to the bag
        Assert.Contains(p.Inv.Items, i => i.Container == ItemContainer.Bag && i.ItemId == "armor_t2" && i.Grade == 0);

        p.Unequip(2);
        Assert.DoesNotContain(p.Inv.Items, i => i.Container == ItemContainer.Equipment && i.Slot == 2);
        Assert.Equal(baseDef, p.Stats.Defense);
    }

    [Fact]
    public void Level_and_weapon_kind_are_checked()
    {
        var h = new ZoneHarness(seed: 105);
        var p = Admin(h, 10);
        p.Chat("/전직 archer");
        p.Chat("/아이템 armor_t3");                                   // Lv30
        p.Chat("/아이템 sword_t2");                                   // Lv20 and a sword
        p.Chat("/아이템 bow_t2");                                     // Lv20: too low
        p.Tick(2);
        p.Equip(BagSlotOf(p, "armor_t3"));
        Assert.Contains(p.Notices, n => n.Text.Contains("Lv.30"));
        p.Chat("/레벨 25");
        p.Tick(2);
        p.Equip(BagSlotOf(p, "sword_t2"));
        Assert.Contains(p.Notices, n => n.Text.Contains("활"));       // an archer cannot wield a sword
        p.Equip(BagSlotOf(p, "bow_t2"));
        Assert.Equal("bow_t2", Worn(p, 0).ItemId);
    }

    [Fact]
    public void Weapon_grade_multiplies_attack_and_options_change_the_stats()
    {
        var h = new ZoneHarness(seed: 106);
        var p = Admin(h, 25);
        p.Chat("/전직 assassin");
        p.Tick(2);
        p.Chat("/아이템 dagger_t2 common");
        p.Tick(2);
        p.Equip(BagSlotOf(p, "dagger_t2"));
        int common = p.Stats.Attack;
        float crit0 = p.Stats.CritChance;

        // a hand-made epic with +10% attack and +3% crit
        var me = p.ServerSide;
        var s = new ItemStack { ItemId = "dagger_t2", Count = 1, Grade = 2, Opts = new() { new ItemOpt("attackPct", 0.10f), new ItemOpt("critChance", 0.03f) } };
        int bagSlot = Array.FindIndex(me.Inv.Bag, x => x == null);
        me.Inv.Bag[bagSlot] = s;
        p.Equip(bagSlot);
        Assert.True(p.Stats.Attack > common, $"{p.Stats.Attack} vs {common}");
        Assert.Equal(crit0 + 0.03f, p.Stats.CritChance, 2);
    }

    // ───────────── potions ─────────────

    [Fact]
    public void Potions_heal_without_a_cooldown_and_are_refused_at_full()
    {
        var h = new ZoneHarness(seed: 107);
        var p = Admin(h, 10);
        p.Chat("/아이템 hp_potion_low common 5");
        p.Chat("/아이템 mp_potion_low common 2");
        p.Tick(2);
        int slot = BagSlotOf(p, "hp_potion_low");
        p.UseItem(slot);
        Assert.Contains(p.Notices, n => n.Text.Contains("가득"));            // full HP
        p.ServerSide.Rec.Hp = 10;
        p.UseItem(slot);
        p.UseItem(slot);                                                     // twice in a row: no cooldown
        Assert.True(p.ServerSide.Rec.Hp > 10 + 80);
        Assert.Equal(3, p.Inv.Items.First(i => i.ItemId == "hp_potion_low").Count);

        p.ServerSide.Mana = 0;
        p.UseItem(BagSlotOf(p, "mp_potion_low"));
        Assert.True(p.ServerSide.Mana >= 49);
    }

    // ───────────── bag ─────────────

    [Fact]
    public void Expanding_the_bag_costs_double_each_time_and_stops_at_120()
    {
        var h = new ZoneHarness(seed: 108);
        var p = Admin(h, 5);
        p.ExpandBag();
        Assert.Equal(40, p.Inv.BagSize);                                     // no gold
        p.Chat("/골드 100000000");
        p.Tick(2);
        long gold = 100000000;
        long cost = 1000;
        for (int size = 50; size <= 120; size += 10)
        {
            p.ExpandBag();
            Assert.Equal(size, p.Inv.BagSize);
            gold -= cost;
            Assert.Equal(gold, p.Char.Gold);
            cost *= 2;
        }
        p.ExpandBag();
        Assert.Equal(120, p.Inv.BagSize);
        Assert.Equal(gold, p.Char.Gold);
    }

    // ───────────── shops ─────────────

    static void OpenShop(ZoneHarness h, TestPlayer p, string npc)
    {
        var n = h.Zone.Npcs.Single(x => x.Def.id == npc);
        Assert.True(p.WalkTo(n.X, n.Z, stop: 2.5f), p.LastWalkFailure);
        p.Talk(n.EntityId);
        p.Choose(n.EntityId, 0);
        Assert.NotNull(p.Shop);
    }

    [Fact]
    public void The_general_store_sells_potions_and_buys_materials_for_ten_percent()
    {
        var h = new ZoneHarness(seed: 109);
        var p = Admin(h, 5);
        p.Chat("/골드 100");
        p.Chat("/아이템 rabbit_liver common 10");
        p.Tick(2);
        OpenShop(h, p, "general_store");
        Assert.Equal(4, p.Shop.Entries.Count);
        int hp = p.Shop.Entries.FindIndex(e => e.ItemId == "hp_potion_low");
        p.Buy(hp, 3);
        Assert.Equal(100 - 90, p.Char.Gold);
        Assert.Equal(3, p.Inv.Items.First(i => i.ItemId == "hp_potion_low").Count);

        p.Buy(hp, 1);                                                        // 10 gold left, price 30: refused
        Assert.Equal(3, p.Inv.Items.First(i => i.ItemId == "hp_potion_low").Count);
        Assert.Equal(10, p.Char.Gold);

        p.Sell(BagSlotOf(p, "rabbit_liver"), 10);                            // 20 x 10% = 2 each
        Assert.Equal(10 + 20, p.Char.Gold);
        Assert.DoesNotContain(p.Inv.Items, i => i.ItemId == "rabbit_liver");
    }

    [Fact]
    public void The_weapon_shop_shows_only_the_jobs_weapons_and_bought_gear_gets_fresh_options()
    {
        var h = new ZoneHarness(seed: 110);
        var p = Admin(h, 25);
        p.Chat("/전직 mage");
        p.Chat("/골드 500000");
        p.Tick(2);
        OpenShop(h, p, "weapon_store");
        Assert.All(p.Shop.Entries, e => Assert.StartsWith("staff_", e.ItemId));
        int epic = p.Shop.Entries.FindIndex(e => e.ItemId == "staff_t3" && e.Grade == 2);
        Assert.True(epic >= 0);
        Assert.Equal(120000, p.Shop.Entries[epic].Price);                    // weapon value 3000 x 40
        p.Buy(epic);
        var bought = p.Inv.Items.First(i => i.ItemId == "staff_t3");
        Assert.Equal(2, bought.Grade);
        Assert.Equal(2, bought.Opts.Split(';', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Equal(500000 - 120000, p.Char.Gold);
    }

    [Fact]
    public void Shopping_needs_you_to_stay_near_the_npc_and_epic_is_the_highest_grade_sold()
    {
        var h = new ZoneHarness(seed: 111);
        var p = Admin(h, 25);
        p.Chat("/골드 500000");
        p.Tick(2);
        OpenShop(h, p, "armor_store");
        Assert.All(p.Shop.Entries, e => Assert.True(e.Grade <= 2));
        int idx = p.Shop.Entries.FindIndex(e => e.ItemId == "helmet_t1");
        p.ServerSide.Move.X += 40;                                           // walked away
        p.Buy(idx);
        Assert.DoesNotContain(p.Inv.Items, i => i.ItemId == "helmet_t1");
        Assert.Equal(500000, p.Char.Gold);
    }

    [Fact]
    public void Forged_shop_indices_do_nothing()
    {
        var h = new ZoneHarness(seed: 112);
        var p = Admin(h, 5);
        p.Chat("/골드 1000");
        p.Tick(2);
        OpenShop(h, p, "general_store");
        p.Buy(999);
        p.Buy(0, 0);
        p.Buy(0, 500);
        Assert.Equal(1000, p.Char.Gold);
    }

    // ───────────── drops ─────────────

    [Fact]
    public void Monster_tables_name_valid_grades_for_gear_drops()
    {
        var h = new ZoneHarness(seed: 113);
        var bear = h.Data.Drops["bear"];
        Assert.Contains(bear.items, i => i.item == "helmet_t1" && i.grades.Count > 0);
        foreach (var t in h.Data.Drops.Values)
            foreach (var i in t.items.Where(i => i.grades.Count > 0))
                Assert.All(i.grades.Keys, g => Assert.True(h.Data.Shared.itemRules.GradeIndex(g) >= 0));
    }
}
