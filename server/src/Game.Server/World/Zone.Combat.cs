using Game.Server.Data;
using Game.Server.Logic;
using Game.Server.Net;
using Game.Shared.Data;
using Game.Shared.Protocol;
using Game.Shared.World;

namespace Game.Server.World;

/// <summary>What one hit is made of (basic attack, a skill strike, an arrow ...).</summary>
public struct HitSpec
{
    public float Coefficient;
    /// <summary>Extra defense penetration on top of the character's (0..1).</summary>
    public float Penetration;
    /// <summary>Set for skill hits: mastery and skill level come from here.</summary>
    public CastContext Cast;
    /// <summary>Spec vol.4 1.3: the 후방 2배 applies to this hit.</summary>
    public bool AllowBack;
    public SkillEffect[] Effects;
    /// <summary>Element of a mage spell: damage type for the 상성 table and the side effect (화상, 둔화 ...). null = none.</summary>
    public string Element;
    /// <summary>The spell already chains (or this is a jump): the lightning effect must not chain again.</summary>
    public bool NoChain;

    public static HitSpec Basic() => new() { Coefficient = 1f };
}

public sealed partial class Zone
{
    readonly Dictionary<uint, MonsterEntity> _monsters = new();
    readonly List<(SpawnDef spawn, double at)> _respawns = new();
    readonly List<uint> _removeScratch = new();

    /// <summary>Corpses stay this long (death animation) before despawning.</summary>
    const double CorpseSeconds = 1.0;

    // ───────────── spawning ─────────────

    void SpawnAllMonsters()
    {
        foreach (var sp in _data.Spawns)
            for (int i = 0; i < sp.count; i++) SpawnMonster(sp, announce: false);
    }

    MonsterEntity SpawnMonster(SpawnDef sp, bool announce)
    {
        var def = _d.Monster(sp.monster);
        // Uniform point in the spawn disc.
        double ang = _rng.NextDouble() * Math.PI * 2, rad = Math.Sqrt(_rng.NextDouble()) * sp.radius;
        float x = sp.x + (float)(Math.Cos(ang) * rad), z = sp.z + (float)(Math.Sin(ang) * rad);
        var m = new MonsterEntity
        {
            EntityId = _nextEntityId++, Def = def, Spawn = sp,
            HomeX = x, HomeZ = z, X = x, Z = z, Y = _terrain.Height(x, z),
            Yaw = (float)(_rng.NextDouble() * 360), Hp = def.maxHp, Ai = MonsterAi.Idle,
            NextWanderAt = _now + 1 + _rng.NextDouble() * 4,
        };
        _monsters[m.EntityId] = m;
        if (announce) Broadcast(SpawnOf(m));
        return m;
    }

    // ───────────── helpers ─────────────

    /// <summary>The job whose rules apply now: the real job, or the trial's job while a trial runs.</summary>
    JobDef EffectiveJob(PlayerEntity p) => _d.Job(p.Rec.Job) ?? _d.Job(p.TrialJob);

    /// <summary>Monsters inside an arc in front of (x,z,yaw), nearest first. arcDeg 360 = all around.</summary>
    List<MonsterEntity> MonstersInArc(PlayerEntity p, float x, float z, float yaw, float range, float arcDeg, int max)
    {
        var v = _data.Config.validation;
        float halfArc = arcDeg * 0.5f + (arcDeg >= 360f ? 0f : v.hitArcToleranceDeg);
        var found = new List<(MonsterEntity m, float d2)>();
        foreach (var m in _monsters.Values)
        {
            if (m.Dead || m.Ai == MonsterAi.Returning) continue;
            if (m.Owner != null && m.Owner != p) continue;
            if (Math.Abs(m.Y - p.Move.Y) > 3f) continue;
            float reach = range + m.Def.bodyRadius + v.hitRangeTolerance;
            float d2 = Dist2(m.X, m.Z, x, z);
            if (d2 > reach * reach) continue;
            if (arcDeg < 360f && d2 > 0.25f && Yaw.Delta(Yaw.FromDirection(m.X - x, m.Z - z), yaw) > halfArc) continue;
            found.Add((m, d2));
        }
        found.Sort((a, b) => a.d2.CompareTo(b.d2));
        var list = new List<MonsterEntity>(Math.Min(max, found.Count));
        for (int i = 0; i < found.Count && i < max; i++) list.Add(found[i].m);
        return list;
    }

