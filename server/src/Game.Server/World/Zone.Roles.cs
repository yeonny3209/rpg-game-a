using Game.Server.Logic;
using Game.Server.Net;
using Game.Shared.Data;
using Game.Shared.Protocol;
using Game.Shared.World;

namespace Game.Server.World;

/// <summary>
/// 서양 검사 (마나 주입), 전사 (도발), 버퍼 (신성력 · 파티 버프 · 치유 · 부활) — spec vol.2 5.1 / 5.4 / 5.6.
/// There is no party system yet: "the party" is every living player within the buffer's allyRange (decision 2026-11-08).
/// </summary>
public sealed partial class Zone
{
    // ───────────── 서양 검사: 마나 주입 ─────────────

    void ToggleInfusion(PlayerEntity p)
    {
        var job = _d.Job(p.Rec.Job);
        if (job == null || !job.infuse.enabled) return;
        p.Infused = !p.Infused;
        SendMana(p);
        p.Conn.Send(new Notice { Kind = NoticeKind.Info, Text = p.Infused ? "마나 주입 ON — 평타마다 마나를 쓰고 피해가 늘어난다." : "마나 주입 OFF" });
    }

    /// <summary>The damage multiplier of one infused swing (1 when off); pays the mana. Not enough mana turns the infusion off.</summary>
    float TakeInfusion(PlayerEntity p)
    {
        var job = _d.Job(p.Rec.Job);
        if (!p.Infused || job == null || !job.infuse.enabled) return 1f;
        int max = p.Stats.MaxMana;
        float cost = Math.Max(1f, MathF.Round(max * job.infuse.costRatio));
        if (p.Mana + 0.001f < cost)
        {
            p.Infused = false;
            SendMana(p);
            p.Conn.Send(new Notice { Kind = NoticeKind.Info, Text = "마나가 모자라 주입이 꺼졌어." });
            return 1f;
        }
        p.Mana -= cost;
        p.LastCombatAt = _now;
        SendMana(p);
        return 1f + max / job.infuse.divisor;
    }

    // ───────────── 전사: 도발 ─────────────

    void Taunt(PlayerEntity p, SkillDef s, CastContext cast)
    {
        int n = 0;
        foreach (var m in MonstersInArc(p, p.Move.X, p.Move.Z, p.Move.Yaw, s.range, 360f, Math.Max(1, s.maxTargets)))
        {
            if (m.Def.dummy || m.Owner != null) continue;
            m.Target = p;
            m.Ai = MonsterAi.Chase;
            m.TauntUntil = _now + s.duration;
            m.NextAttackAt = Math.Max(m.NextAttackAt, _now + 0.4);
            n++;
        }
        cast.Landed = true;
        if (n > 0) AddSkillUse(p, s);
    }

    // ───────────── 버퍼 ─────────────

    /// <summary>The temporary party: everyone alive within range (the caster included).</summary>
    List<PlayerEntity> Allies(PlayerEntity p, float range)
    {
        var list = new List<PlayerEntity>();
        float r2 = range * range;
        foreach (var o in _players.Values)
            if (!o.Dead && (o == p || Dist2(o.Move.X, o.Move.Z, p.Move.X, p.Move.Z) <= r2)) list.Add(o);
        return list;
    }

    float AllyRange(PlayerEntity p)
    {
        var job = _d.Job(p.Rec.Job) ?? _d.Job(p.TrialJob);
        return job != null && job.buffer.enabled ? job.buffer.allyRange : 40f;
    }

    float HolyMultiplier(PlayerEntity p)
    {
        var job = _d.Job(p.Rec.Job);
        return job != null && job.buffer.enabled ? job.buffer.Multiplier(p.Rec.Holy) : 1f;
    }

    void PartyBuff(PlayerEntity p, SkillDef s, CastContext cast)
    {
        var job = _d.Job(p.Rec.Job) ?? _d.Job(p.TrialJob);
        int cap = job != null && job.buffer.enabled ? Math.Max(1, job.buffer.stackCap) : 2;
        float mult = HolyMultiplier(p);
        foreach (var o in Allies(p, AllyRange(p)))
        {
            foreach (var fx in s.effects)
            {
                if (fx.type != "buff") continue;
                o.Buffs.RemoveAll(b => b.SkillId == s.id && b.Stat == fx.stat && b.Source == p.EntityId);
                // The same buff from a third buffer replaces the oldest one (spec vol.2 5.6 제안: 최대 2중첩).
                var others = o.Buffs.Where(b => b.SkillId == s.id && b.Stat == fx.stat && b.Until > _now).OrderBy(b => b.Until).ToList();
                while (others.Count >= cap) { o.Buffs.Remove(others[0]); others.RemoveAt(0); }
                o.Buffs.Add(new ActiveBuff { SkillId = s.id, Stat = fx.stat, Amount = fx.amount * mult, Until = _now + s.duration, Source = p.EntityId });
            }
            if (o != p) o.Conn.Send(new Notice { Kind = NoticeKind.Info, Text = $"{p.Name}의 {s.name}!" });
        }
        cast.Landed = true;
        AddSkillUse(p, s);
    }

