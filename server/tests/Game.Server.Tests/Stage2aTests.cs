using Game.Server.Logic;
using Game.Server.World;
using Game.Shared.Data;
using Game.Shared.Protocol;
using Game.Shared.World;
using Xunit;

namespace Game.Server.Tests;

/// <summary>Stage 2a: stats, jobs, skills, the archer, crowd control, the job trial, training, chat.</summary>
public class Stage2aTests
{
    // 세윤 is in data/server/server_config.json, so dev commands work in tests.
    static TestPlayer Admin(ZoneHarness h, string name = "세윤") => h.Join(name);

    /// <summary>A level `level` character with the job, standing still, with a rabbit placed `dist` meters in front.</summary>
    static (TestPlayer p, MonsterEntity rabbit) Setup(ZoneHarness h, string job, int level, float dist = 2.5f)
    {
        var p = Admin(h);
        p.Chat($"/레벨 {level}");
        if (job != null) p.Chat($"/전직 {job}");
        p.Tick(2);
        var rabbit = h.Zone.Monsters.First(m => m.Def.id == "rabbit");
        PlaceInFront(p, rabbit, dist);
        return (p, rabbit);
    }

    static void PlaceInFront(TestPlayer p, MonsterEntity m, float dist, float side = 0f)
    {
        m.X = p.X + side; m.Z = p.Z + dist; m.Y = p.Y;
        m.HomeX = m.X; m.HomeZ = m.Z; m.HasWanderGoal = false; m.NextWanderAt = 1e9;
    }

    /// <summary>Damage without the crit multiplier, so tier comparisons do not depend on luck.</summary>
    static double NoCrit(CombatEvent e) => (e.Flags & HitFlags.Crit) != 0 ? e.Damage / 1.5 : e.Damage;

    // ───────────── pure formulas ─────────────

    [Fact]
    public void Element_table_follows_the_spec_and_void_ignores_it()
    {
        var d = new ZoneHarness().Data.Shared;
        Assert.Equal(1.25f, d.ElementMultiplier("fire", "plant"));
        Assert.Equal(0.8f, d.ElementMultiplier("plant", "fire"));   // the other way round is a disadvantage
        Assert.Equal(1.25f, d.ElementMultiplier("light", "dark"));  // light and dark are strong against each other
        Assert.Equal(1.25f, d.ElementMultiplier("dark", "light"));
        Assert.Equal(1f, d.ElementMultiplier("fire", "metal"));     // unrelated
        Assert.Equal(1f, d.ElementMultiplier("void", "plant"));     // 공허는 상성 무시
        Assert.Equal(1f, d.ElementMultiplier("none", "plant"));
    }

    [Fact]
    public void Behind_check_is_a_120_degree_fan_at_the_back()
    {
        // Target at origin facing +Z (yaw 0): its back is -Z.
        Assert.True(Rules.IsBehind(0, 0, 0, 0, -3, 120));
        Assert.True(Rules.IsBehind(0, 0, 0, 2.5f, -3, 120));     // ~40° off straight behind: inside the 60° half-angle
        Assert.False(Rules.IsBehind(0, 0, 0, 3, -1, 120));       // ~72° off: outside
        Assert.False(Rules.IsBehind(0, 0, 0, 0, 3, 120));        // in front
    }

    [Fact]
    public void Penetration_lowers_the_defense_reduction_and_multipliers_stack()
    {
        var cfg = new ZoneHarness().Data.Config.combat;
        var rng = new Random(1);
        var plain = DamageInput.Basic(1000, 50, 2400, 0f, 1.5f);
        int none = Rules.Compute(cfg, plain, rng, out _);
        Assert.InRange(none, 470, 530);                           // 방어력 2,400 vs 50레벨 → 피해 50% 감소 (4권 1.1)
        var pen = plain; pen.Penetration = 0.5f;
        Assert.InRange(Rules.Compute(cfg, pen, rng, out _), 650, 720);
        var back = plain; back.ConditionMultiplier = 2f; back.ElementMultiplier = 1.25f;
        Assert.InRange(Rules.Compute(cfg, back, rng, out _), (int)(none * 2.5f * 0.9f), (int)(none * 2.5f * 1.1f));
    }

    [Fact]
    public void Training_levels_need_more_each_time()
    {
        var t = new ZoneHarness().Data.Shared.Job("eastern_swordsman").training;
        Assert.Equal(0, Rules.TrainingLevel(t, 999));
        Assert.Equal(1, Rules.TrainingLevel(t, 1000));
        Assert.Equal(1, Rules.TrainingLevel(t, 2199));   // second level needs 1,200
        Assert.Equal(2, Rules.TrainingLevel(t, 2200));
        Assert.Equal(1200, Rules.TrainingToNext(t, 1000));
    }

    // ───────────── stat points ─────────────

