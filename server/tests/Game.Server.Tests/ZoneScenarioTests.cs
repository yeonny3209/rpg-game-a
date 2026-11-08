using Game.Server.Db;
using Game.Server.World;
using Game.Shared.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace Game.Server.Tests;

/// <summary>Stage 1 completion criteria, played through the real zone with the real data tables.</summary>
public class ZoneScenarioTests
{
    readonly ITestOutputHelper _out;
    public ZoneScenarioTests(ITestOutputHelper o) { _out = o; }

    static NpcEntity Npc(ZoneHarness h, string id) => h.Zone.Npcs.Single(n => n.Def.id == id);

    static void TalkThroughIntro(ZoneHarness h, TestPlayer p)
    {
        var elder = Npc(h, "elder");
        Assert.True(p.WalkTo(elder.X, elder.Z, stop: 2.5f), p.LastWalkFailure);
        p.Talk(elder.EntityId);
        Assert.Equal("elder_intro", p.LastNode);
        p.Choose(elder.EntityId, 0);
        Assert.Equal("elder_intro2", p.LastNode);
        p.Choose(elder.EntityId, 0); // 몽둥이를 받는다
        Assert.Equal("elder_quest", p.LastNode);
        p.Choose(elder.EntityId, 0); // 맡겨 주세요
        Assert.Equal("", p.LastNode);
    }

    /// <summary>Hunts rabbits until the bag holds `livers` rabbit livers. Returns kills.</summary>
    static int HuntRabbits(ZoneHarness h, TestPlayer p, int livers, ITestOutputHelper log)
    {
        int kills = 0;
        for (int guard = 0; guard < 200 && p.Count("rabbit_liver") < livers; guard++)
        {
            var target = h.Zone.Monsters.Where(m => m.Def.id == "rabbit" && !m.Dead)
                .OrderBy(m => (m.X - p.X) * (m.X - p.X) + (m.Z - p.Z) * (m.Z - p.Z)).FirstOrDefault();
            if (target == null) { p.StandStill(20); continue; }
            uint id = target.EntityId;
            for (int swing = 0; swing < 30 && !target.Dead && h.Zone.Monsters.Contains(target); swing++)
            {
                Assert.True(p.WalkTo(target.X, target.Z, stop: 1.6f), p.LastWalkFailure);
                p.AttackToward(target.X, target.Z);
                p.StandStill(16); // 0.8 s > club interval 0.7 s
            }
            if (target.Dead) kills++;
        }
        log?.WriteLine($"kills {kills}, livers {p.Count("rabbit_liver")}, gold {p.Char.Gold}, exp {p.Char.Exp}, hp {p.Char.Hp}");
        return kills;
    }

    [Fact]
    public void First_quest_flow_levels_to_2_unlocks_and_persists()
    {
        var store = new MemoryCharacterStore();
        var h = new ZoneHarness(store, seed: 3);
        var p = h.Join("세윤");
        Assert.Equal(1, p.Char.Level);
        Assert.Equal(UnlockFlags.None, p.Char.Unlocks);
        int bareAttack = p.Char.Attack;

        TalkThroughIntro(h, p);
        // 결정 Q1: club auto-equipped; equipment window still locked.
        Assert.Contains(p.Inv.Items, i => i.Container == ItemContainer.Equipment && i.ItemId == "wood_club");
        Assert.Equal(bareAttack + 5, p.Char.Attack);
        Assert.Equal(QuestState.Active, p.QuestState("q_rabbit_liver"));

        // Talking again gives no second club.
        var elder = Npc(h, "elder");
        p.Talk(elder.EntityId);
        Assert.Equal("elder_progress", p.LastNode);
        p.Choose(elder.EntityId, 0);
        Assert.Equal(1, p.Count("wood_club"));

        int kills = HuntRabbits(h, p, 10, _out);
        Assert.True(kills >= 10, $"only {kills} kills");
        Assert.Equal(QuestState.Ready, p.QuestState("q_rabbit_liver"));
        Assert.Equal(1, p.Char.Level);            // 3 EXP × ~14 rabbits < 60
        Assert.Equal(kills * 3, p.Char.Exp);
        Assert.InRange(p.Char.Gold, kills, kills * 3);

        // Hand in.
        Assert.True(p.WalkTo(elder.X, elder.Z, stop: 2.5f), p.LastWalkFailure);
        p.Talk(elder.EntityId);
        Assert.Equal("elder_ready", p.LastNode);
        p.Choose(elder.EntityId, 0);
        Assert.Equal("elder_reward", p.LastNode);
        Assert.Equal(2, p.Char.Level);
        Assert.Equal(0, p.Char.Exp);
        Assert.Equal(5, p.Char.StatPoints);
        Assert.Equal(UnlockFlags.Equipment | UnlockFlags.LeaveStartArea, p.Char.Unlocks);
        Assert.Equal(QuestState.Completed, p.QuestState("q_rabbit_liver"));
        Assert.Equal(p.Count("rabbit_liver"), p.Inv.Items.Where(i => i.ItemId == "rabbit_liver").Sum(i => i.Count));
        Assert.Contains(p.Gains, g => g.Kind == GainKind.LevelUp && g.Amount == 2);

        // Log out → log in: everything is still there.
        long gold = p.Char.Gold;
        int feet = p.Count("rabbit_foot"), liversLeft = p.Count("rabbit_liver");
        h.Leave(p.Conn);
        h.FlushDb();
        var again = h.Join("세윤");
        Assert.Equal(2, again.Char.Level);
        Assert.Equal(gold, again.Char.Gold);
        Assert.Equal(feet, again.Count("rabbit_foot"));
        Assert.Equal(liversLeft, again.Count("rabbit_liver"));
        Assert.Equal(1, again.Count("wood_club"));
        Assert.Equal(QuestState.Completed, again.QuestState("q_rabbit_liver"));
        Assert.Equal(UnlockFlags.Equipment | UnlockFlags.LeaveStartArea, again.Char.Unlocks);
        Assert.Contains(store.Audits, a => a.Kind == "quest_completed");
    }