    /// <summary>Heals `amount` (capped by the missing HP) and tells everyone: a green number.</summary>
    int HealPlayer(PlayerEntity src, PlayerEntity t, int amount)
    {
        if (t.Dead) return 0;
        int healed = Math.Max(0, Math.Min(amount, t.Stats.MaxHp - t.Rec.Hp));
        t.Rec.Hp += healed;
        t.CharDirty = t.SaveDirty = true;
        Broadcast(new CombatEvent { AttackerId = src.EntityId, TargetId = t.EntityId, Damage = healed, Flags = HitFlags.Heal, TargetHp = t.Rec.Hp, TargetMaxHp = t.Stats.MaxHp });
        return healed;
    }

    int HealAmount(PlayerEntity p, SkillDef s, CastContext cast)
    {
        float level = 1f + _d.skillRules.powerPerLevel * (cast.SkillLevel - 1);
        return (int)MathF.Round(p.Stats.Attack * s.coefficient * level * (1f + p.Stats.HealPower));
    }

    /// <summary>치유: the nearest ally in front (aim target) or yourself. Healing never changes aggro.</summary>
    void HealAimed(PlayerEntity p, SkillDef s, float yaw, CastContext cast)
    {
        PlayerEntity best = null;
        float bestD2 = float.MaxValue;
        float half = s.arcDeg * 0.5f + _data.Config.validation.hitArcToleranceDeg;
        foreach (var o in _players.Values)
        {
            if (o == p || o.Dead) continue;
            float d2 = Dist2(o.Move.X, o.Move.Z, p.Move.X, p.Move.Z);
            if (d2 > (s.range + 0.5f) * (s.range + 0.5f) || d2 >= bestD2) continue;
            if (Yaw.Delta(Yaw.FromDirection(o.Move.X - p.Move.X, o.Move.Z - p.Move.Z), yaw) > half) continue;
            best = o; bestD2 = d2;
        }
        HealPlayer(p, best ?? p, HealAmount(p, s, cast));
        cast.Landed = true;
        AddSkillUse(p, s);
    }

    void GroupHeal(PlayerEntity p, SkillDef s, CastContext cast)
    {
        int amount = HealAmount(p, s, cast);
        foreach (var o in Allies(p, s.range > 0 ? s.range : AllyRange(p))) HealPlayer(p, o, amount);
        cast.Landed = true;
        AddSkillUse(p, s);
    }

    /// <summary>부활: the nearest fallen ally within range gets up on the spot (spec vol.4 1.6).</summary>
    void Revive(PlayerEntity p, SkillDef s, CastContext cast)
    {
        PlayerEntity best = null;
        float bestD2 = s.range * s.range;
        foreach (var o in _players.Values)
        {
            if (o == p || !o.Dead) continue;
            float d2 = Dist2(o.Move.X, o.Move.Z, p.Move.X, p.Move.Z);
            if (d2 <= bestD2) { best = o; bestD2 = d2; }
        }
        if (best == null)
        {
            p.SkillReadyAt.Remove(s.id);         // nothing happened: no cooldown
            p.Conn.Send(new SkillCooldown { SkillId = s.id, Seconds = 0f });
            p.Conn.Send(new Notice { Kind = NoticeKind.Error, Text = "근처에 쓰러진 사람이 없어." });
            return;
        }
        best.Dead = false;
        best.Rec.Hp = Math.Max(1, (int)MathF.Round(best.Stats.MaxHp * s.coefficient));
        best.InvulnUntil = _now + 2.0;
        best.LastCombatAt = -999;
        best.CharDirty = best.SaveDirty = true;
        Broadcast(new CombatEvent { AttackerId = p.EntityId, TargetId = best.EntityId, Damage = 0, Flags = HitFlags.Heal, TargetHp = best.Rec.Hp, TargetMaxHp = best.Stats.MaxHp });
        best.Conn.Send(new HpUpdate { EntityId = best.EntityId, Hp = best.Rec.Hp, MaxHp = best.Stats.MaxHp });
        best.Conn.Send(new Notice { Kind = NoticeKind.Info, Text = $"{p.Name}이(가) 일으켜 줬어!" });
        cast.Landed = true;
        AddSkillUse(p, s);
        Audit(p, "revive", new { target = best.Name });
    }
}