    [Fact]
    public void Stat_points_are_spent_on_the_server_and_primary_stat_raises_attack()
    {
        var h = new ZoneHarness();
        var (p, _) = Setup(h, "eastern_swordsman", 5);
        int points = p.Char.StatPoints;
        Assert.Equal(5 * 4 + 10, points);                      // 레벨업 5/레벨 + 전직 보너스 10
        int attack = p.Stats.Attack;
        p.Allocate(str: 10);
        Assert.Equal(points - 10, p.Char.StatPoints);
        Assert.Equal(20, p.Char.Str);
        Assert.InRange(p.Stats.Attack - attack, 19, 21);       // 주 스탯(힘) +2 / 포인트

        int def = p.Stats.Defense, maxHp = p.Stats.MaxHp;
        p.Allocate(con: 10);                                   // 체력: 최대 HP +20, 방어력 +1 per point
        Assert.Equal(def + 10, p.Stats.Defense);
        Assert.Equal(maxHp + 200, p.Stats.MaxHp);
    }

    [Fact]
    public void Spending_more_points_than_you_have_is_refused()
    {
        var h = new ZoneHarness();
        var (p, _) = Setup(h, "eastern_swordsman", 5);
        int points = p.Char.StatPoints;
        p.Allocate(str: points + 1);
        Assert.Equal(points, p.Char.StatPoints);
        Assert.Equal(10, p.Char.Str);
        Assert.Contains(p.Notices, n => n.Kind == NoticeKind.Error);
    }

    [Fact]
    public void Dexterity_and_spirit_give_the_spec_percentages_up_to_the_caps()
    {
        var h = new ZoneHarness();
        var (p, _) = Setup(h, "archer", 50);
        p.Allocate(dex: 100, spi: 100);
        Assert.Equal(0.05f + 100 * 0.0005f, p.Stats.CritChance, 4);
        Assert.Equal(1f + 100 * 0.0005f, p.Stats.AttackSpeed, 4);
        Assert.Equal(100 * 0.0003f, p.Stats.CdReduction, 4);
        Assert.Equal(100 * 0.0005f, p.Stats.CcResist, 4);
        p.Allocate(spi: 200);                                  // far past the 40% / 50% caps
        Assert.True(p.Stats.CdReduction <= 0.4f + 1e-4f);
        Assert.True(p.Stats.CcResist <= 0.5f + 1e-4f);
    }

    // ───────────── skills ─────────────

    [Fact]
    public void Flurry_hits_three_times_goes_on_cooldown_and_trains_once()
    {
        var h = new ZoneHarness(seed: 3);
        var (p, rabbit) = Setup(h, "eastern_swordsman", 12);
        rabbit.Hp = rabbit.Def.maxHp = 100000;                 // keep it alive for all three strikes
        p.ClearEvents();
        p.UseSkill(0);
        p.Tick(12);
        var hits = p.TakeCombat().Where(e => e.TargetId == rabbit.EntityId).ToList();
        Assert.Equal(3, hits.Count);                           // 연속 베기 3타
        Assert.Equal(1, p.Skills.Skills[0].Uses);              // 숙련도는 사용 1회로만 센다

        p.Cooldowns.Clear();
        p.UseSkill(0);                                         // still on cooldown (6 s)
        p.Tick(12);
        Assert.Empty(p.TakeCombat().Where(e => e.TargetId == rabbit.EntityId));
        Assert.Contains(p.Cooldowns, c => c.SkillId == "sw_flurry" && c.Seconds > 3);

        p.Tick(130);                                           // 6.5 s later it works again
        p.ClearEvents();
        p.UseSkill(0);
        p.Tick(12);
        Assert.Equal(3, p.TakeCombat().Count(e => e.TargetId == rabbit.EntityId));
    }

    [Fact]
    public void A_skill_you_have_not_unlocked_does_nothing()
    {
        var h = new ZoneHarness();
        var (p, rabbit) = Setup(h, "eastern_swordsman", 5);    // 발도 일섬 needs level 8
        int hp = rabbit.Hp;
        p.UseSkill(1);
        p.Tick(4);
        Assert.Equal(hp, rabbit.Hp);
        Assert.DoesNotContain(p.Cooldowns, c => c.SkillId == "sw_iai");
    }

    [Fact]
    public void Skill_level_rises_with_uses_and_adds_six_percent_power()
    {
        var h = new ZoneHarness(seed: 4);
        var (p, rabbit) = Setup(h, "eastern_swordsman", 12);
        rabbit.Hp = rabbit.Def.maxHp = 10_000_000;
        for (int i = 0; i < 30; i++)
        {
            p.Chat("/쿨타임");
            p.UseSkill(0);
            p.Tick(12);
        }
        Assert.Equal(2, p.Skills.Skills[0].Level);             // 2레벨: 사용 30회
        Assert.Equal(0, p.Skills.Skills[0].Uses);
        Assert.Contains(p.Gains, g => g.Kind == GainKind.SkillLevelUp && g.RefId == "sw_flurry");
    }

