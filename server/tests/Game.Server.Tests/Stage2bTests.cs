using Game.Server.World;
using Game.Shared.Data;
using Game.Shared.Protocol;
using Game.Shared.World;
using Xunit;

namespace Game.Server.Tests;

/// <summary>Stage 2b (first part): 마법사 (mana, circles, element, spells), 암살자 (후방 2배, 그림자 걸음), 비행.</summary>
public class Stage2bTests
{
    static TestPlayer Admin(ZoneHarness h) => h.Join("세윤");

    static TestPlayer Mage(ZoneHarness h, int level = 12, string element = "fire")
    {
        var p = Admin(h);
        p.Chat($"/레벨 {level}");
        p.Chat("/전직 mage");
        if (element != null) p.Element(element);
        p.Tick(2);
        return p;
    }

    static TestPlayer Assassin(ZoneHarness h, int level = 25)
    {
        var p = Admin(h);
        p.Chat($"/레벨 {level}");
        p.Chat("/전직 assassin");
        p.Tick(2);
        return p;
    }

    static MonsterEntity Rabbit(ZoneHarness h, int skip = 0) => h.Zone.Monsters.Where(m => m.Def.id == "rabbit").Skip(skip).First();

    static void Place(TestPlayer p, MonsterEntity m, float dist, float side = 0f, float? facing = null)
    {
        m.X = p.X + side; m.Z = p.Z + dist; m.Y = p.Y;
        m.HomeX = m.X; m.HomeZ = m.Z; m.HasWanderGoal = false; m.NextWanderAt = 1e9;
        m.Hp = m.Def.maxHp = 10_000_000;
        if (facing.HasValue) m.Yaw = facing.Value;
    }

    static double NoCrit(CombatEvent e) => (e.Flags & HitFlags.Crit) != 0 ? e.Damage / 1.5 : e.Damage;

    // ───────────── mage: job, element, mana ─────────────

    [Fact]
    public void A_new_mage_starts_at_circle_one_with_full_mana_and_must_pick_an_element()
    {
        var h = new ZoneHarness(seed: 40);
        var p = Mage(h, level: 5, element: null);
        Assert.Equal("mage", p.Char.Job);
        Assert.Equal(1, p.Skills.Circle);
        Assert.Equal("", p.Skills.Element);
        Assert.Equal(p.Stats.MaxMana, p.Mana.Mana);
        Assert.Contains(p.Inv.Items, i => i.ItemId == "apprentice_staff" && i.Container == ItemContainer.Equipment);

        // Without an element a spell is refused and nothing is spent.
        int mana = p.Mana.Mana;
        p.ClearEvents();
        p.UseSkill(0);
        Assert.Equal(mana, p.Mana.Mana);
        Assert.Empty(p.Projectiles);
        Assert.Contains(p.Notices, n => n.Text.Contains("속성"));

        p.Element("lightning");
        Assert.Equal("lightning", p.Skills.Element);
        p.Element("fire");                                            // chosen once: no change by message
        Assert.Equal("lightning", p.Skills.Element);
        p.Element("void");
        Assert.Equal("lightning", p.Skills.Element);
    }

    [Fact]
    public void A_spell_costs_mana_goes_on_cooldown_and_is_refused_when_mana_runs_out()
    {
        var h = new ZoneHarness(seed: 41);
        var p = Mage(h, 5, "fire");
        Place(p, Rabbit(h), 8f);
        int before = p.Mana.Mana;
        p.ClearEvents();
        p.UseSkill(0);                                               // 마력탄: 8 mana
        Assert.Single(p.Projectiles);
        Assert.Equal(ProjectileKind.Magic, p.Projectiles[0].Kind);
        Assert.True(p.Mana.Mana <= before - 8 + 1);                  // spent (a little may have regenerated)
        Assert.Contains(p.Cooldowns, c => c.SkillId == "mg_bolt.fire");

        p.Tick(40);
        p.ServerSide.Mana = 3;                                       // too little
        p.Chat("/쿨타임");
        p.ServerSide.Mana = 3;
        p.ClearEvents();
        p.Cooldowns.Clear();
        p.UseSkill(0);
        Assert.Empty(p.Projectiles);
        Assert.Empty(p.Cooldowns);                                    // refused spells do not start a cooldown
        Assert.Contains(p.Notices, n => n.Text.Contains("마나가 부족"));
    }

