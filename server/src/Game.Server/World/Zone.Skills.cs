using Game.Server.Logic;
using Game.Server.Net;
using Game.Shared.Data;
using Game.Shared.Protocol;
using Game.Shared.World;

namespace Game.Server.World;

/// <summary>Data-driven skills (data/shared/skills.json): the server checks level, cooldown and state, then plays out the skill's kind.</summary>
public sealed partial class Zone
{
    readonly Dictionary<uint, ProjectileEntity> _projectiles = new();
    uint _nextProjectileId = 1;
    readonly List<(double at, Action act)> _scheduled = new();
    readonly List<Action> _dueScratch = new();

    void Schedule(double delay, Action a) => _scheduled.Add((_now + delay, a));

    void RunScheduled()
    {
        if (_scheduled.Count == 0) return;
        _dueScratch.Clear();
        for (int i = _scheduled.Count - 1; i >= 0; i--)
            if (_scheduled[i].at <= _now) { _dueScratch.Add(_scheduled[i].act); _scheduled.RemoveAt(i); }
        // Run in the order they were queued.
        for (int i = _dueScratch.Count - 1; i >= 0; i--) _dueScratch[i]();
    }

    bool Alive(PlayerEntity p) => !p.Dead && _players.ContainsKey(p.EntityId);

    // ───────────── skill table helpers ─────────────

    SkillDef SkillAt(PlayerEntity p, int slot)
    {
        var job = EffectiveJob(p);
        if (job == null || slot < 0 || slot >= job.skills.Length) return null;
        return _d.SkillFor(job.skills[slot], job.mage.enabled && p.Rec.Job == job.id ? p.Rec.Element : null);   // 마법사: the chosen element's version
    }

    /// <summary>A skill works at its unlock level; during a job trial the master lends the job's first skill.</summary>
    bool SkillUnlocked(PlayerEntity p, SkillDef s)
    {
        if (p.Rec.Job == s.job)
        {
            if (s.circle > 0) return p.Rec.Circle >= s.circle;     // 마법사: 서클이 오르면 그 서클의 마법을 배움
            return p.Rec.Level >= s.unlockLevel;
        }
        return p.TrialJob == s.job && s.slot == 0;
    }

    int SkillLevelOf(PlayerEntity p, SkillDef s)
    {
        foreach (var r in p.Rec.Skills) if (r.SkillId == s.id) return r.Level;
        return 1;
    }

    /// <summary>Spec vol.2 6.1: the skill gains a use when it lands; enough uses raise its level (+6% power per level).</summary>
    void AddSkillUse(PlayerEntity p, SkillDef s)
    {
        if (p.Rec.Job != s.job) return;   // the trial's lent skill does not train
        var rules = _d.skillRules;
        int idx = p.Rec.Skills.FindIndex(r => r.SkillId == s.id);
        var row = idx >= 0 ? p.Rec.Skills[idx] : new SkillRow(s.id, 1, 0);
        int level = row.Level, uses = row.Uses + 1;
        bool up = false;
        while (level < rules.maxLevel)
        {
            int need = rules.UsesToNext(level);
            if (need <= 0 || uses < need) break;
            uses -= need;
            level++;
            up = true;
        }
        if (level >= rules.maxLevel) uses = 0;
        row = row with { Level = level, Uses = uses };
        if (idx >= 0) p.Rec.Skills[idx] = row; else p.Rec.Skills.Add(row);
        p.SkillDirty = p.SaveDirty = true;
        if (up)
        {
            Gain(p, GainKind.SkillLevelUp, s.id, level);
            Audit(p, "skill_level", new { skill = s.id, level });
        }
    }

    // ───────────── using a skill ─────────────