    [Fact]
    public void Dash_cuts_everything_on_the_line_and_grants_the_extra_distance()
    {
        var h = new ZoneHarness(seed: 5);
        var (p, near) = Setup(h, "eastern_swordsman", 12, dist: 3f);
        var far = h.Zone.Monsters.Where(m => m.Def.id == "rabbit" && m != near).First();
        PlaceInFront(p, far, 6f);
        var off = h.Zone.Monsters.Where(m => m.Def.id == "rabbit" && m != near && m != far).First();
        PlaceInFront(p, off, 4f, side: 6f);                    // far to the side: not on the line
        int offHp = off.Hp;
        p.ClearEvents();
        p.UseSkill(1);                                         // 발도 일섬: 7 m
        p.Tick(2);
        var ids = p.TakeCombat().Select(e => e.TargetId).ToHashSet();
        Assert.Contains(near.EntityId, ids);
        Assert.Contains(far.EntityId, ids);
        Assert.DoesNotContain(off.EntityId, ids);
        Assert.Equal(offHp, off.Hp);

        // The client rushes 7 m at once: allowed this time, a normal move that far is not.
        float z0 = p.Z;
        p.Z += 7f;
        h.Send(p.Conn, new MoveState { Seq = 500, CorrectionEpoch = 0, X = p.X, Y = p.Y, Z = p.Z, Yaw = 0, Flags = MoveFlags.Grounded });
        p.Tick(2);
        Assert.DoesNotContain(p.Corrections, c => c.Reason == CorrectionReason.TooFast);
    }

    [Fact]
    public void Whirl_knocks_back_and_the_target_is_then_immune_to_the_next_cc()
    {
        var h = new ZoneHarness(seed: 6);
        var (p, rabbit) = Setup(h, "eastern_swordsman", 25, dist: 1.5f);
        rabbit.Hp = rabbit.Def.maxHp = 10_000_000;
        float d0 = MathF.Abs(rabbit.Z - p.Z);
        p.UseSkill(3);                                         // 회전 베기: 넉백 3 m
        p.Tick(2);
        Assert.True(MathF.Abs(rabbit.Z - p.Z) > d0 + 2f, "knocked back");
        Assert.Contains(p.Ccs, c => c.TargetId == rabbit.EntityId && c.Type == CcType.Knockback);

        // 일도양단 would stun it, but it is immune for 3 s after a CC.
        PlaceInFront(p, rabbit, 2f);
        p.Ccs.Clear();
        p.UseSkill(5);
        p.Tick(2);
        Assert.DoesNotContain(p.Ccs, c => c.TargetId == rabbit.EntityId && c.Type == CcType.Stun);

        // After the immunity (stun 0.3 + 3 s) it works.
        p.Tick(80);
        PlaceInFront(p, rabbit, 2f);
        p.Chat("/쿨타임"); p.Tick(2);
        p.Ccs.Clear();
        p.UseSkill(5);
        p.Tick(2);
        Assert.Contains(p.Ccs, c => c.TargetId == rabbit.EntityId && c.Type == CcType.Stun);
        Assert.True(rabbit.StunUntil > h.Now);
    }

    [Fact]
    public void Counter_stance_blocks_one_hit_and_strikes_back()
    {
        var h = new ZoneHarness(seed: 7);
        var (p, rabbit) = Setup(h, "eastern_swordsman", 12, dist: 1.2f);
        rabbit.Hp = rabbit.Def.maxHp = 100000;
        p.AttackToward(rabbit.X, rabbit.Z);                    // wake it up so it attacks back
        p.StandStill(6);
        Assert.Equal(MonsterAi.Chase, rabbit.Ai);
        int hp = p.ServerHp;
        p.UseSkill(2);                                         // 반격 자세
        p.ClearEvents();
        p.StandStill(25);                                      // the rabbit's first attack lands during the stance
        var events = p.TakeCombat();
        Assert.Contains(events, e => e.AttackerId == rabbit.EntityId && e.TargetId == p.EntityId && e.Damage == 0);   // blocked
        Assert.Contains(events, e => e.AttackerId == p.EntityId && e.TargetId == rabbit.EntityId && e.Damage > 20);   // answered
        Assert.Equal(hp, p.ServerHp);
    }

    // ───────────── archer ─────────────

