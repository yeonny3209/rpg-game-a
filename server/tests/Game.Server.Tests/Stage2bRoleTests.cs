using Game.Server.World;
using Game.Shared.Protocol;
using Xunit;

namespace Game.Server.Tests;

/// <summary>Stage 2b part 2: 서양 검사 (마나 주입), 전사 (도발 · 철벽 · CC), 버퍼 (신성력 · 파티 버프 · 치유 · 부활).</summary>
public class Stage2bRoleTests
{
    static TestPlayer Admin(ZoneHarness h) => h.Join("세윤");

    static TestPlayer As(ZoneHarness h, string job, int level = 12)
    {
        var p = Admin(h);
        p.Chat($"/레벨 {level}");
        p.Chat($"/전직 {job}");
        p.Tick(2);
        return p;
    }

    static MonsterEntity Rabbit(ZoneHarness h, int skip = 0) => h.Zone.Monsters.Where(m => m.Def.id == "rabbit").Skip(skip).First();

    static void Place(TestPlayer p, MonsterEntity m, float dist, float side = 0f)
    {
        m.X = p.X + side; m.Z = p.Z + dist; m.Y = p.Y;
        m.HomeX = m.X; m.HomeZ = m.Z; m.HasWanderGoal = false; m.NextWanderAt = 1e9;
        m.Hp = m.Def.maxHp = 10_000_000;
    }

    static double NoCrit(CombatEvent e) => (e.Flags & HitFlags.Crit) != 0 ? e.Damage / 1.5 : e.Damage;

    // ───────────── trials of the three new masters ─────────────

    [Theory]
    [InlineData("western_master", "wm_offer", "trial_western", "western_swordsman", 6f)]
    [InlineData("warrior_master", "wr_offer", "trial_warrior", "warrior", 2f)]
    [InlineData("buffer_master", "bf_offer", "trial_buffer", "buffer", 6f)]
    public void Each_new_master_runs_a_trial_that_ends_in_the_job(string npc, string offer, string opponent, string job, float dist)
    {
        var h = new ZoneHarness(seed: 80);
        var p = Admin(h);
        p.Chat("/레벨 5");
        var master = h.Zone.Npcs.Single(n => n.Def.id == npc);
        Assert.True(p.WalkTo(master.X, master.Z, stop: 2.5f), p.LastWalkFailure);
        p.Talk(master.EntityId);
        Assert.Equal(offer, p.LastNode);
        p.Choose(master.EntityId, 0);
        Assert.True(p.Trial.Active);
        var opp = h.Zone.Monsters.Single(m => m.Owner == p.ServerSide);
        Assert.Equal(opponent, opp.Def.id);
        opp.X = p.X; opp.Z = p.Z + dist; opp.Y = p.Y; opp.HomeX = opp.X; opp.HomeZ = opp.Z; opp.Hp = 1;
        p.UseSkill(0, 0f);
        p.Tick(24);
        Assert.Equal(job, p.Char.Job);
        Assert.False(p.Trial.Active);
    }

    // ───────────── 서양 검사 ─────────────

    [Fact]
    public void Mana_infusion_costs_one_percent_per_swing_and_multiplies_damage()
    {
        var h = new ZoneHarness(seed: 81);
        var p = As(h, "western_swordsman", 40);
        Assert.Contains(p.Inv.Items, i => i.ItemId == "apprentice_blade" && i.Container == ItemContainer.Equipment);
        p.Allocate(intel: 190);                                   // → ~1,600 max mana
        p.Chat("/마나");
        int max = p.Stats.MaxMana;
        Assert.True(max > 1000);
        var r = Rabbit(h); Place(p, r, 1.5f);

        double Swing(int swings)
        {
            double sum = 0; int n = 0;
            for (int i = 0; i < swings; i++)
            {
                p.ClearEvents();
                p.AttackToward(r.X, r.Z);
                p.StandStill(24);                                  // past the weapon interval
                foreach (var e in p.TakeCombat().Where(e => e.TargetId == r.EntityId)) { sum += NoCrit(e); n++; }
            }
            return n == 0 ? 0 : sum / n;
        }

        double plain = Swing(6);
        p.Chat("/마나");
        p.UseSkill(1);                                            // 마나 주입 ON
        Assert.True(p.Mana.Infused);
        p.ServerSide.Mana = max;
        p.ClearEvents();
        p.AttackToward(r.X, r.Z);
        p.Tick(3);
        Assert.InRange(p.Mana.Mana, (int)(max * 0.97), (int)(max * 0.995));   // one swing ≈ 1% (a little regenerates)
        double infused = Swing(6);
        Assert.InRange(infused / plain, 1f + max / 2000f - 0.3f, 1f + max / 2000f + 0.3f);

        p.Tick(24);                                               // wait out the 1 s cooldown, then OFF
        p.UseSkill(1);
        Assert.False(p.Mana.Infused);
    }