    // ───────────── player basic attack (melee) ─────────────

    void OnAttack(PlayerEntity p, Attack a)
    {
        if (p.Dead || p.CannotAct(_now)) return;
        var job = EffectiveJob(p);
        if (job != null && job.bow != null && job.bow.enabled) return; // archers shoot with ChargeStart / ChargeRelease
        var w = p.Stats.Weapon;
        bool ranged = job != null && job.basic != null && job.basic.kind == "projectile";
        var v = _data.Config.validation;
        // Server-side attack speed check (doc 02: 쿨타임은 서버가 검증). Early arrivals within tolerance are fine.
        float interval = w.interval / Math.Max(0.1f, p.Stats.AttackSpeed);
        if (_now - p.LastAttackAt < interval * v.attackIntervalTolerance) return;
        p.LastAttackAt = _now;
        p.Move.Yaw = a.Yaw;
        Broadcast(new ActionEvent { EntityId = p.EntityId, Action = ActionKind.Attack, Yaw = a.Yaw }, except: p);

        // Basic attack hits the nearest target inside the weapon's arc (non-target aiming, spec vol.4 1).
        if (ranged)                                  // 마법사 평타: a small magic bolt, no mana
        {
            var b = job.basic;
            string el = ElementOf(p);
            var bolt = new HitSpec { Coefficient = b.coefficient, Element = el };
            SpawnProjectile(p, a.Yaw, b.speed, b.range, b.radius, 1, bolt, ProjectileKind.Magic, job.buffer.enabled ? (byte)250 : ElementIndex(el), null);
            return;
        }
        var best = MonstersInArc(p, p.Move.X, p.Move.Z, a.Yaw, w.range, w.arcDeg, 1);
        if (best.Count == 0) return;
        var basic = HitSpec.Basic();
        basic.Coefficient *= TakeInfusion(p);
        HitMonster(p, best[0], basic);
    }

    // ───────────── the one place damage to a monster is decided ─────────────