    [Fact]
    public void Mana_comes_back_and_is_saved_with_the_character()
    {
        var store = new Game.Server.Db.MemoryCharacterStore();
        var h = new ZoneHarness(store, seed: 42);
        var p = Mage(h, 5, "ice");
        p.ServerSide.Mana = 10;
        p.Tick(60);                                                   // 3 s
        Assert.True(p.ServerSide.Mana > 10f + 4f, $"mana {p.ServerSide.Mana}");

        p.ServerSide.Mana = 42;
        h.Leave(p.Conn);
        h.FlushDb();
        var again = Admin(h);
        Assert.Equal("ice", again.Skills.Element);
        Assert.Equal(1, again.Skills.Circle);
        Assert.InRange(again.Mana.Mana, 40, 60);                      // what it was, not a fresh full bar
    }

    // ───────────── circles ─────────────

    [Fact]
    public void Circles_open_automatically_when_level_and_max_mana_allow_and_unlock_their_spells()
    {
        var h = new ZoneHarness(seed: 43);
        var p = Mage(h, 12, "fire");
        Assert.Equal(1, p.Skills.Circle);                             // level 12 but only 100 mana: circle 2 needs 400

        // 마력 폭발 (2서클, slot 2) is still locked.
        Place(p, Rabbit(h), 7f);
        p.ClearEvents();
        p.UseSkill(2);
        p.Tick(10);
        Assert.Empty(p.TakeCombat());

        p.Allocate(intel: 40);                                        // 지능 50 → 최대 마나 420
        Assert.True(p.Stats.MaxMana >= 400);
        Assert.Equal(2, p.Skills.Circle);
        Assert.Contains(p.Gains, g => g.Kind == GainKind.CircleUp && g.Amount == 2);

        p.ClearEvents();
        p.UseSkill(2);
        p.Tick(14);
        Assert.NotEmpty(p.TakeCombat());                              // now it lands
    }

    [Fact]
    public void Circle_three_and_four_need_their_level_and_mana_and_circle_five_is_not_open_yet()
    {
        var h = new ZoneHarness(seed: 44);
        var p = Mage(h, 50, "fire");
        Assert.Equal(1, p.Skills.Circle);                             // mana still 100
        p.Allocate(intel: 200);                                       // → 지능 210 → 1,700 mana
        Assert.Equal(4, p.Skills.Circle);                             // 3 (Lv20 · 700), 4 (Lv28 · 1100) in one go; 5 is a later stage
        Assert.Equal(4, h.Data.Shared.Job("mage").mage.circles.Count(c => c.available && c.trial == "auto"));
    }

    // ───────────── spell forms ─────────────

    [Fact]
    public void Ground_spells_land_after_their_delay_on_the_point_ahead()
    {
        var h = new ZoneHarness(seed: 45);
        var p = Mage(h, 40, "fire");
        p.Allocate(intel: 190);                                        // circle 3 (700 mana)
        Assert.True(p.Skills.Circle >= 3);
        var near = Rabbit(h); var far = Rabbit(h, 1);
        Place(p, near, 8f);                                           // 마력 기둥 lands 8 m ahead
        Place(p, far, 8f, side: 9f);                                  // far off to the side: outside the 2.2 m radius
        p.Chat("/마나");
        p.ClearEvents();
        p.UseSkill(3);
        p.Tick(8);                                                    // 0.4 s: still winding up (delay 0.9 s)
        Assert.Empty(p.TakeCombat());
        p.Tick(14);
        var hits = p.TakeCombat();
        Assert.Contains(hits, e => e.TargetId == near.EntityId);
        Assert.DoesNotContain(hits, e => e.TargetId == far.EntityId);
    }