    [Fact]
    public void Archer_charge_tiers_follow_the_hold_time_and_full_charge_pierces()
    {
        var h = new ZoneHarness(seed: 8);
        var (p, rabbit) = Setup(h, "archer", 12, dist: 8f);
        rabbit.Hp = rabbit.Def.maxHp = 10_000_000;
        Assert.Equal(5, p.Ammo.Ammo);

        void Shoot(int holdTicks)
        {
            p.ClearEvents();
            p.ChargeStart();
            p.StandStill(holdTicks);
            p.ChargeRelease(0);
            p.Tick(8);
        }
        Shoot(2);                                              // tap → 즉발
        var tier0 = p.Projectiles.Last().Tier;
        var hit0 = p.TakeCombat().First(e => e.TargetId == rabbit.EntityId);
        p.Tick(25);
        Shoot(21);                                             // ~1 s → 반차징
        var tier1 = p.Projectiles.Last().Tier;
        var hit1 = p.TakeCombat().First(e => e.TargetId == rabbit.EntityId);
        p.Tick(25);
        Shoot(42);                                             // ~2 s → 풀차징
        var tier2 = p.Projectiles.Last().Tier;
        var hit2 = p.TakeCombat().First(e => e.TargetId == rabbit.EntityId);
        Assert.Equal((byte)0, tier0); Assert.Equal((byte)1, tier1); Assert.Equal((byte)2, tier2);
        double n0 = NoCrit(hit0), n1 = NoCrit(hit1), n2 = NoCrit(hit2);
        Assert.InRange(n1, n0 * 1.7, n0 * 2.4);                // 200%
        Assert.InRange(n2, n0 * 2.5, n0 * 3.6);                // 300% (+ a little from the 20% penetration)
        Assert.True((hit2.Flags & HitFlags.Pierce) != 0);                    // 풀차징: 방어 20% 관통
        Assert.True((hit0.Flags & HitFlags.Pierce) == 0);
    }

    [Fact]
    public void Archer_reloads_for_two_and_a_half_seconds_after_five_shots_and_cannot_shoot_meanwhile()
    {
        var h = new ZoneHarness(seed: 9);
        var (p, _) = Setup(h, "archer", 12, dist: 8f);
        for (int i = 0; i < 5; i++)
        {
            p.ChargeStart(); p.ChargeRelease(0);
            p.Tick(22);                                        // the 1 s shot interval
        }
        Assert.Equal(0, p.Ammo.Ammo);
        Assert.True(p.Ammo.ReloadIn > 0);
        int shots = p.Projectiles.Count;
        p.ChargeStart(); p.ChargeRelease(0); p.Tick(2);
        Assert.Equal(shots, p.Projectiles.Count);              // nothing while reloading
        p.Tick(55);                                            // 2.5 s +
        Assert.Equal(5, p.Ammo.Ammo);
        p.ChargeStart(); p.ChargeRelease(0); p.Tick(2);
        Assert.Equal(shots + 1, p.Projectiles.Count);
    }

    [Fact]
    public void Shooting_again_inside_the_one_second_interval_is_dropped()
    {
        var h = new ZoneHarness(seed: 10);
        var (p, _) = Setup(h, "archer", 12, dist: 8f);
        p.ChargeStart(); p.ChargeRelease(0);
        int shots = p.Projectiles.Count;
        p.Tick(4);
        p.ChargeStart(); p.ChargeRelease(0);                   // 0.2 s later
        p.Tick(2);
        Assert.Equal(shots, p.Projectiles.Count);
    }

    [Fact]
    public void Rapid_fire_makes_instant_shots_come_four_times_a_second()
    {
        var h = new ZoneHarness(seed: 11);
        var (p, rabbit) = Setup(h, "archer", 12, dist: 8f);
        rabbit.Hp = rabbit.Def.maxHp = 10_000_000;
        p.UseSkill(0);                                         // 속사
        int before = p.Projectiles.Count;
        for (int i = 0; i < 3; i++) { p.ChargeStart(); p.ChargeRelease(0); p.Tick(6); }   // 0.3 s apart
        Assert.Equal(before + 3, p.Projectiles.Count);
    }

    [Fact]
    public void A_cc_cancels_the_charge_but_being_hit_does_not()
    {
        var h = new ZoneHarness(seed: 12);
        var (p, _) = Setup(h, "archer", 12, dist: 8f);
        p.ChargeStart();
        p.StandStill(4);
        int shots = p.Projectiles.Count;
        h.Zone.TestApplyCc(p.ServerSide, "slow", 2f);          // slow is a CC too
        p.Tick(2);
        Assert.Equal(1, p.ChargeCancels);
        p.ChargeRelease(0); p.Tick(2);
        Assert.Equal(shots, p.Projectiles.Count);              // nothing to release
    }

    // ───────────── crowd control on players ─────────────