    /// <returns>true when the monster was hit.</returns>
    bool HitMonster(PlayerEntity p, MonsterEntity m, in HitSpec h)
    {
        if (m.Dead || m.Ai == MonsterAi.Returning) return false;
        if (m.Owner != null && m.Owner != p) return false;      // someone else's trial opponent
        var cfg = _data.Config.combat;
        var st = p.Stats;
        var fx = _d.character.stats;

        var input = DamageInput.Basic(st.Attack, p.Rec.Level, m.Def.defense, st.CritChance, st.CritDamage);
        input.Coefficient = h.Coefficient;
        input.Penetration = Math.Clamp(Math.Min(fx.penetrationCap, st.Penetration) + h.Penetration, 0f, 1f);
        if (h.Cast != null) input.LevelMultiplier = 1f + _d.skillRules.powerPerLevel * (h.Cast.SkillLevel - 1);
        input.ElementMultiplier = _d.ElementMultiplier(h.Element ?? "none", m.Def.element);   // 속성 상성 (4권 1.8)
        bool allowBack = h.AllowBack || (EffectiveJob(p)?.backstab ?? false);           // 암살자: 모든 타격이 후방 2배 대상
        bool back = allowBack && Rules.IsBehind(m.X, m.Z, m.Yaw, p.Move.X, p.Move.Z, cfg.backArcDeg);
        if (back) input.ConditionMultiplier = cfg.backMultiplier;
        var skillDef = h.Cast?.Skill;
        if (skillDef != null && skillDef.executeBelow > 0 && m.Hp <= m.Def.maxHp * skillDef.executeBelow)
            input.ConditionMultiplier *= skillDef.executeMultiplier;                     // 처형: 낮은 HP에 추가 피해
        input.BuffMultiplier = 1f + p.BuffSum("damage", _now);

        int dmg = Rules.Compute(cfg, input, _rng, out bool crit);
        var flags = HitFlags.None;
        if (crit) flags |= HitFlags.Crit;
        if (back) flags |= HitFlags.Back;
        if (h.Penetration > 0.0001f) flags |= HitFlags.Pierce;

        if (!m.Def.dummy) m.Hp = Math.Max(0, m.Hp - dmg);
        if (st.Lifesteal > 0f && dmg > 0 && !p.Dead && p.Rec.Hp < st.MaxHp)      // 흡혈 (장비 옵션)
        {
            p.Rec.Hp = Math.Min(st.MaxHp, p.Rec.Hp + Math.Max(1, (int)MathF.Round(dmg * st.Lifesteal)));
            p.CharDirty = true;
        }
        p.LastCombatAt = _now;
        if (m.Hp <= 0) flags |= HitFlags.Kill;
        Broadcast(new CombatEvent { AttackerId = p.EntityId, TargetId = m.EntityId, Damage = dmg, Flags = flags, TargetHp = m.Hp, TargetMaxHp = m.Def.maxHp });

        AfterHit(p, m, h);
        if (h.Effects != null) foreach (var fxDef in h.Effects) if (fxDef.type == "cc") ApplyCcToMonster(p, m, fxDef);
        if (m.Hp > 0 && !string.IsNullOrEmpty(h.Element) && h.Element != "none") ApplyElementEffect(p, m, h, dmg);

        if (m.Hp <= 0) { KillMonster(m, p); return true; }

        // Aggro: last attacker (spec vol.4 1.2 확정). A short reaction delay before the first counter. Dummies never fight.
        if (!m.Def.dummy && m.Owner == null)
        {
            bool wasIdle = m.Ai == MonsterAi.Idle;
            bool taunted = _now < m.TauntUntil && m.Target != null && m.Target != p && !m.Target.Dead && _players.ContainsKey(m.Target.EntityId);
            if (!taunted) m.Target = p;      // 도발 중에는 다른 공격자로 바뀌지 않는다 (spec vol.4 1.2 제안)
            m.Ai = MonsterAi.Chase;
            if (wasIdle) m.NextAttackAt = Math.Max(m.NextAttackAt, _now + 0.5);
        }
        return true;
    }

    /// <summary>Damage that is already decided (화상 ticks): no crit, no aggro change, but it can kill.</summary>
    void DamageMonsterDirect(PlayerEntity p, MonsterEntity m, int dmg, HitFlags flags)
    {
        if (m.Dead || m.Def.dummy || dmg <= 0) return;
        m.Hp = Math.Max(0, m.Hp - dmg);
        if (m.Hp <= 0) flags |= HitFlags.Kill;
        Broadcast(new CombatEvent { AttackerId = p.EntityId, TargetId = m.EntityId, Damage = dmg, Flags = flags, TargetHp = m.Hp, TargetMaxHp = m.Def.maxHp });
        if (m.Hp <= 0) KillMonster(m, p);
    }

    /// <summary>Training (동양 검사) and skill mastery for one landed hit.</summary>
    void AfterHit(PlayerEntity p, MonsterEntity m, in HitSpec h)
    {
        GainTraining(p, skillHit: h.Cast != null, onDummy: m.Def.dummy);
        if (h.Cast != null && !h.Cast.Landed)
        {
            h.Cast.Landed = true;
            AddSkillUse(p, h.Cast.Skill);
        }
    }

    void KillMonster(MonsterEntity m, PlayerEntity killer)
    {
        m.Dead = true;
        m.Target = null;
        m.RemoveAt = _now + CorpseSeconds;
        if (m.Owner != null) return;   // a trial opponent: UpdateTrials sees Hp <= 0 and finishes the trial. No EXP, no drops.
        if (m.Spawn != null) _respawns.Add((m.Spawn, _now + m.Def.respawnSec));

        // Last hit takes everything (막타 루팅, roadmap stage 1). Drops are decided here only.
        long exp = Rules.MonsterExp(_d.exp, m.Def.exp, m.Def.level, killer.Rec.Level);
        GrantExp(killer, exp, "kill:" + m.Def.id);

        if (!string.IsNullOrEmpty(m.Def.dropTable) && _data.Drops.TryGetValue(m.Def.dropTable, out var table))
        {
            int gold = Rules.MonsterGold(_data.Config.economy, table.goldMin, table.goldMax, m.Def.level, _rng);
            if (gold > 0)
            {
                killer.Rec.Gold += gold;
                killer.CharDirty = killer.SaveDirty = true;
                Gain(killer, GainKind.Gold, "", gold);
            }
            foreach (var di in table.items)
            {
                if (_rng.NextDouble() >= di.chance) continue;
                int count = _rng.Next(di.min, di.max + 1);
                if (di.grades != null && di.grades.Count > 0) GiveGear(killer, di.item, RollGrade(di.grades));
                else GiveItem(killer, di.item, count);
            }
        }
        if (killer.Quests.OnKill(m.Def.id)) killer.QuestDirty = killer.SaveDirty = true;
    }