    [Fact]
    public void Infusion_turns_itself_off_when_the_mana_runs_out()
    {
        var h = new ZoneHarness(seed: 82);
        var p = As(h, "western_swordsman", 12);
        p.UseSkill(1);
        Assert.True(p.Mana.Infused);
        var r = Rabbit(h); Place(p, r, 1.5f);
        p.ServerSide.Mana = 0;
        p.AttackToward(r.X, r.Z);
        p.StandStill(24);
        Assert.False(p.Mana.Infused);
    }

    [Fact]
    public void Western_skills_spend_mana_and_are_refused_without_it()
    {
        var h = new ZoneHarness(seed: 83);
        var p = As(h, "western_swordsman", 12);
        var r = Rabbit(h); Place(p, r, 6f);
        int before = p.Mana.Mana;
        p.ClearEvents();
        p.UseSkill(0, 0f);                                        // 마나 검기, 12 mana
        Assert.Single(p.Projectiles);
        Assert.True(p.Mana.Mana <= before - 11);
        p.Chat("/쿨타임");
        p.ServerSide.Mana = 2;
        p.ClearEvents();
        p.UseSkill(0, 0f);
        Assert.Empty(p.Projectiles);
    }

    // ───────────── 전사 ─────────────

    [Fact]
    public void Taunt_pulls_monsters_and_keeps_them_even_when_someone_else_attacks()
    {
        var h = new ZoneHarness(seed: 84);
        var w = As(h, "warrior", 12);
        var other = h.Join("Bystander");
        var r = Rabbit(h); Place(w, r, 6f);
        r.Target = other.ServerSide; r.Ai = MonsterAi.Chase;
        w.UseSkill(1, 0f);                                        // 도발
        Assert.Same(w.ServerSide, r.Target);
        Assert.True(r.TauntUntil > 0);

        // The other player hits it: the target must stay.
        other.X = r.X; other.Z = r.Z - 1.2f; other.Y = r.Y;
        h.Zone.Players.Single(x => x.Name == "Bystander").Move.X = other.X;
        h.Zone.Players.Single(x => x.Name == "Bystander").Move.Z = other.Z;
        other.AttackToward(r.X, r.Z);
        other.Tick(10);
        Assert.Same(w.ServerSide, r.Target);

        // After the taunt ends the next attacker takes the aggro again.
        w.Tick(120);
        r.X = other.X; r.Z = other.Z + 1.2f; r.HomeX = r.X; r.HomeZ = r.Z;
        other.Tick(1);
        other.AttackToward(r.X, r.Z);
        other.Tick(10);
        Assert.Same(other.ServerSide, r.Target);
    }

    [Fact]
    public void Iron_wall_cuts_the_damage_taken()
    {
        var h = new ZoneHarness(seed: 85);
        var w = As(h, "warrior", 12);
        var r = Rabbit(h); Place(w, r, 1.0f);
        w.UseSkill(2);                                            // 철벽 −35%
        Assert.InRange(w.ServerSide.BuffSum("damageTaken", h.Now), 0.34f, 0.36f);
        Assert.Equal("warrior", w.Char.Job);
    }

    [Fact]
    public void Warrior_crowd_control_stuns_roots_and_roars()
    {
        var h = new ZoneHarness(seed: 86);
        var w = As(h, "warrior", 40);
        var a = Rabbit(h); var b = Rabbit(h, 1);
        Place(w, a, 1.8f); Place(w, b, 2f, side: 1.2f);
        w.ClearEvents();
        w.UseSkill(0, 0f);                                        // 방패 치기
        w.Tick(2);
        Assert.Contains(w.Ccs, c => c.TargetId == a.EntityId && c.Type == CcType.Stun);

        a.CcImmuneUntil = b.CcImmuneUntil = 0; a.StunUntil = b.StunUntil = 0;
        w.ClearEvents(); w.Chat("/쿨타임");
        w.UseSkill(3);                                            // 대지 가르기: root all around
        w.Tick(2);
        Assert.True(a.RootUntil > 0 && b.RootUntil > 0);

        a.CcImmuneUntil = b.CcImmuneUntil = 0; a.StunUntil = b.StunUntil = 0;
        w.Chat("/쿨타임"); w.ClearEvents();
        w.UseSkill(5);                                            // 포효: stun everyone near
        w.Tick(2);
        Assert.Contains(w.Ccs, c => c.TargetId == a.EntityId && c.Type == CcType.Stun);
        Assert.Contains(w.Ccs, c => c.TargetId == b.EntityId && c.Type == CcType.Stun);
    }

    // ───────────── 버퍼 ─────────────

    [Fact]
    public void Buffer_basic_attack_and_smite_are_free_ranged_light_bolts()
    {
        var h = new ZoneHarness(seed: 87);
        var p = As(h, "buffer", 12);
        Assert.Contains(p.Inv.Items, i => i.ItemId == "apprentice_relic" && i.Container == ItemContainer.Equipment);
        var r = Rabbit(h); Place(p, r, 8f);
        p.ClearEvents();
        p.UseSkill(0, 0f);
        p.Tick(14);
        Assert.Contains(p.Projectiles, x => x.Kind == ProjectileKind.Magic && x.Tier == 250);
        Assert.NotEmpty(p.TakeCombat());
    }