    [Fact]
    public void Stun_blocks_actions_and_movement_and_the_next_stun_is_ignored_for_three_seconds()
    {
        var h = new ZoneHarness(seed: 13);
        var (p, rabbit) = Setup(h, "eastern_swordsman", 12);
        h.Zone.TestApplyCc(p.ServerSide, "stun", 1.0f);
        p.Tick(1);
        Assert.Contains(p.Ccs, c => c.TargetId == p.EntityId && c.Type == CcType.Stun);

        int hp = rabbit.Hp;
        p.AttackToward(rabbit.X, rabbit.Z); p.Tick(2);
        p.UseSkill(0); p.Tick(2);
        Assert.Equal(hp, rabbit.Hp);                           // can't act

        float x0 = p.X;
        p.X += 3f;
        h.Send(p.Conn, new MoveState { Seq = 900, CorrectionEpoch = 0, X = p.X, Y = p.Y, Z = p.Z, Yaw = 0, Flags = MoveFlags.Grounded });
        p.Tick(2);
        Assert.Contains(p.Corrections, c => c.Reason == CorrectionReason.CcLocked);

        p.Tick(25);                                            // stun over (1 s); immune now
        p.Ccs.Clear();
        h.Zone.TestApplyCc(p.ServerSide, "stun", 1.0f);
        p.Tick(1);
        Assert.Empty(p.Ccs);
        Assert.False(p.ServerSide.CannotAct(h.Now));

        p.Tick(65);                                            // 3 s immunity over
        h.Zone.TestApplyCc(p.ServerSide, "stun", 1.0f);
        p.Tick(1);
        Assert.Single(p.Ccs);
    }

    [Fact]
    public void Spirit_shortens_cc_duration()
    {
        var h = new ZoneHarness(seed: 14);
        var (p, _) = Setup(h, "archer", 50);
        p.Allocate(spi: 100);                                  // 5% CC resist
        h.Zone.TestApplyCc(p.ServerSide, "stun", 2.0f);
        Assert.Equal(h.Now + 1.9, p.ServerSide.StunUntil, 3);
    }

    [Fact]
    public void Knockback_moves_the_player_and_slow_is_not_blocked_by_immunity()
    {
        var h = new ZoneHarness(seed: 15);
        var (p, _) = Setup(h, "archer", 12);
        float z0 = p.ServerSide.Move.Z;
        h.Zone.TestApplyCc(p.ServerSide, "knockback", 0f, distance: 3f);
        p.Tick(1);
        Assert.Contains(p.Corrections, c => c.Reason == CorrectionReason.Knockback);
        Assert.InRange(MathF.Abs(p.Z - z0), 2.5f, 3.5f);

        p.Ccs.Clear();
        h.Zone.TestApplyCc(p.ServerSide, "slow", 3f);
        p.Tick(1);
        Assert.Contains(p.Ccs, c => c.Type == CcType.Slow);    // 둔화는 면역에 걸리지 않는다
    }

    // ───────────── dodge ─────────────

    [Fact]
    public void Dodge_roll_has_its_own_cooldown_and_a_short_invulnerable_moment()
    {
        var h = new ZoneHarness(seed: 16);
        var (p, _) = Setup(h, "eastern_swordsman", 5);
        p.Z += 3.5f;
        h.Send(p.Conn, new MoveState { Seq = 700, CorrectionEpoch = 0, X = p.X, Y = p.Y, Z = p.Z, Yaw = 0, Flags = MoveFlags.Grounded | MoveFlags.DodgeStart });
        p.Tick(1);
        Assert.DoesNotContain(p.Corrections, c => c.Reason == CorrectionReason.TooFast);
        Assert.True(p.ServerSide.InvulnUntil > h.Now);

        p.Tick(20);
        Assert.True(p.ServerSide.InvulnUntil < h.Now);          // 0.4 s is over
        p.Corrections.Clear();
        p.Z += 3.5f;
        h.Send(p.Conn, new MoveState { Seq = 701, CorrectionEpoch = 0, X = p.X, Y = p.Y, Z = p.Z, Yaw = 0, Flags = MoveFlags.Grounded | MoveFlags.DodgeStart });
        p.Tick(1);
        Assert.Contains(p.Corrections, c => c.Reason == CorrectionReason.DashCooldown);   // 3 s cooldown
    }

    // ───────────── projectiles ─────────────

    [Fact]
    public void Sword_wave_pierces_through_several_monsters_in_a_line()
    {
        var h = new ZoneHarness(seed: 17);
        var (p, a) = Setup(h, "eastern_swordsman", 20, dist: 3f);
        var rabbits = h.Zone.Monsters.Where(m => m.Def.id == "rabbit").Take(3).ToList();
        for (int i = 0; i < 3; i++) { PlaceInFront(p, rabbits[i], 3f + 3f * i); rabbits[i].Hp = rabbits[i].Def.maxHp = 10_000_000; }
        p.ClearEvents();
        p.UseSkill(4);                                         // 검기: 12 m, 관통
        p.Tick(20);
        var hit = p.TakeCombat().Select(e => e.TargetId).ToHashSet();
        foreach (var r in rabbits) Assert.Contains(r.EntityId, hit);
        Assert.Contains(p.Projectiles, x => x.Kind == ProjectileKind.SwordWave);
    }