    void OnUseSkill(PlayerEntity p, UseSkill m)
    {
        if (p.Dead || p.CannotAct(_now)) return;
        var s = SkillAt(p, m.Slot);
        if (s == null || !SkillUnlocked(p, s)) return;

        double ready = p.SkillReadyAt.TryGetValue(s.id, out var r) ? r : 0;
        if (_now < ready - _d.skillRules.cooldownTolerance)
        {
            p.Conn.Send(new SkillCooldown { SkillId = s.id, Seconds = (float)(ready - _now) }); // refused: tell the client what is left
            return;
        }
        // 마법사: a spell needs the chosen element first and costs mana (nothing is spent when it is refused).
        if (s.useElement && p.Rec.Job == s.job && string.IsNullOrEmpty(p.Rec.Element) && _d.Job(s.job).mage.enabled)
        {
            p.Conn.Send(new Notice { Kind = NoticeKind.Error, Text = "속성을 먼저 골라야 마법을 쓸 수 있어." });
            return;
        }
        if (s.manaCost > 0 && !TrySpendMana(p, s.manaCost)) return;

        float cd = s.cooldown * (1f - p.Stats.CdReduction);
        p.SkillReadyAt[s.id] = _now + cd;
        p.Conn.Send(new SkillCooldown { SkillId = s.id, Seconds = cd });

        CancelCharge(p);
        p.Move.Yaw = m.Yaw;
        p.LastCombatAt = _now;
        Broadcast(new SkillCast { EntityId = p.EntityId, SkillId = s.id, Yaw = m.Yaw }, except: p);

        var cast = new CastContext { Player = p, Skill = s, SkillLevel = SkillLevelOf(p, s) };
        var spec = new HitSpec { Coefficient = s.coefficient, Penetration = s.penetration, Cast = cast, Effects = s.effects };
        if (s.useElement)
        {
            spec.Element = ElementOf(p);
            var efx = _d.elements.Find(spec.Element)?.effect;
            if (efx != null && efx.type == "pierce") spec.Penetration += efx.amount;   // 금속 = 관통
        }
        float yaw = m.Yaw;

        switch (s.kind)
        {
            case "melee":
                for (int i = 0; i < Math.Max(1, s.hits); i++)
                {
                    if (i == 0) MeleeStrike(p, s, yaw, spec);
                    else Schedule(i * s.hitInterval, () => { if (Alive(p)) MeleeStrike(p, s, p.Move.Yaw, spec); });
                }
                break;

            case "circle":
                foreach (var t in MonstersInArc(p, p.Move.X, p.Move.Z, yaw, s.range, 360f, s.maxTargets)) HitMonster(p, t, spec);
                break;

            case "dash":
                DashStrike(p, s, yaw, spec);
                break;

            case "projectile":
                SpawnProjectile(p, s, yaw, spec, 0f);
                break;

            case "volley":
            {
                int n = Math.Max(2, s.projectiles);
                for (int i = 0; i < n; i++)
                    SpawnProjectile(p, s, yaw, spec, -s.spreadDeg * 0.5f + s.spreadDeg * i / (n - 1));
                break;
            }

            case "backstep":
                _validator.GrantSkillMove(p.Move, s.distance, _now);   // the client hops backwards by itself
                SpawnProjectile(p, s, yaw, spec, 0f);
                break;

            case "buff":
                foreach (var fx in s.effects)
                {
                    if (fx.type != "buff") continue;
                    p.Buffs.RemoveAll(b => b.SkillId == s.id && b.Stat == fx.stat);
                    p.Buffs.Add(new ActiveBuff { SkillId = s.id, Stat = fx.stat, Amount = fx.amount, Until = _now + s.duration });
                }
                cast.Landed = true;           // a buff cannot miss: it counts as one use
                AddSkillUse(p, s);
                break;

            case "stance":
                p.CounterUntil = _now + s.duration;
                p.CounterSkill = s;
                break;

            case "blink":
                ShadowStep(p, s, yaw, spec);
                break;

            case "ground":
            {
                GroundPoint(p, yaw, s.distance, out float gx, out float gz);
                Schedule(Math.Max(0.01f, s.delay), () => { if (Alive(p)) GroundStrike(p, s, gx, gz, spec); });
                break;
            }

            case "zone":
            {
                GroundPoint(p, yaw, s.distance, out float zx, out float zz);
                for (int i = 0; i < Math.Max(1, s.ticks); i++)
                    Schedule(Math.Max(0.01f, s.delay) + i * s.tickInterval, () => { if (Alive(p)) GroundStrike(p, s, zx, zz, spec); });
                break;
            }

            case "chain":
            {
                var first = MonstersInArc(p, p.Move.X, p.Move.Z, yaw, s.range, s.arcDeg, 1);
                if (first.Count == 0) break;
                var cs = spec; cs.NoChain = true;     // this spell does its own jumping
                HitMonster(p, first[0], cs);
                ChainJumps(p, first[0], cs, s.chainCount, s.chainRange, s.chainFalloff);
                break;
            }

            case "toggle":
                ToggleInfusion(p);
                break;

            case "taunt":
                Taunt(p, s, cast);
                break;

            case "partyBuff":
                PartyBuff(p, s, cast);
                break;

            case "heal":
                HealAimed(p, s, yaw, cast);
                break;

            case "groupHeal":
                GroupHeal(p, s, cast);
                break;

            case "revive":
                Revive(p, s, cast);
                break;

            case "shield":
                p.ShieldAmount = p.Stats.Attack * s.coefficient * (1f + _d.skillRules.powerPerLevel * (cast.SkillLevel - 1));
                p.ShieldUntil = _now + s.duration;
                cast.Landed = true;           // a shield cannot miss: it counts as one use
                AddSkillUse(p, s);
                break;
        }
    }