    [Fact]
    public void Start_area_is_locked_until_the_quest()
    {
        var h = new ZoneHarness();
        var p = h.Join("Walker");
        var area = h.Data.Shared.map.startArea;
        // Walk north out of the village toward the edge.
        bool ok = p.WalkTo(area.x, area.z + area.radius + 10);
        Assert.False(ok);
        Assert.Equal(CorrectionReason.AreaLocked, p.Corrections.Last().Reason);
        Assert.True(p.Z <= area.z + area.radius + 0.6f);

        // Unlock (as the quest would) and try again.
        h.Zone.Players.Single().Rec.Unlocks |= UnlockFlags.LeaveStartArea;
        Assert.True(p.WalkTo(area.x, area.z + area.radius + 10), p.LastWalkFailure);
    }

    [Fact]
    public void Wolf_kills_without_penalty_and_respawn_returns_to_village()
    {
        var h = new ZoneHarness(seed: 9);
        var p = h.Join("늑대밥");
        var wolf = h.Zone.Monsters.Single(m => m.Def.id == "wolf");
        // Unlock so the long walk can't trip the start-area edge.
        h.Zone.Players.Single().Rec.Unlocks |= UnlockFlags.LeaveStartArea;
        long goldBefore = p.Char.Gold, expBefore = p.Char.Exp;

        Assert.True(p.WalkTo(wolf.HomeX, wolf.HomeZ, stop: 3f), p.LastWalkFailure);
        for (int i = 0; i < 1200 && p.Deaths.Count == 0; i++) p.StandStill(1);
        Assert.Single(p.Deaths);
        Assert.Equal("회색 늑대", p.Deaths[0].KillerName);
        Assert.Equal(0, p.ServerHp);

        // Too early: ignored. After the delay: back in the village with full HP, nothing lost.
        h.Send(p.Conn, new RespawnRequest());
        p.Tick();
        Assert.DoesNotContain(p.Corrections, c => c.Reason == CorrectionReason.Respawn);
        p.Tick((int)(h.Data.Shared.character.respawnDelaySec / ZoneHarness.Dt) + 1);
        h.Send(p.Conn, new RespawnRequest());
        p.Tick();
        var rc = p.Corrections.Last();
        Assert.Equal(CorrectionReason.Respawn, rc.Reason);
        var rp = h.Data.Shared.map.respawnPoint;
        Assert.Equal(rp.x, rc.X, 1);
        Assert.Equal(rp.z, rc.Z, 1);
        Assert.Equal(p.Char.MaxHp, p.ServerHp);
        Assert.Equal(goldBefore, p.Char.Gold);
        Assert.Equal(expBefore, p.Char.Exp);

        // The wolf gives up and walks home (leash) once its target is gone.
        p.StandStill(200);
        Assert.True(wolf.Target == null);
    }

    [Fact]
    public void Rabbit_is_passive_until_hit_then_fights_back()
    {
        var h = new ZoneHarness(seed: 4);
        var p = h.Join("토끼친구");
        var rabbit = h.Zone.Monsters.First(m => m.Def.id == "rabbit");
        Assert.True(p.WalkTo(rabbit.X, rabbit.Z, stop: 1.5f), p.LastWalkFailure);
        p.StandStill(100);
        Assert.Null(rabbit.Target); // 비선공
        int hp = p.ServerHp;
        Assert.True(p.WalkTo(rabbit.X, rabbit.Z, stop: 1.5f), p.LastWalkFailure);
        p.AttackToward(rabbit.X, rabbit.Z);
        p.Tick();
        Assert.Same(h.Zone.Players.Single(), rabbit.Target);
        p.StandStill(60); // rabbit attacks every 2 s
        Assert.True(p.ServerHp < hp, "rabbit should counterattack");
        Assert.True(hp - p.ServerHp < 20, "but only weakly");
    }