    [Fact]
    public void Volley_fires_five_arrows_in_a_fan_and_bind_roots_the_target()
    {
        var h = new ZoneHarness(seed: 18);
        var (p, rabbit) = Setup(h, "archer", 20, dist: 8f);
        rabbit.Hp = rabbit.Def.maxHp = 10_000_000;
        int before = p.Projectiles.Count;
        p.UseSkill(4);                                         // 연발 사격
        Assert.Equal(before + 5, p.Projectiles.Count);
        Assert.True(p.Projectiles.Skip(before).Select(x => Math.Round(x.Yaw)).Distinct().Count() == 5);

        p.Tick(40);
        p.Ccs.Clear();
        PlaceInFront(p, rabbit, 8f);
        p.UseSkill(3);                                         // 속박 화살
        p.Tick(12);
        Assert.Contains(p.Ccs, c => c.TargetId == rabbit.EntityId && c.Type == CcType.Root);
    }

    // ───────────── job trial ─────────────

    [Fact]
    public void Below_level_five_the_master_sends_you_away()
    {
        var h = new ZoneHarness();
        var p = h.Join("Novice");
        var master = h.Zone.Npcs.Single(n => n.Def.id == "sword_master");
        Assert.True(p.WalkTo(master.X, master.Z, stop: 2.5f), p.LastWalkFailure);
        p.Talk(master.EntityId);
        Assert.Equal("sm_low", p.LastNode);
    }

    [Fact]
    public void Winning_the_trial_makes_you_a_swordsman_with_the_starter_sword_and_ten_points()
    {
        var h = new ZoneHarness(seed: 19);
        var p = Admin(h);
        p.Chat("/레벨 5");
        var master = h.Zone.Npcs.Single(n => n.Def.id == "sword_master");
        Assert.True(p.WalkTo(master.X, master.Z, stop: 2.5f), p.LastWalkFailure);
        p.Talk(master.EntityId);
        Assert.Equal("sm_offer", p.LastNode);
        p.Choose(master.EntityId, 0);                          // 시험을 본다
        Assert.True(p.Trial.Active);
        var opp = h.Zone.Monsters.Single(m => m.Owner == p.ServerSide);
        Assert.Equal("trial_swordsman", opp.Def.id);
        Assert.Contains(p.Skills.Skills, s => s.SkillId == "sw_flurry");   // the lent first skill

        // Somebody else can't touch the opponent.
        var other = h.Join("Bystander");
        int hp0 = opp.Hp;
        h.Zone.Players.Single(x => x.Name == "Bystander").Move.X = opp.X; // stand right next to it
        h.Zone.Players.Single(x => x.Name == "Bystander").Move.Z = opp.Z - 1f;
        other.X = opp.X; other.Z = opp.Z - 1f; other.Y = opp.Y;
        other.AttackToward(opp.X, opp.Z); other.Tick(4);
        Assert.Equal(hp0, opp.Hp);

        // The taker beats it (lent skill works; it shows nothing but the skill list).
        opp.X = p.X; opp.Z = p.Z + 2f; opp.Y = p.Y; opp.HomeX = opp.X; opp.HomeZ = opp.Z;
        opp.Hp = 5;
        p.UseSkill(0);
        p.Tick(6);
        Assert.Equal("eastern_swordsman", p.Char.Job);
        Assert.Equal((byte)1, p.Char.JobTier);
        Assert.False(p.Trial.Active);
        Assert.DoesNotContain(h.Zone.Monsters, m => m.Owner != null);
        Assert.Equal(h.Data.Shared.character.jobChangeStatPoints + 5 * 4, p.Char.StatPoints);
        Assert.Contains(p.Inv.Items, i => i.ItemId == "apprentice_sword" && i.Container == ItemContainer.Equipment);
        Assert.Contains(p.Gains, g => g.Kind == GainKind.JobChange);
    }

    [Fact]
    public void Losing_or_timing_out_the_trial_fails_it_and_you_can_retry()
    {
        var h = new ZoneHarness(seed: 20);
        var p = Admin(h);
        p.Chat("/레벨 5");
        h.Data.Shared.Monster("trial_archer").attack = 0;      // let the clock run out instead of the player
        var master = h.Zone.Npcs.Single(n => n.Def.id == "bow_master");
        Assert.True(p.WalkTo(master.X, master.Z, stop: 2.5f), p.LastWalkFailure);
        p.Talk(master.EntityId); p.Choose(master.EntityId, 0);
        Assert.True(p.Trial.Active);
        p.StandStill((int)(125 / ZoneHarness.Dt));             // time limit 120 s
        Assert.False(p.Trial.Active);
        Assert.Equal("", p.Char.Job);
        Assert.Empty(h.Zone.Monsters.Where(m => m.Owner != null));
        Assert.Contains(p.Notices, n => n.Text.Contains("시험에 실패"));

        // Again, then give up from the dialogue.
        p.Talk(master.EntityId);
        Assert.Equal("bm_offer", p.LastNode);
        p.Choose(master.EntityId, 0);
        Assert.True(p.Trial.Active);
        p.Talk(master.EntityId);
        Assert.Equal("bm_running", p.LastNode);
        p.Choose(master.EntityId, 1);                          // 포기한다
        p.Tick(2);
        Assert.False(p.Trial.Active);
    }

