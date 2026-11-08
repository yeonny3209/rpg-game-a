using Game.Server.Data;
using Game.Server.Logic;
using Game.Shared.Data;
using Game.Shared.Protocol;
using Xunit;

namespace Game.Server.Tests;

public class LogicTests
{
    static readonly GameData Data = GameData.Load(GameData.FindRepoRoot());
    static SharedData D => Data.Shared;

    [Fact]
    public void Real_data_tables_load_and_validate()
    {
        Assert.Equal(49, D.exp.toNext.Length);
        Assert.Equal(60, D.exp.ToNext(1));        // 2권 1.3
        Assert.Equal(622060, D.exp.ToNext(49));
        Assert.Equal(0, D.exp.ToNext(50));
        Assert.NotNull(D.Item("wood_club"));
        Assert.Equal(3, D.Monster("rabbit").exp);  // 결정 Q2
        Assert.Contains("\"rabbit\"", Data.SharedJson);
        // Server-only tables stay on the server: drop chances, spawn layout, admin nicknames.
        Assert.DoesNotContain("goldMin", Data.SharedJson);
        Assert.DoesNotContain("dropTables", Data.SharedJson);
        Assert.DoesNotContain("admins", Data.SharedJson);
        Assert.DoesNotContain("rabbit_liver\",\"chance", Data.SharedJson);
    }

    [Fact]
    public void Validator_reports_broken_references()
    {
        var s = Data.Shared;
        var broken = new SharedData
        {
            config = s.config, exp = s.exp, character = s.character, items = s.items, map = s.map, npcs = s.npcs, dialogues = s.dialogues,
            monsters = new[] { new MonsterDef { id = "ghost", name = "유령", maxHp = 5, dropTable = "nope" } },
            quests = new[] { new QuestDef { id = "q", title = "t", objectives = new[] { new QuestObjective { type = "collect", target = "no_item", count = 1 } } } },
        };
        var ex = Assert.Throws<InvalidDataException>(() => GameData.Build(broken, Array.Empty<SpawnDef>(), Array.Empty<DropTableDef>()));
        Assert.Contains("unknown dropTable 'nope'", ex.Message);
        Assert.Contains("'no_item' is not an item", ex.Message);
        Assert.Contains("entry quest 'q_rabbit_liver' unknown", ex.Message);
    }

    [Fact]
    public void Exp_levels_up_and_grants_stat_points()
    {
        var c = Rules.NewCharacter(D, "a", "m");
        Assert.Equal(0, Rules.AddExp(D, c, 59, DateTime.UtcNow));
        Assert.Equal(1, Rules.AddExp(D, c, 1, DateTime.UtcNow));
        Assert.Equal(2, c.Level);
        Assert.Equal(0, c.Exp);
        Assert.Equal(5, c.StatPoints); // 2권 2.1 레벨당 5
        Assert.Equal(2, Rules.AddExp(D, c, 100 + 150 + 7, DateTime.UtcNow));
        Assert.Equal(4, c.Level);
        Assert.Equal(7, c.Exp);
    }

    [Fact]
    public void Fill_to_level_gives_exactly_the_missing_exp()
    {
        var c = Rules.NewCharacter(D, "a", "m");
        Rules.AddExp(D, c, 42, DateTime.UtcNow);
        Assert.Equal(18, Rules.ExpToReach(D.exp, c, 2));
        Rules.AddExp(D, c, 18, DateTime.UtcNow);
        Assert.Equal(2, c.Level);
        Assert.Equal(0, Rules.ExpToReach(D.exp, c, 2));
    }