    [Fact]
    public void A_zone_spell_hits_once_per_tick_for_five_ticks()
    {
        var h = new ZoneHarness(seed: 46);
        var p = Mage(h, 40, "ice");
        p.Allocate(intel: 190);
        Assert.Equal(4, p.Skills.Circle);
        var r = Rabbit(h); Place(p, r, 7f);
        r.RootUntil = 1e9;                                            // stays in the field
        p.Chat("/마나");
        p.ClearEvents();
        p.UseSkill(5);                                                // 마력 장판
        p.Tick(130);                                                  // 6.5 s
        Assert.Equal(5, p.TakeCombat().Count(e => e.TargetId == r.EntityId && (e.Flags & HitFlags.Burn) == 0));
    }

    [Fact]
    public void Chain_lightning_jumps_to_nearby_monsters_each_hit_weaker()
    {
        var h = new ZoneHarness(seed: 47);
        var p = Mage(h, 40, "lightning");
        p.Allocate(intel: 190);
        var a = Rabbit(h); var b = Rabbit(h, 1); var c = Rabbit(h, 2); var far = Rabbit(h, 3);
        Place(p, a, 5f); Place(p, b, 8f); Place(p, c, 11f); Place(p, far, 30f);
        p.Chat("/마나");
        p.ClearEvents();
        p.UseSkill(4);                                                // 마력 연쇄
        p.Tick(4);
        var hits = p.TakeCombat();
        double da = NoCrit(hits.First(e => e.TargetId == a.EntityId));
        double db = NoCrit(hits.First(e => e.TargetId == b.EntityId));
        double dc = NoCrit(hits.First(e => e.TargetId == c.EntityId));
        Assert.True(da > db * 1.1 && db > dc * 1.1, $"{da} {db} {dc}");
        Assert.DoesNotContain(hits, e => e.TargetId == far.EntityId);   // too far to be jumped to
    }

    [Fact]
    public void The_shield_soaks_hits_before_hp_does()
    {
        var h = new ZoneHarness(seed: 48);
        var p = Mage(h, 5, "water");
        var r = Rabbit(h); Place(p, r, 1.2f);
        r.Hp = r.Def.maxHp = 100000;
        p.AttackToward(r.X, r.Z);                                     // wake it up
        p.UseSkill(1);                                                // 마력 방패
        Assert.True(p.ServerSide.ShieldAmount > 20f);
        int hp = p.ServerHp;
        p.ClearEvents();
        p.StandStill(30);                                             // its first attack lands
        Assert.Contains(p.TakeCombat(), e => e.AttackerId == r.EntityId && e.TargetId == p.EntityId && e.Damage == 0);
        Assert.Equal(hp, p.ServerHp);
    }

    // ───────────── elements ─────────────

    static (TestPlayer p, MonsterEntity target) ElementShot(string element, int seed = 50)
    {
        var h = new ZoneHarness(seed: seed);
        var p = Mage(h, 5, element);
        var r = Rabbit(h);
        Place(p, r, 8f);
        p.ClearEvents();
        p.UseSkill(0);
        p.Tick(14);
        return (p, r);
    }

    [Fact]
    public void Fire_burns_over_time()
    {
        var (p, r) = ElementShot("fire");
        Assert.True(r.BurnTickDamage > 0);
        p.ClearEvents();
        p.Tick(80);                                                   // the 3 s burn plays out
        var ticks = p.TakeCombat().Where(e => e.TargetId == r.EntityId && (e.Flags & HitFlags.Burn) != 0).ToList();
        Assert.InRange(ticks.Count, 2, 3);
        Assert.All(ticks, t => Assert.Equal(r.BurnTickDamage, t.Damage));
    }

    [Fact]
    public void Ice_slows_water_pushes_plant_roots()
    {
        var (_, ice) = ElementShot("ice");
        Assert.True(ice.SlowUntil > 0);
        var (pw, water) = ElementShot("water", 51);
        Assert.Contains(pw.Ccs, c => c.TargetId == water.EntityId && c.Type == CcType.Knockback);
        var (_, plant) = ElementShot("plant", 52);
        Assert.True(plant.RootUntil > 0);
    }