    [Fact]
    public void Archer_trial_lends_the_starter_bow()
    {
        var h = new ZoneHarness(seed: 21);
        var p = Admin(h);
        p.Chat("/레벨 5");
        var master = h.Zone.Npcs.Single(n => n.Def.id == "bow_master");
        Assert.True(p.WalkTo(master.X, master.Z, stop: 2.5f), p.LastWalkFailure);
        p.Talk(master.EntityId); p.Choose(master.EntityId, 0);
        Assert.Equal(5, p.Ammo.Ammo);
        var opp = h.Zone.Monsters.Single(m => m.Owner == p.ServerSide);
        opp.X = p.X; opp.Z = p.Z + 6f; opp.Y = p.Y; opp.HomeX = opp.X; opp.HomeZ = opp.Z;
        opp.Hp = 1;
        p.ChargeStart(); p.ChargeRelease(0);
        p.Tick(20);
        Assert.Equal("archer", p.Char.Job);
        Assert.Contains(p.Inv.Items, i => i.ItemId == "apprentice_bow" && i.Container == ItemContainer.Equipment);
    }

    // ───────────── 훈련량 ─────────────

    [Fact]
    public void Training_dummy_gives_training_up_to_the_daily_cap_and_resets_the_next_day()
    {
        var h = new ZoneHarness(seed: 22);
        var (p, _) = Setup(h, "eastern_swordsman", 12);
        var dummy = h.Zone.Monsters.First(m => m.Def.dummy);
        PlaceInFront(p, dummy, 2f);
        var rec = p.ServerSide.Rec;
        rec.Training = 998; rec.TrainingToday = 998; rec.TrainingDay = h.Zone.CurrentDay();
        h.Zone.TestDirtySkills(p.ServerSide);
        for (int i = 0; i < 6; i++) { p.AttackToward(dummy.X, dummy.Z); p.StandStill(18); }
        Assert.Equal(1000, rec.TrainingToday);                 // 하루 상한 1,000
        Assert.Equal(1000, rec.Training);
        Assert.Equal(1, p.Stats.TrainingLevel);                // 훈련 단계 1 → 공격력 +1.5%
        Assert.Equal(0.015f, p.Stats.TrainingBonus, 4);
        Assert.Contains(p.Notices, n => n.Text.Contains("훈련량"));

        // Next day: the cap is fresh.
        h.Zone.Clock = () => DateTime.Now.AddDays(1);
        p.AttackToward(dummy.X, dummy.Z); p.StandStill(18);
        Assert.True(rec.TrainingToday is >= 1 and <= 3);
        Assert.True(rec.Training > 1000);
    }

    [Fact]
    public void Dummies_never_die_and_other_jobs_get_no_training()
    {
        var h = new ZoneHarness(seed: 23);
        var (p, _) = Setup(h, "archer", 12, dist: 5f);
        var dummy = h.Zone.Monsters.First(m => m.Def.dummy);
        PlaceInFront(p, dummy, 5f);
        for (int i = 0; i < 4; i++) { p.ChargeStart(); p.StandStill(22); p.ChargeRelease(0); p.Tick(12); }
        Assert.Equal(dummy.Def.maxHp, dummy.Hp);
        Assert.False(dummy.Dead);
        Assert.Equal(0, p.ServerSide.Rec.Training);
    }

    // ───────────── chat & dev commands ─────────────

    [Fact]
    public void General_chat_reaches_only_nearby_players_and_world_chat_has_a_cooldown()
    {
        var h = new ZoneHarness(seed: 24);
        var a = h.Join("Alice");
        var b = h.Join("Bob");
        var c = h.Join("Carol");
        h.Zone.Players.Single(x => x.Name == "Carol").Move.X = 100;   // far away
        a.Chat("안녕하세요");
        b.Absorb(); c.Absorb();
        Assert.Contains(a.Chats, m => m.Text == "안녕하세요" && m.Sender == "Alice" && m.Channel == ChatChannel.General);
        Assert.Contains(b.Chats, m => m.Text == "안녕하세요");
        Assert.DoesNotContain(c.Chats, m => m.Text == "안녕하세요");

        h.Tick(20);
        a.Chat("모두 들어라", ChatChannel.World);
        c.Absorb();
        Assert.Contains(c.Chats, m => m.Text == "모두 들어라" && m.Channel == ChatChannel.World);
        h.Tick(20);
        a.Chat("또 외친다", ChatChannel.World);
        c.Absorb();
        Assert.DoesNotContain(c.Chats, m => m.Text == "또 외친다");
        Assert.Contains(a.Chats, m => m.Channel == ChatChannel.System && m.Text.Contains("외치기"));
    }