    [Fact]
    public void Damage_formula_matches_spec_example()
    {
        // 4권 1.1: 50레벨 공격자 기준 방어력 2,400이면 피해 50% 감소.
        var cfg = new CombatConfig { randomMin = 1, randomMax = 1 };
        var rng = new Random(1);
        int dmg = Rules.Damage(cfg, 1000, 50, 2400, 1f, 0f, 1.5f, rng, out bool crit);
        Assert.False(crit);
        Assert.Equal(500, dmg);
        Assert.Equal(1000, Rules.Damage(cfg, 1000, 50, 0, 1f, 0f, 1.5f, rng, out _));
        Assert.Equal(1500, Rules.Damage(cfg, 1000, 50, 0, 1f, 1f, 1.5f, rng, out crit));
        Assert.True(crit);
        Assert.Equal(1, Rules.Damage(cfg, 0, 1, 100, 1f, 0f, 1.5f, rng, out _)); // never 0
    }

    [Fact]
    public void Low_level_monsters_give_ten_percent()
    {
        Assert.Equal(25, Rules.MonsterExp(D.exp, 25, 5, 14));
        Assert.Equal(3, Rules.MonsterExp(D.exp, 25, 5, 15)); // 10 below → 10%
        Assert.Equal(1, Rules.MonsterExp(D.exp, 3, 1, 30));  // at least 1
    }

    [Fact]
    public void Rabbit_gold_stays_one_to_three()
    {
        var rng = new Random(5);
        for (int i = 0; i < 500; i++)
        {
            int g = Rules.MonsterGold(D.config.economy, 1, 3, 1, rng);
            Assert.InRange(g, 1, 3);
        }
    }

    [Fact]
    public void Inventory_stacks_and_reports_overflow()
    {
        var inv = new Inventory(D, 3);
        Assert.Equal(0, inv.Add("rabbit_liver", 998));
        Assert.Equal(0, inv.Add("rabbit_liver", 5)); // 999 + 4
        Assert.Equal(1003, inv.Count("rabbit_liver"));
        Assert.Equal(1, inv.Add("wood_club", 2)); // weapons do not stack: 1 free slot → 1 left over
        Assert.Equal(0, inv.Room("rabbit_foot"));
        Assert.True(inv.Remove("rabbit_liver", 1000));
        Assert.Equal(3, inv.Count("rabbit_liver"));
        Assert.False(inv.Remove("rabbit_liver", 4));
    }

    [Fact]
    public void Club_is_auto_equipped_once_and_raises_attack()
    {
        var c = Rules.NewCharacter(D, "a", "m");
        var inv = new Inventory(D, 40);
        int bare = Rules.Stats(D, c, inv).Attack;
        Assert.True(inv.TryEquipNew("wood_club"));
        Assert.False(inv.TryEquipNew("wood_club")); // slot taken
        Assert.Equal(bare + 5, Rules.Stats(D, c, inv).Attack);
        var rows = inv.ToRows();
        Assert.Contains(rows, r => r.Container == ItemContainer.Equipment && r.ItemId == "wood_club");
        var back = Inventory.FromRows(D, 40, rows);
        Assert.Equal("wood_club", back.Weapon.id);
    }

    [Fact]
    public void Collect_quest_becomes_ready_and_consumes_items()
    {
        var inv = new Inventory(D, 40);
        var log = new QuestLog(D, Array.Empty<QuestRow>());
        Assert.Equal(QuestState.None, log.State("q_rabbit_liver", inv));
        Assert.True(log.Accept("q_rabbit_liver"));
        Assert.False(log.Accept("q_rabbit_liver"));
        inv.Add("rabbit_liver", 9);
        Assert.Equal(QuestState.Active, log.State("q_rabbit_liver", inv));
        Assert.False(log.Complete("q_rabbit_liver", inv));
        inv.Add("rabbit_liver", 3);
        Assert.Equal(QuestState.Ready, log.State("q_rabbit_liver", inv));
        Assert.Equal(10, log.ToSync(inv).Quests[0].Progress[0]); // capped at the goal
        Assert.True(log.Complete("q_rabbit_liver", inv));
        Assert.Equal(2, inv.Count("rabbit_liver"));
        Assert.Equal(QuestState.Completed, log.State("q_rabbit_liver", inv));
    }
}