    void GiveItem(PlayerEntity p, string itemId, int count)
    {
        int lost = p.Inv.Add(itemId, count);
        int got = count - lost;
        if (got > 0) Gain(p, GainKind.Item, itemId, got);
        if (lost > 0) Gain(p, GainKind.ItemLost, itemId, lost);
        p.InvDirty = p.SaveDirty = true;
        if (p.Quests.Tracks(itemId)) p.QuestDirty = true;
    }

    void GrantExp(PlayerEntity p, long amount, string source)
    {
        if (amount <= 0) return;
        int before = p.Rec.Level;
        int levels = Rules.AddExp(_d, p.Rec, amount, DateTime.UtcNow);
        Gain(p, GainKind.Exp, source, amount);
        p.CharDirty = p.SaveDirty = true;
        if (levels <= 0) return;
        RecalcStats(p);
        p.Rec.Hp = p.Stats.MaxHp; // level-up heals fully
        p.SkillDirty = true;      // new skills may have unlocked
        Gain(p, GainKind.LevelUp, "", p.Rec.Level);
        Audit(p, "level_up", new { from = before, to = p.Rec.Level, source });
        p.SaveNow = true;
        Log.Info($"level #{p.EntityId} '{p.Name}' {before} → {p.Rec.Level}");
    }

    // ───────────── monster AI ─────────────

    void UpdateMonsters(float dt)
    {
        _removeScratch.Clear();
        foreach (var m in _monsters.Values)
        {
            if (m.Dead)
            {
                if (_now >= m.RemoveAt) _removeScratch.Add(m.EntityId);
                continue;
            }
            if (m.BurnUntil > 0) { BurnTick(m); if (m.Dead) continue; }
            Think(m, dt);
        }
        foreach (var id in _removeScratch)
        {
            _monsters.Remove(id);
            Broadcast(new EntityDespawn { EntityId = id });
        }
        for (int i = _respawns.Count - 1; i >= 0; i--)
        {
            if (_now < _respawns[i].at) continue;
            SpawnMonster(_respawns[i].spawn, announce: true);
            _respawns.RemoveAt(i);
        }
    }