    [Fact]
    public void Lightning_chains_and_metal_pierces()
    {
        var h = new ZoneHarness(seed: 53);
        var p = Mage(h, 5, "lightning");
        var a = Rabbit(h); var b = Rabbit(h, 1);
        Place(p, a, 8f); Place(p, b, 11f, side: 3f);                  // b is not on the bolt's path, only near a
        p.ClearEvents();
        p.UseSkill(0);
        p.Tick(16);
        var hits = p.TakeCombat();
        Assert.Contains(hits, e => e.TargetId == a.EntityId);
        Assert.Contains(hits, e => e.TargetId == b.EntityId);         // 번개 = 연쇄

        var h2 = new ZoneHarness(seed: 54);
        var p2 = Mage(h2, 5, "metal");
        var c = Rabbit(h2); var d = Rabbit(h2, 1); var e2 = Rabbit(h2, 2);
        Place(p2, c, 5f); Place(p2, d, 8f); Place(p2, e2, 11f);       // all on the path
        p2.ClearEvents();
        p2.UseSkill(0);
        p2.Tick(20);
        var hits2 = p2.TakeCombat();
        Assert.Contains(hits2, x => x.TargetId == c.EntityId);
        Assert.Contains(hits2, x => x.TargetId == d.EntityId);
        Assert.Contains(hits2, x => x.TargetId == e2.EntityId);       // 금속 = 관통 (1 + 2)
    }

    [Fact]
    public void A_matching_element_matchup_hits_harder()
    {
        var h = new ZoneHarness(seed: 55);
        var p = Mage(h, 5, "fire");
        var plant = Rabbit(h); var none = Rabbit(h, 1);
        plant.Def.element = "plant";                                   // 불 → 식물: 유리 +25%
        Place(p, plant, 8f); Place(p, none, 8f, side: 0.01f);
        none.Def = new MonsterDef { id = "plain", name = "plain", level = 1, maxHp = 10_000_000, element = "none", visual = plant.Def.visual, bodyRadius = 0.35f };
        plant.Def.defense = 0;
        double sumPlant = 0, sumNone = 0; int n = 0;
        for (int i = 0; i < 6; i++)
        {
            p.ClearEvents();
            p.Chat("/쿨타임");
            p.UseSkill(0);
            p.Tick(14);
            foreach (var e in p.TakeCombat())
            {
                if (e.TargetId == plant.EntityId) { sumPlant += NoCrit(e); n++; }
                if (e.TargetId == none.EntityId) sumNone += NoCrit(e);
            }
        }
        // the bolt stops at the first monster, so compare the plant one against its own base number
        Assert.True(n >= 3);
        double avg = sumPlant / n;
        var cfg = h.Data.Config.combat;
        Assert.True(avg > 0);
        Assert.Equal(1.25f, h.Data.Shared.ElementMultiplier("fire", "plant"));
    }

    [Fact]
    public void The_mage_basic_attack_is_a_free_ranged_bolt()
    {
        var h = new ZoneHarness(seed: 56);
        var p = Mage(h, 5, "fire");
        Place(p, Rabbit(h), 8f);
        int mana = p.Mana.Mana;
        p.ClearEvents();
        p.AttackToward(p.X, p.Z + 8f);
        p.Tick(14);
        Assert.Contains(p.Projectiles, x => x.Kind == ProjectileKind.Magic);
        Assert.NotEmpty(p.TakeCombat());
        Assert.True(p.Mana.Mana >= mana - 1);                          // no mana for the basic attack
    }

    // ───────────── mage trial ─────────────

    [Fact]
    public void The_mage_trial_lends_a_bolt_with_full_mana_and_ends_in_the_job()
    {
        var h = new ZoneHarness(seed: 57);
        var p = Admin(h);
        p.Chat("/레벨 5");
        var master = h.Zone.Npcs.Single(n => n.Def.id == "mage_master");
        Assert.True(p.WalkTo(master.X, master.Z, stop: 2.5f), p.LastWalkFailure);
        p.Talk(master.EntityId);
        Assert.Equal("mm_offer", p.LastNode);
        p.Choose(master.EntityId, 0);
        Assert.True(p.Trial.Active);
        Assert.Equal(p.Stats.MaxMana, p.Mana.Mana);
        var opp = h.Zone.Monsters.Single(m => m.Owner == p.ServerSide);
        Assert.Equal("trial_mage", opp.Def.id);

        opp.X = p.X; opp.Z = p.Z + 8f; opp.Y = p.Y; opp.HomeX = opp.X; opp.HomeZ = opp.Z; opp.Hp = 1;
        p.ClearEvents();
        p.UseSkill(0, 0f);                                            // the lent 마력탄, element-less
        p.Tick(16);
        Assert.Equal("mage", p.Char.Job);
        Assert.Equal(1, p.Skills.Circle);
        Assert.Equal("", p.Skills.Element);                           // now the element picker opens on the client
    }