    // ───────────── 2b spell forms ─────────────

    /// <summary>The point `dist` meters ahead along yaw (ground spells have no pitch).</summary>
    void GroundPoint(PlayerEntity p, float yaw, float dist, out float x, out float z)
    {
        Yaw.ToDirection(yaw, out float dx, out float dz);
        x = p.Move.X + dx * dist; z = p.Move.Z + dz * dist;
        if (!_terrain.InBounds(x, z)) { x = p.Move.X; z = p.Move.Z; }
    }

    /// <summary>Everything within the radius of a point, nearest to the point first.</summary>
    void GroundStrike(PlayerEntity p, SkillDef s, float x, float z, HitSpec spec)
    {
        var found = new List<(MonsterEntity m, float d2)>();
        foreach (var m in _monsters.Values)
        {
            if (m.Dead || m.Ai == MonsterAi.Returning) continue;
            if (m.Owner != null && m.Owner != p) continue;
            if (Math.Abs(m.Y - p.Move.Y) > 4f) continue;
            float reach = s.radius + m.Def.bodyRadius;
            float d2 = Dist2(m.X, m.Z, x, z);
            if (d2 <= reach * reach) found.Add((m, d2));
        }
        found.Sort((a, b) => a.d2.CompareTo(b.d2));
        for (int i = 0; i < found.Count && i < s.maxTargets; i++) HitMonster(p, found[i].m, spec);
    }

    /// <summary>The player is moved by the server (그림자 걸음): set the position and tell the client to jump there.</summary>
    bool TeleportPlayer(PlayerEntity p, float x, float z)
    {
        if (!_terrain.InBounds(x, z)) return false;
        if (AreaLimitFor(p) is { } lim && Dist2(x, z, lim.x, lim.z) > lim.radius * lim.radius) return false;
        var a = p.Move;
        a.X = x; a.Z = z; a.Y = _terrain.Height(x, z);
        a.LastGroundY = a.Y;
        Correct(p, CorrectionReason.Teleport);
        return true;
    }

    /// <summary>그림자 걸음: appear 1.3 m behind the nearest target in front and stab it (후방 2배). No target: a short blink forward.</summary>
    void ShadowStep(PlayerEntity p, SkillDef s, float yaw, HitSpec spec)
    {
        var targets = MonstersInArc(p, p.Move.X, p.Move.Z, yaw, s.range, s.arcDeg, 1);
        if (targets.Count == 0)
        {
            Yaw.ToDirection(yaw, out float fx0, out float fz0);
            TeleportPlayer(p, p.Move.X + fx0 * s.distance, p.Move.Z + fz0 * s.distance);
            return;
        }
        var t = targets[0];
        Yaw.ToDirection(t.Yaw, out float fx, out float fz);        // where the target looks
        float nx = t.X - fx * 1.3f, nz = t.Z - fz * 1.3f;           // so this is right behind its back
        if (!TeleportPlayer(p, nx, nz)) return;
        p.Move.Yaw = Yaw.FromDirection(t.X - nx, t.Z - nz);
        HitMonster(p, t, spec);
    }