    void Think(MonsterEntity m, float dt)
    {
        var def = m.Def;
        if (def.dummy) return;
        // Stunned / frozen monsters do nothing; rooted ones cannot move but still fight; slowed ones are slower (spec vol.4 1.4).
        if (m.CannotAct(_now)) return;
        bool rooted = _now < m.RootUntil;
        float speedMul = _now < m.SlowUntil ? _data.Config.combat.slowMultiplier : 1f;
        float homeD2 = Dist2(m.X, m.Z, m.HomeX, m.HomeZ);

        if (m.Ai == MonsterAi.Returning)
        {
            if (rooted) return;
            if (StepToward(m, m.HomeX, m.HomeZ, def.moveSpeed * 1.3f * speedMul * dt, 0.3f))
            {
                m.Ai = MonsterAi.Idle;
                m.NextWanderAt = _now + 2 + _rng.NextDouble() * 3;
                if (m.Hp != def.maxHp)
                {
                    m.Hp = def.maxHp; // spec vol.4 1.2 제안: 끌려가면 돌아가며 체력 회복
                    Broadcast(new HpUpdate { EntityId = m.EntityId, Hp = m.Hp, MaxHp = def.maxHp });
                }
            }
            return;
        }

        // Drop invalid targets.
        if (m.Target != null && (m.Target.Dead || !_players.ContainsKey(m.Target.EntityId)))
        {
            m.Target = null;
            m.Ai = homeD2 > 1f ? MonsterAi.Returning : MonsterAi.Idle;
            return;
        }

        // 선공: look for someone in range (who is also within this monster's territory).
        if (m.Target == null && def.aggressive)
        {
            PlayerEntity best = null;
            float bestD2 = def.aggroRange * def.aggroRange;
            float leash2 = def.leashRange * def.leashRange;
            foreach (var p in _players.Values)
            {
                if (p.Dead) continue;
                float d2 = Dist2(p.Move.X, p.Move.Z, m.X, m.Z);
                if (d2 <= bestD2 && Dist2(p.Move.X, p.Move.Z, m.HomeX, m.HomeZ) <= leash2) { best = p; bestD2 = d2; }
            }
            if (best != null)
            {
                m.Target = best;
                m.Ai = MonsterAi.Chase;
                m.NextAttackAt = Math.Max(m.NextAttackAt, _now + 0.4);
            }
        }

        if (m.Target != null)
        {
            if (m.Owner == null && homeD2 > def.leashRange * def.leashRange)
            {
                m.Target = null;
                m.Ai = MonsterAi.Returning;
                return;
            }
            var t = m.Target;
            float d = MathF.Sqrt(Dist2(t.Move.X, t.Move.Z, m.X, m.Z));
            m.Yaw = Yaw.FromDirection(t.Move.X - m.X, t.Move.Z - m.Z);
            float reach = def.attackRange + 0.4f; // player body
            if (d > reach * 0.9f)
            {
                if (!rooted) StepToward(m, t.Move.X, t.Move.Z, def.moveSpeed * speedMul * dt, reach * 0.8f);
            }
            else if (_now >= m.NextAttackAt && Math.Abs(t.Move.Y - m.Y) < 2.5f)
            {
                m.NextAttackAt = _now + def.attackInterval;
                Broadcast(new ActionEvent { EntityId = m.EntityId, Action = ActionKind.Attack, Yaw = m.Yaw });
                MonsterHits(m, t);
            }
            return;
        }

        // Idle wander around home.
        if (!m.HasWanderGoal && _now >= m.NextWanderAt && def.wanderRadius > 0)
        {
            double ang = _rng.NextDouble() * Math.PI * 2, rad = Math.Sqrt(_rng.NextDouble()) * def.wanderRadius;
            m.WanderX = m.HomeX + (float)(Math.Cos(ang) * rad);
            m.WanderZ = m.HomeZ + (float)(Math.Sin(ang) * rad);
            m.HasWanderGoal = true;
        }
        if (m.HasWanderGoal && !rooted && StepToward(m, m.WanderX, m.WanderZ, def.moveSpeed * 0.35f * speedMul * dt, 0.2f))
        {
            m.HasWanderGoal = false;
            m.NextWanderAt = _now + 3 + _rng.NextDouble() * 5;
        }
    }

    /// <summary>Moves toward (x,z) by at most step, stopping at stopDist. Returns true when arrived.</summary>
    bool StepToward(MonsterEntity m, float x, float z, float step, float stopDist)
    {
        float dx = x - m.X, dz = z - m.Z;
        float d = MathF.Sqrt(dx * dx + dz * dz);
        if (d <= stopDist) return true;
        float move = Math.Min(step, d - stopDist);
        m.X += dx / d * move;
        m.Z += dz / d * move;
        m.Y = _terrain.Height(m.X, m.Z);
        m.Yaw = Yaw.FromDirection(dx, dz);
        return d - move <= stopDist + 0.001f;
    }