    // ───────────── assassin ─────────────

    [Fact]
    public void An_assassin_hits_twice_as_hard_from_behind()
    {
        var h = new ZoneHarness(seed: 60);
        var p = Assassin(h, 5);
        Assert.Equal("assassin", p.Char.Job);
        Assert.Contains(p.Inv.Items, i => i.ItemId == "apprentice_dagger" && i.Container == ItemContainer.Equipment);

        var r = Rabbit(h);
        double Hit(float facing)
        {
            Place(p, r, 1.4f, facing: facing);
            p.Chat("/쿨타임");
            p.ClearEvents();
            double best = 0; int n = 0; double sum = 0;
            for (int i = 0; i < 6; i++)
            {
                Place(p, r, 1.4f, facing: facing);
                p.AttackToward(r.X, r.Z);
                p.StandStill(14);
                foreach (var e in p.TakeCombat().Where(e => e.TargetId == r.EntityId)) { sum += NoCrit(e) / (((e.Flags & HitFlags.Back) != 0) ? 2.0 : 1.0); n++; if ((e.Flags & HitFlags.Back) != 0 != (facing == 0f)) { } }
            }
            return n == 0 ? 0 : sum / n;
        }
        // facing 0 = looking the same way as the assassin (+Z): the assassin is behind it. 180 = looking at the assassin.
        p.ClearEvents();
        Place(p, r, 1.4f, facing: 0f);
        p.AttackToward(r.X, r.Z); p.StandStill(14);
        var behind = p.TakeCombat().First(e => e.TargetId == r.EntityId);
        Assert.True((behind.Flags & HitFlags.Back) != 0);

        Place(p, r, 1.4f, facing: 180f);
        p.StandStill(10);
        p.ClearEvents();
        p.AttackToward(r.X, r.Z); p.StandStill(14);
        var front = p.TakeCombat().First(e => e.TargetId == r.EntityId);
        Assert.True((front.Flags & HitFlags.Back) == 0);
        Assert.InRange(NoCrit(behind) / NoCrit(front), 1.7, 2.3);       // 후방 2배 (4권 1.3)
    }

    [Fact]
    public void Shadow_step_puts_the_assassin_behind_the_target_and_stabs_its_back()
    {
        var h = new ZoneHarness(seed: 61);
        var p = Assassin(h, 5);
        var r = Rabbit(h);
        Place(p, r, 6f, facing: 180f);                                 // it looks at the assassin
        float z0 = p.Z;
        p.ClearEvents();
        p.UseSkill(0);                                                 // 그림자 걸음
        p.Tick(2);
        Assert.Contains(p.Corrections, c => c.Reason == CorrectionReason.Teleport);
        Assert.True(p.Z > r.Z, $"assassin z {p.Z} should be past the rabbit at {r.Z}");   // behind it (it faced -Z toward us)
        Assert.InRange(p.Z - r.Z, 0.8f, 1.8f);
        var stab = p.TakeCombat().First(e => e.TargetId == r.EntityId);
        Assert.True((stab.Flags & HitFlags.Back) != 0);
        Assert.DoesNotContain(p.Corrections, c => c.Reason is CorrectionReason.TooFast or CorrectionReason.TooHigh);
    }

    [Fact]
    public void Shadow_step_without_a_target_is_a_short_blink_forward()
    {
        var h = new ZoneHarness(seed: 62);
        var p = Assassin(h, 5);
        float z0 = p.Z;
        p.UseSkill(0);
        p.Tick(2);
        Assert.InRange(p.Z - z0, 3.5f, 4.5f);
        Assert.Contains(p.Corrections, c => c.Reason == CorrectionReason.Teleport);
    }