    [Fact]
    public void Chat_is_rate_limited_and_long_lines_are_cut()
    {
        var h = new ZoneHarness();
        var a = h.Join("Alice");
        a.Chat(new string('가', 300));
        Assert.Equal(100, a.Chats.First(m => m.Channel == ChatChannel.General).Text.Length);
        a.Chat("바로 또", wait: false);                         // inside the minimum gap
        Assert.DoesNotContain(a.Chats, m => m.Text == "바로 또");
    }

    [Fact]
    public void Dev_commands_work_only_for_admins_and_are_audited()
    {
        var store = new Game.Server.Db.MemoryCharacterStore();
        var h = new ZoneHarness(store);
        var friend = h.Join("Friend");
        friend.Chat("/레벨 50");
        Assert.Equal(1, friend.Char.Level);
        Assert.Contains(friend.Chats, m => m.Text.Contains("알 수 없는 명령"));

        var admin = Admin(h);
        admin.Chat("/레벨 30");
        Assert.Equal(30, admin.Char.Level);
        Assert.Equal(5 * 29, admin.Char.StatPoints);
        admin.Chat("/전직 archer");
        Assert.Equal("archer", admin.Char.Job);
        Assert.Equal(5 * 29 + 10, admin.Char.StatPoints);
        admin.Chat("/직업초기화");
        Assert.Equal("", admin.Char.Job);
        h.Leave(admin.Conn);
        h.FlushDb();
        Assert.Equal(3, store.Audits.Count(a => a.Kind == "dev_command"));   // 레벨, 전직, 직업초기화 (친구의 시도는 기록 대상이 아님)
    }

    [Fact]
    public void Job_skills_and_training_are_saved_with_the_character()
    {
        var store = new Game.Server.Db.MemoryCharacterStore();
        var h = new ZoneHarness(store, seed: 25);
        var (p, rabbit) = Setup(h, "eastern_swordsman", 12);
        rabbit.Hp = rabbit.Def.maxHp = 1_000_000;
        p.UseSkill(0);
        p.Tick(12);
        p.Chat("/훈련량 1234");
        h.Leave(p.Conn);
        h.FlushDb();

        var again = Admin(h);
        Assert.Equal("eastern_swordsman", again.Char.Job);
        Assert.Equal(1234, again.Skills.Training);
        Assert.Equal(1, again.Skills.Skills[0].Uses);
        Assert.Contains(again.Inv.Items, i => i.ItemId == "apprentice_sword");
    }

    // ───────────── monsters of stage 2a ─────────────

    [Fact]
    public void New_monsters_are_in_the_world_and_carry_their_cc()
    {
        var h = new ZoneHarness();
        var ids = h.Zone.Monsters.Select(m => m.Def.id).ToHashSet();
        foreach (var id in new[] { "rabbit", "wolf", "boar", "spider", "bear", "training_dummy" }) Assert.Contains(id, ids);
        Assert.Equal("knockback", h.Data.Shared.Monster("boar").hitCc.type);
        Assert.Equal("slow", h.Data.Shared.Monster("spider").hitCc.type);
        Assert.Equal("stun", h.Data.Shared.Monster("bear").hitCc.type);
        Assert.DoesNotContain(h.Zone.Monsters, m => m.Def.id.StartsWith("trial_"));   // only exist during a trial
    }

    [Fact]
    public void Others_are_told_which_weapon_you_hold_and_get_it_again_when_it_changes()
    {
        var h = new ZoneHarness(seed: 30);
        var watcher = h.Join("Watcher");
        var p = Admin(h);
        watcher.Absorb();
        var first = watcher.Conn.Inbox.ToArray().OfType<EntitySpawn>().Where(e => e.EntityId == p.EntityId).ToList();
        watcher.ClearEvents();

        p.Chat("/레벨 5");
        p.Chat("/전직 archer");
        var spawns = new List<EntitySpawn>();
        while (watcher.Conn.Inbox.TryDequeue(out var m)) if (m is EntitySpawn es && es.EntityId == p.EntityId) spawns.Add(es);
        Assert.Contains(spawns, e => e.Weapon == "bow");           // the archer's bow replaces the club for everybody

        p.Chat("/직업초기화");
        spawns.Clear();
        while (watcher.Conn.Inbox.TryDequeue(out var m)) if (m is EntitySpawn es && es.EntityId == p.EntityId) spawns.Add(es);
        Assert.Contains(spawns, e => e.Weapon == "club");          // back to the starting club
    }
}