    void MeleeStrike(PlayerEntity p, SkillDef s, float yaw, HitSpec spec)
    {
        foreach (var t in MonstersInArc(p, p.Move.X, p.Move.Z, yaw, s.range, s.arcDeg, s.maxTargets)) HitMonster(p, t, spec);
    }

    /// <summary>발도 일섬: rush forward and cut everything on the way. The client moves itself; the server allows the extra distance and hits along the line.</summary>
    void DashStrike(PlayerEntity p, SkillDef s, float yaw, HitSpec spec)
    {
        _validator.GrantSkillMove(p.Move, s.distance, _now);
        Yaw.ToDirection(yaw, out float dx, out float dz);
        float sx = p.Move.X, sz = p.Move.Z;
        var hits = new List<(MonsterEntity m, float t)>();
        foreach (var m in _monsters.Values)
        {
            if (m.Dead || m.Ai == MonsterAi.Returning) continue;
            if (m.Owner != null && m.Owner != p) continue;
            float px = m.X - sx, pz = m.Z - sz;
            float t = px * dx + pz * dz;                       // distance along the dash
            if (t < -0.5f || t > s.distance + _data.Config.validation.hitRangeTolerance) continue;
            float side = MathF.Abs(px * dz - pz * dx);         // distance from the line
            if (side > s.radius + m.Def.bodyRadius) continue;
            if (Math.Abs(m.Y - p.Move.Y) > 3f) continue;
            hits.Add((m, t));
        }
        hits.Sort((a, b) => a.t.CompareTo(b.t));
        for (int i = 0; i < hits.Count && i < s.maxTargets; i++) HitMonster(p, hits[i].m, spec);
    }

    /// <summary>반격 자세 answered: strike the attacker (and anything around it).</summary>
    void CounterStrike(PlayerEntity p, SkillDef s, MonsterEntity attacker)
    {
        float yaw = Yaw.FromDirection(attacker.X - p.Move.X, attacker.Z - p.Move.Z);
        p.Move.Yaw = yaw;
        Broadcast(new SkillCast { EntityId = p.EntityId, SkillId = s.id, Yaw = yaw }, except: p);
        var cast = new CastContext { Player = p, Skill = s, SkillLevel = SkillLevelOf(p, s) };
        var spec = new HitSpec { Coefficient = s.coefficient, Penetration = s.penetration, Cast = cast, Effects = s.effects };
        var targets = MonstersInArc(p, p.Move.X, p.Move.Z, yaw, s.range, s.arcDeg, s.maxTargets);
        if (!targets.Contains(attacker) && !attacker.Dead) targets.Insert(0, attacker);
        foreach (var t in targets) HitMonster(p, t, spec);
    }

    // ───────────── projectiles ─────────────

    void SpawnProjectile(PlayerEntity p, SkillDef s, float yaw, HitSpec spec, float offsetYaw)
    {
        var job = EffectiveJob(p);
        bool holy = job != null && job.buffer.enabled;
        var kind = s.useElement || holy ? ProjectileKind.Magic
                 : job != null && job.weaponType == "dagger" ? ProjectileKind.Dagger
                 : job != null && (job.weaponType == "sword" || job.weaponType == "longsword") ? ProjectileKind.SwordWave : ProjectileKind.Arrow;
        byte tier = s.useElement ? ElementIndex(spec.Element ?? "none") : holy ? (byte)250 : (byte)0;
        int pierce = Math.Max(1, s.pierce);
        var efx = s.useElement ? _d.elements.Find(spec.Element)?.effect : null;
        if (efx != null && efx.type == "pierce") pierce += efx.count;                 // 금속 = 관통
        SpawnProjectile(p, yaw + offsetYaw, s.speed, s.range, s.radius, pierce, spec, kind, tier, s);
    }