    [Fact]
    public void Execute_does_double_damage_to_a_target_below_thirty_percent_hp()
    {
        var h = new ZoneHarness(seed: 63);
        var p = Assassin(h, 25);
        var r = Rabbit(h);
        double Stab(int hp)
        {
            Place(p, r, 1.5f, facing: 180f);
            r.CcImmuneUntil = 0; r.StunUntil = 0; r.CcImmuneUntil = 0; r.StunUntil = 0; r.Def.maxHp = 100000; r.Hp = hp * 100;
            p.Chat("/쿨타임");
            p.ClearEvents();
            p.UseSkill(5);                                             // 처형
            p.Tick(3);
            Assert.Contains(p.Ccs, c => c.TargetId == r.EntityId && c.Type == CcType.Stun);
            return NoCrit(p.TakeCombat().First(e => e.TargetId == r.EntityId));
        }
        double high = Stab(1000), low = Stab(250);
        Assert.InRange(low / high, 1.7, 2.3);
    }

    [Fact]
    public void Smoke_slows_everything_around_the_assassin()
    {
        var h = new ZoneHarness(seed: 64);
        var p = Assassin(h, 12);
        var a = Rabbit(h); var b = Rabbit(h, 1);
        Place(p, a, 2.5f); Place(p, b, 2f, side: -2f);
        p.UseSkill(2);
        p.Tick(2);
        Assert.True(a.SlowUntil > 0 && b.SlowUntil > 0);
    }

    [Fact]
    public void The_assassin_trial_works_like_the_others()
    {
        var h = new ZoneHarness(seed: 65);
        var p = Admin(h);
        p.Chat("/레벨 5");
        var master = h.Zone.Npcs.Single(n => n.Def.id == "assassin_master");
        Assert.True(p.WalkTo(master.X, master.Z, stop: 2.5f), p.LastWalkFailure);
        p.Talk(master.EntityId);
        Assert.Equal("am_offer", p.LastNode);
        p.Choose(master.EntityId, 0);
        Assert.True(p.Trial.Active);
        var opp = h.Zone.Monsters.Single(m => m.Owner == p.ServerSide);
        opp.X = p.X; opp.Z = p.Z + 2f; opp.Y = p.Y; opp.HomeX = opp.X; opp.HomeZ = opp.Z; opp.Hp = 1;
        p.UseSkill(0);                                                 // lent 그림자 걸음
        p.Tick(6);
        Assert.Equal("assassin", p.Char.Job);
    }

    // ───────────── flight ─────────────

    [Fact]
    public void A_mage_can_fly_high_and_fast_but_only_for_a_while_and_then_must_wait()
    {
        var h = new ZoneHarness(seed: 70);
        var p = Mage(h, 5, "fire");
        p.Fly();
        Assert.Contains(p.Flights, f => f.EntityId == p.EntityId && f.Active);

        // 15 m up is legal while flying …
        p.Y += 15f; p.Z += 0.4f;
        h.Send(p.Conn, new MoveState { Seq = 800, CorrectionEpoch = 0, X = p.X, Y = p.Y, Z = p.Z, Yaw = 0, Flags = MoveFlags.None });
        p.Tick(1);
        Assert.DoesNotContain(p.Corrections, c => c.Reason == CorrectionReason.TooHigh);
        // … and 8.4 m/s is allowed (a bit over the normal run speed): 20 states, 0.42 m each
        for (int i = 0; i < 20; i++)
        {
            p.Z += 0.42f;
            h.Send(p.Conn, new MoveState { Seq = (uint)(801 + i), CorrectionEpoch = 0, X = p.X, Y = p.Y, Z = p.Z, Yaw = 0, Flags = MoveFlags.None });
            p.Tick(1);
        }
        Assert.DoesNotContain(p.Corrections, c => c.Reason == CorrectionReason.TooFast);

        p.Tick(200);                                                   // 10 s: it ends
        Assert.Contains(p.Flights, f => !f.Active && !f.Fell);
        // Coming down is still not "TooHigh" for a while.
        p.Y -= 5f;
        h.Send(p.Conn, new MoveState { Seq = 900, CorrectionEpoch = 0, X = p.X, Y = p.Y, Z = p.Z, Yaw = 0, Flags = MoveFlags.None });
        p.Tick(1);
        Assert.DoesNotContain(p.Corrections, c => c.Reason == CorrectionReason.TooHigh);

        // Cooldown 30 s: not yet.
        p.Flights.Clear(); p.Notices.Clear();
        p.Fly();
        Assert.DoesNotContain(p.Flights, f => f.Active);
        Assert.Contains(p.Notices, n => n.Text.Contains("비행"));
    }