    [Fact]
    public void Bless_buffs_everyone_near_scaled_by_holy_power_and_skips_the_far()
    {
        var h = new ZoneHarness(seed: 88);
        var b = As(h, "buffer", 12);
        var ally = h.Join("Friend");
        var far = h.Join("Far");
        var zf = h.Zone.Players.Single(x => x.Name == "Far");
        zf.Move.X += 100; zf.Move.Z += 100;
        b.UseSkill(2);                                            // 축복: +15% damage
        var za = h.Zone.Players.Single(x => x.Name == "Friend");
        Assert.InRange(b.ServerSide.BuffSum("damage", h.Now), 0.149f, 0.151f);
        Assert.InRange(za.BuffSum("damage", h.Now), 0.149f, 0.151f);
        Assert.Equal(0f, zf.BuffSum("damage", h.Now));
        ally.Tick(1);
        Assert.Contains(ally.Notices, n => n.Text.Contains("축복"));

        b.Chat("/신성력 100");                                    // ×1.25
        b.Chat("/쿨타임");
        b.UseSkill(2);
        Assert.InRange(za.BuffSum("damage", h.Now), 0.1869f, 0.1881f);
    }

    [Fact]
    public void The_same_buff_from_a_third_buffer_replaces_the_oldest_two_stack_at_most()
    {
        var h = new ZoneHarness(seed: 89);
        var b = As(h, "buffer", 12);
        var me = b.ServerSide;
        me.Buffs.Add(new ActiveBuff { SkillId = "bf_bless", Stat = "damage", Amount = 0.15f, Until = h.Now + 5, Source = 901 });
        me.Buffs.Add(new ActiveBuff { SkillId = "bf_bless", Stat = "damage", Amount = 0.15f, Until = h.Now + 9, Source = 902 });
        b.UseSkill(2);
        var list = me.Buffs.Where(x => x.SkillId == "bf_bless").ToList();
        Assert.Equal(2, list.Count);
        Assert.DoesNotContain(list, x => x.Source == 901);        // the oldest left
        Assert.Contains(list, x => x.Source == b.EntityId);
        Assert.InRange(me.BuffSum("damage", h.Now), 0.29f, 0.31f);
    }

    [Fact]
    public void Heal_goes_to_the_ally_in_front_else_to_yourself()
    {
        var h = new ZoneHarness(seed: 90);
        var b = As(h, "buffer", 12);
        var ally = h.Join("Friend");
        var za = h.Zone.Players.Single(x => x.Name == "Friend");
        za.Move.X = b.X; za.Move.Z = b.Z + 6f;
        za.Rec.Hp = 10;
        b.ServerSide.Rec.Hp = Math.Max(1, b.Stats.MaxHp / 2);
        int mine = b.ServerSide.Rec.Hp;
        b.UseSkill(1, 0f);                                        // 치유 toward +Z
        Assert.True(za.Rec.Hp > 10);
        Assert.Equal(mine, b.ServerSide.Rec.Hp);                  // not on me: the friend was in front

        b.Chat("/쿨타임");
        b.UseSkill(1, 180f);                                      // nobody behind → myself
        Assert.True(b.ServerSide.Rec.Hp > mine);
    }

    [Fact]
    public void Revive_stands_a_fallen_ally_up_on_the_spot_and_wastes_nothing_without_one()
    {
        var h = new ZoneHarness(seed: 91);
        var b = As(h, "buffer", 40);
        b.ClearEvents();
        b.UseSkill(5);                                            // 부활 with nobody down
        Assert.Contains(b.Notices, n => n.Text.Contains("쓰러진"));
        Assert.Equal(0f, b.Cooldowns.Last(c => c.SkillId == "bf_revive").Seconds);

        var ally = h.Join("Friend");
        var za = h.Zone.Players.Single(x => x.Name == "Friend");
        za.Move.X = b.X + 3; za.Move.Z = b.Z;
        za.Dead = true; za.Rec.Hp = 0;
        b.UseSkill(5);
        Assert.False(za.Dead);
        Assert.InRange(za.Rec.Hp, (int)(za.Stats.MaxHp * 0.39), (int)(za.Stats.MaxHp * 0.41) + 1);
    }

    [Fact]
    public void Group_heal_reaches_everyone_near()
    {
        var h = new ZoneHarness(seed: 92);
        var b = As(h, "buffer", 40);
        var f1 = h.Join("F1"); var f2 = h.Join("F2");
        foreach (var n in new[] { "F1", "F2" }) h.Zone.Players.Single(x => x.Name == n).Rec.Hp = 5;
        b.UseSkill(4);
        Assert.All(new[] { "F1", "F2" }, n => Assert.True(h.Zone.Players.Single(x => x.Name == n).Rec.Hp > 5));
    }
}