    void MonsterHits(MonsterEntity m, PlayerEntity p)
    {
        // Dodge roll: no damage during the invulnerable moment.
        if (_now < p.InvulnUntil) return;

        // 반격 자세: the hit is blocked and answered once.
        if (_now < p.CounterUntil && p.CounterSkill != null)
        {
            var skill = p.CounterSkill;
            p.CounterUntil = 0; p.CounterSkill = null;
            Broadcast(new CombatEvent { AttackerId = m.EntityId, TargetId = p.EntityId, Damage = 0, Flags = HitFlags.None, TargetHp = p.Rec.Hp, TargetMaxHp = p.Stats.MaxHp });
            CounterStrike(p, skill, m);
            return;
        }

        var cfg = _data.Config.combat;
        var input = DamageInput.Basic(m.Def.attack, m.Def.level, p.Stats.Defense, 0f, 1f);
        input.Penetration = m.Def.penetration;
        input.ElementMultiplier = 1f; // players have no defence element yet
        int dmg = Rules.Compute(cfg, input, _rng, out _);
        float takenCut = Math.Clamp(p.BuffSum("damageTaken", _now), 0f, 0.9f);     // 철벽 · 수호: 받는 피해 감소
        if (takenCut > 0f) dmg = (int)Math.Round(dmg * (1f - takenCut));
        // 마력 방패 soaks the hit first.
        if (p.ShieldAmount > 0 && _now < p.ShieldUntil)
        {
            float soaked = Math.Min(dmg, p.ShieldAmount);
            p.ShieldAmount -= soaked;
            dmg -= (int)Math.Ceiling(soaked);
            if (dmg <= 0)
            {
                p.LastCombatAt = _now;
                Broadcast(new CombatEvent { AttackerId = m.EntityId, TargetId = p.EntityId, Damage = 0, Flags = HitFlags.None, TargetHp = p.Rec.Hp, TargetMaxHp = p.Stats.MaxHp });
                return;
            }
        }
        p.Rec.Hp = Math.Max(0, p.Rec.Hp - dmg);
        p.LastCombatAt = _now;
        p.SaveDirty = true;
        var flags = p.Rec.Hp <= 0 ? HitFlags.Kill : HitFlags.None;
        if (m.Def.penetration > 0.0001f) flags |= HitFlags.Pierce;
        Broadcast(new CombatEvent { AttackerId = m.EntityId, TargetId = p.EntityId, Damage = dmg, Flags = flags, TargetHp = p.Rec.Hp, TargetMaxHp = p.Stats.MaxHp });
        if (p.Rec.Hp <= 0) { Die(p, m.EntityId, m.Def.name); return; }
        FallFromFlight(p);   // 비행 중 피격 → 추락

        var cc = m.Def.hitCc;
        if (cc != null && cc.type != "none" && _rng.NextDouble() < cc.chance)
            ApplyCcToPlayer(p, cc.type, cc.duration, cc.distance, m.X, m.Z);
    }

    // ───────────── death / respawn (spec vol.4 1.6 확정: 패널티 없음, 가까운 마을에서 부활) ─────────────

    void Die(PlayerEntity p, uint killerId, string killerName)
    {
        p.Dead = true;
        p.DeadAt = _now;
        p.DialogueNpc = 0;
        p.CharDirty = true;
        p.ChargeStartedAt = -1;
        p.CounterUntil = 0;
        p.StunUntil = p.FrozenUntil = p.RootUntil = p.SlowUntil = 0;
        p.ShieldAmount = 0;
        if (p.Move.FlyUntil > _now) { p.Move.FlyUntil = 0; SendFlight(p, false, true); }
        p.Conn.Send(new DeathEvent { KillerId = killerId, KillerName = killerName ?? "", RespawnAvailableIn = _d.character.respawnDelaySec });
        Log.Info($"death #{p.EntityId} '{p.Name}' by {killerName}");
    }

    void OnRespawnRequest(PlayerEntity p)
    {
        if (!p.Dead || _now < p.DeadAt + _d.character.respawnDelaySec - 0.05) return;
        var sp = _d.map.respawnPoint;
        p.Dead = false;
        p.Rec.Hp = p.Stats.MaxHp;
        p.LastCombatAt = -999;
        float y = _terrain.Height(sp.x, sp.z);
        _validator.Init(p.Move, sp.x, y, sp.z, _now);
        p.Move.Yaw = sp.yaw;
        Correct(p, CorrectionReason.Respawn);
        p.Conn.Send(new HpUpdate { EntityId = p.EntityId, Hp = p.Rec.Hp, MaxHp = p.Stats.MaxHp });
        p.CharDirty = p.SaveDirty = true;
    }
}