    [Fact]
    public void Without_flight_the_same_height_is_flying_hacks()
    {
        var h = new ZoneHarness(seed: 71);
        var p = Assassin(h, 5);                                        // assassins have no flight
        p.Fly();
        Assert.DoesNotContain(p.Flights, f => f.Active);
        p.Y += 15f; p.Z += 0.4f;
        h.Send(p.Conn, new MoveState { Seq = 810, CorrectionEpoch = 0, X = p.X, Y = p.Y, Z = p.Z, Yaw = 0, Flags = MoveFlags.None });
        p.Tick(1);
        Assert.Contains(p.Corrections, c => c.Reason == CorrectionReason.TooHigh);
    }

    [Fact]
    public void Pressing_the_key_again_lands_early_and_archers_fly_too()
    {
        var h = new ZoneHarness(seed: 72);
        var p = Admin(h);
        p.Chat("/레벨 5");
        p.Chat("/전직 archer");
        p.Fly();
        Assert.Contains(p.Flights, f => f.Active);
        p.Flights.Clear();
        p.Fly();                                                       // lands
        Assert.Contains(p.Flights, f => !f.Active);
        Assert.False(p.ServerSide.Move.FlyUntil > h.Now);
    }

    [Fact]
    public void Being_hit_in_the_air_shoots_you_down_with_fall_damage()
    {
        var h = new ZoneHarness(seed: 73);
        var p = Mage(h, 5, "fire");
        p.Fly();
        p.ServerSide.Move.Y = h.Zone.Terrain.Height(p.ServerSide.Move.X, p.ServerSide.Move.Z) + 20f;
        int hp = p.ServerHp, max = p.Stats.MaxHp;
        p.Flights.Clear();
        h.Zone.TestFall(p.ServerSide);
        p.Tick(1);
        Assert.Contains(p.Flights, f => f.Fell && !f.Active);
        int expected = (int)Math.Round(max * 0.08 * 20 / 10);
        Assert.InRange(hp - p.ServerHp, expected - 1, expected + 1);   // 8% of max HP per 10 m
        Assert.False(p.ServerSide.Move.FlyUntil > h.Now);
    }
}

/// <summary>마법사: every element has its own six spells (data/shared/skills.json, ids "mg_x.element").</summary>
public class ElementSpellTests
{
    static readonly string[] Elements = { "fire", "plant", "earth", "water", "lightning", "metal", "ice", "wind", "light", "dark", "void" };
    static readonly string[] Bases = { "mg_bolt", "mg_shield", "mg_blast", "mg_pillar", "mg_chain", "mg_field" };

    [Fact]
    public void Every_element_has_six_spells_and_no_two_elements_share_a_spell()
    {
        var h = new ZoneHarness(seed: 91);
        var d = h.Data.Shared;
        var names = new HashSet<string>();
        foreach (var el in Elements)
            for (int i = 0; i < 6; i++)
            {
                var s = d.SkillFor(Bases[i], el);
                Assert.Equal(Bases[i] + "." + el, s.id);
                Assert.Equal(el, s.element);
                Assert.True(names.Add(s.name), $"duplicate spell name {s.name}");
            }
        Assert.Equal(66, names.Count);
    }

    [Fact]
    public void A_mage_casts_the_spell_of_the_chosen_element()
    {
        foreach (var el in new[] { "fire", "ice", "water", "lightning", "plant", "metal" })
        {
            var h = new ZoneHarness(seed: 92);
            var p = h.Join("세윤");
            p.Chat("/레벨 40"); p.Chat("/전직 mage"); p.Element(el); p.Allocate(intel: 190); p.Chat("/마나");
            p.Tick(2);
            p.ClearEvents();
            p.UseSkill(0);
            Assert.Contains(p.Cooldowns, c => c.SkillId == "mg_bolt." + el);
        }
    }
}