    [Fact]
    public void Last_hit_takes_the_loot_when_two_players_share_a_rabbit()
    {
        var h = new ZoneHarness(seed: 11);
        var a = h.Join("Alpha");
        var b = h.Join("Bravo");
        var rabbit = h.Zone.Monsters.First(m => m.Def.id == "rabbit");
        Assert.True(a.WalkTo(rabbit.X, rabbit.Z, stop: 1.5f), a.LastWalkFailure);
        Assert.True(b.WalkTo(rabbit.X, rabbit.Z, stop: 1.5f), b.LastWalkFailure);
        int guard = 0;
        bool aTurn = true;
        while (!rabbit.Dead && guard++ < 20)
        {
            var who = aTurn ? a : b;
            Assert.True(who.WalkTo(rabbit.X, rabbit.Z, stop: 1.5f), who.LastWalkFailure);
            who.AttackToward(rabbit.X, rabbit.Z);
            a.StandStill(16);
            b.Absorb();
            aTurn = !aTurn;
        }
        Assert.True(rabbit.Dead);
        var killer = aTurn ? b : a; // last swing
        var other = killer == a ? b : a;
        Assert.Contains(killer.Gains, g => g.Kind == GainKind.Gold);
        Assert.DoesNotContain(other.Gains, g => g.Kind == GainKind.Gold);
        Assert.Equal(3, killer.Char.Exp);
        Assert.Equal(0, other.Char.Exp);

        // Respawns after 5 s (1권 6.1).
        int rabbits = h.Zone.Monsters.Count(m => m.Def.id == "rabbit" && !m.Dead);
        a.StandStill((int)(5.2f / ZoneHarness.Dt));
        Assert.Equal(rabbits + 1, h.Zone.Monsters.Count(m => m.Def.id == "rabbit" && !m.Dead));
    }

    [Fact]
    public void Attacks_faster_than_the_weapon_allows_are_ignored()
    {
        var h = new ZoneHarness(seed: 5);
        var p = h.Join("Spammer");
        var rabbit = h.Zone.Monsters.First(m => m.Def.id == "rabbit");
        h.Zone.Players.Single().Rec.Unlocks |= UnlockFlags.LeaveStartArea;
        Assert.True(p.WalkTo(rabbit.X, rabbit.Z, stop: 1.2f), p.LastWalkFailure);
        p.Conn.Clear();
        for (int i = 0; i < 10; i++) p.AttackToward(rabbit.X, rabbit.Z); // 10 swings in one tick
        h.Tick();
        var hits = p.Conn.Take<CombatEvent>().Where(e => e.AttackerId == p.EntityId).ToList();
        Assert.Single(hits);
    }

    [Fact]
    public void Same_name_twice_is_rejected()
    {
        var h = new ZoneHarness();
        h.Join("Twin");
        var c = new FakeConnection();
        h.Send(c, new Hello { ProtocolVersion = ProtocolInfo.Version, Name = "twin", Password = "pw1234", Create = true });
        h.Tick();
        Assert.True(c.Closed);
        Assert.Contains(c.Take<Notice>(), n => n.Kind == NoticeKind.Fatal);
    }

    // ───────────── look (gender) ─────────────

    [Fact]
    public void Gender_is_chosen_once_saved_and_shown_to_others()
    {
        var store = new MemoryCharacterStore();
        var h = new ZoneHarness(store);
        var watcher = h.Join("Watcher");
        var p = h.Join("Chooser");
        Assert.Equal(Gender.Unset, p.Char.Gender);   // the client shows the picker for this
        watcher.Conn.Clear();

        h.Send(p.Conn, new ChooseGender { Value = (Gender)9 }); // nonsense is ignored
        p.Tick();
        Assert.Equal(Gender.Unset, p.Char.Gender);

        h.Send(p.Conn, new ChooseGender { Value = Gender.Female });
        p.Tick();
        Assert.Equal(Gender.Female, p.Char.Gender);

        // Others get the player again with the right look.
        Assert.Contains(watcher.Conn.Take<EntityDespawn>(), d => d.EntityId == p.EntityId);
        var respawn = watcher.Conn.Take<EntitySpawn>().Single(e => e.EntityId == p.EntityId);
        Assert.Equal(Gender.Female, respawn.Gender);

        // Choosing again does nothing.
        h.Send(p.Conn, new ChooseGender { Value = Gender.Male });
        p.Tick();
        Assert.Equal(Gender.Female, p.Char.Gender);

        // Saved: a new login comes back as female and is not asked again.
        h.Leave(p.Conn);
        h.FlushDb();
        var again = h.Join("Chooser");
        Assert.Equal(Gender.Female, again.Char.Gender);
    }
}