    void SpawnProjectile(PlayerEntity p, float yaw, float speed, float range, float radius, int pierce,
                         HitSpec spec, ProjectileKind kind, byte tier, SkillDef skill)
    {
        Yaw.ToDirection(yaw, out float dx, out float dz);
        var pr = new ProjectileEntity
        {
            Id = _nextProjectileId++, Owner = p,
            X = p.Move.X + dx * 0.8f, Z = p.Move.Z + dz * 0.8f, Y = p.Move.Y + 1.3f,
            DirX = dx, DirZ = dz, Yaw = yaw, Speed = speed, Range = range, Radius = radius, Pierce = pierce,
            Coefficient = spec.Coefficient, Penetration = spec.Penetration, Skill = skill, Kind = kind, Cast = spec.Cast, Element = spec.Element,
        };
        _projectiles[pr.Id] = pr;
        Broadcast(new ProjectileSpawn { ProjectileId = pr.Id, OwnerId = p.EntityId, X = pr.X, Y = pr.Y, Z = pr.Z, Yaw = yaw, Speed = speed, Range = range, Kind = kind, Tier = tier });
    }

    readonly List<uint> _endedProjectiles = new();
    readonly List<(MonsterEntity m, float t)> _projHits = new();

    void UpdateProjectiles(float dt)
    {
        if (_projectiles.Count == 0) return;
        _endedProjectiles.Clear();
        var tol = _data.Config.combat.projectileHeightTolerance;
        foreach (var pr in _projectiles.Values)
        {
            if (!_players.ContainsKey(pr.Owner.EntityId)) { _endedProjectiles.Add(pr.Id); continue; }
            float step = Math.Min(pr.Speed * dt, pr.Range - pr.Traveled);
            float x0 = pr.X, z0 = pr.Z;
            float x1 = x0 + pr.DirX * step, z1 = z0 + pr.DirZ * step;

            _projHits.Clear();
            foreach (var m in _monsters.Values)
            {
                if (m.Dead || m.Ai == MonsterAi.Returning || pr.Hit.Contains(m.EntityId)) continue;
                if (m.Owner != null && m.Owner != pr.Owner) continue;
                if (Math.Abs(m.Y + 0.8f - pr.Y) > tol) continue;
                float px = m.X - x0, pz = m.Z - z0;
                float t = Math.Clamp(px * pr.DirX + pz * pr.DirZ, 0f, step);          // closest point along this tick's segment
                float cx = x0 + pr.DirX * t - m.X, cz = z0 + pr.DirZ * t - m.Z;
                float reach = pr.Radius + m.Def.bodyRadius;
                if (cx * cx + cz * cz > reach * reach) continue;
                _projHits.Add((m, t));
            }
            _projHits.Sort((a, b) => a.t.CompareTo(b.t));

            bool ended = false, hitAny = false;
            var spec = new HitSpec { Coefficient = pr.Coefficient, Penetration = pr.Penetration, Cast = pr.Cast, Effects = pr.Skill?.effects, Element = pr.Element };
            foreach (var (m, _) in _projHits)
            {
                if (!Alive(pr.Owner)) { ended = true; break; }
                pr.Hit.Add(m.EntityId);
                if (HitMonster(pr.Owner, m, spec)) hitAny = true;
                if (--pr.Pierce <= 0) { ended = true; break; }
            }
            pr.X = x1; pr.Z = z1; pr.Traveled += step;
            if (!ended && pr.Traveled >= pr.Range - 0.001f) ended = true;
            if (ended)
            {
                _endedProjectiles.Add(pr.Id);
                Broadcast(new ProjectileEnd { ProjectileId = pr.Id, Hit = hitAny });
            }
        }
        foreach (var id in _endedProjectiles) _projectiles.Remove(id);
    }
}
