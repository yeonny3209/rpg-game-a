using Game.Server.Logic;
using Game.Server.Net;
using Game.Shared.Data;
using Game.Shared.Protocol;
using Game.Shared.World;

namespace Game.Server.World;

/// <summary>
/// 마법사 (spec vol.2 5.2): mana, circles (서클), the chosen element and what the element adds to a spell.
/// Decisions 2026-11-08: the element is chosen once right after the job change (no change yet); no meditation yet.
/// </summary>
public sealed partial class Zone
{
    // ───────────── mana ─────────────

    void SendMana(PlayerEntity p)
    {
        p.LastManaSent = (int)p.Mana;
        p.Conn.Send(new ManaState { Mana = (int)p.Mana, MaxMana = p.Stats.MaxMana, Infused = p.Infused });
    }

    void FillMana(PlayerEntity p)
    {
        p.Mana = p.Stats.MaxMana;
        SendMana(p);
    }

    /// <returns>false (and tells the player) when there is not enough mana.</returns>
    bool TrySpendMana(PlayerEntity p, int cost)
    {
        if (cost <= 0) return true;
        if (p.Mana + 0.001f < cost)
        {
            p.Conn.Send(new Notice { Kind = NoticeKind.Error, Text = "마나가 부족해." });
            return false;
        }
        p.Mana -= cost;
        p.LastCombatAt = _now;
        SendMana(p);
        return true;
    }

    /// <summary>Mana comes back every tick (spec vol.2 2.2: 지능 → 마나 재생), faster out of combat. Sent a few times a second.</summary>
    void UpdateMana(float dt)
    {
        var fx = _d.character.stats;
        foreach (var p in _players.Values)
        {
            int max = p.Stats.MaxMana;
            if (p.Dead || max <= 0) continue;
            if (p.Mana < max)
            {
                float perSec = fx.manaRegenBase + p.Stats.ManaRegen;
                if (!p.InCombat(_now, _d.character.combatTimeoutSec)) perSec *= fx.manaRegenOutOfCombatMultiplier;
                p.Mana = Math.Min(max, p.Mana + perSec * dt);
            }
            else if (p.Mana > max) p.Mana = max;
            if (_tick % 4 == 0 && (int)p.Mana != p.LastManaSent) SendMana(p);
            p.Rec.Mana = (int)p.Mana;
        }
    }

    // ───────────── circles (서클 승급표, spec vol.2 5.2) ─────────────

    /// <summary>
    /// Raises the circle while level and max mana allow it (1~4 are automatic; 5+ need a battle exam, a later stage).
    /// Runs whenever stats change (level up, stat points, job change).
    /// </summary>
    void CheckCircle(PlayerEntity p)
    {
        var job = _d.Job(p.Rec.Job);
        if (job == null || !job.mage.enabled) return;
        bool up = false;
        while (true)
        {
            var next = job.mage.Circle(p.Rec.Circle + 1);
            if (next == null || !next.available || next.trial != "auto") break;
            if (p.Rec.Level < next.level || p.Stats.MaxMana < next.maxMana) break;
            p.Rec.Circle = next.circle;
            up = true;
            Gain(p, GainKind.CircleUp, "", next.circle);
            Audit(p, "circle_up", new { circle = next.circle, level = p.Rec.Level, maxMana = p.Stats.MaxMana });
        }
        if (up) { p.SkillDirty = p.SaveDirty = true; p.SaveNow = true; }
    }

    // ───────────── element ─────────────

    /// <summary>The element a spell takes on: the chosen one, or none (trial, or before choosing).</summary>
    string ElementOf(PlayerEntity p) => string.IsNullOrEmpty(p.Rec.Element) ? "none" : p.Rec.Element;

    byte ElementIndex(string id)
    {
        var list = _d.elements.list;
        for (int i = 0; i < list.Length; i++) if (list[i].id == id) return (byte)i;
        return 255;
    }

    void OnChooseElement(PlayerEntity p, ChooseElement m)
    {
        var job = _d.Job(p.Rec.Job);
        if (job == null || !job.mage.enabled) return;
        if (!string.IsNullOrEmpty(p.Rec.Element)) return;                          // chosen once, no change yet (결정 2026-11-08)
        if (Array.IndexOf(job.mage.elements, m.ElementId) < 0) return;
        p.Rec.Element = m.ElementId;
        p.SkillDirty = p.SaveDirty = true;
        p.SaveNow = true;
        Audit(p, "element_chosen", new { element = m.ElementId });
        Log.Info($"element #{p.EntityId} '{p.Name}' → {m.ElementId}");
    }

    // ───────────── what the element adds (불=화상, 얼음=둔화, 물=넉백, 번개=연쇄, 식물=속박, 금속=관통) ─────────────

    void ApplyElementEffect(PlayerEntity p, MonsterEntity m, in HitSpec h, int dmg)
    {
        if (m.Dead || m.Def.dummy) return;
        var fx = _d.elements.Find(h.Element)?.effect;
        if (fx == null) return;
        switch (fx.type)
        {
            case "burn":
                m.BurnUntil = _now + fx.duration;
                m.BurnNextTick = _now + 1.0;
                m.BurnTickDamage = Math.Max(1, (int)Math.Round(dmg * fx.amount));
                m.BurnOwner = p;
                break;
            case "slow":
                ApplyCcToMonster(p, m, new SkillEffect { type = "cc", cc = "slow", duration = fx.duration, chance = 1f });
                break;
            case "root":
                ApplyCcToMonster(p, m, new SkillEffect { type = "cc", cc = "root", duration = fx.duration, chance = 1f });
                break;
            case "knockback":
                ApplyCcToMonster(p, m, new SkillEffect { type = "cc", cc = "knockback", distance = fx.distance, chance = 1f });
                break;
            case "chain":
                if (h.NoChain) break;
                ChainJumps(p, m, h, fx.count, fx.distance, fx.amount);
                break;
        }
    }

    /// <summary>Jumps from `from` to the nearest other monsters (each jump weaker by `falloff`).</summary>
    void ChainJumps(PlayerEntity p, MonsterEntity from, in HitSpec h, int count, float range, float falloff)
    {
        var spec = h;
        spec.NoChain = true;          // a jump never starts another chain
        spec.Cast = null;             // mastery was already counted by the first hit
        spec.Effects = null;
        var hit = new HashSet<uint> { from.EntityId };
        var cur = from;
        float coef = h.Coefficient;
        for (int i = 0; i < count; i++)
        {
            MonsterEntity best = null;
            float bestD2 = range * range;
            foreach (var m in _monsters.Values)
            {
                if (m.Dead || m.Ai == MonsterAi.Returning || hit.Contains(m.EntityId)) continue;
                if (m.Owner != null && m.Owner != p) continue;
                float d2 = Dist2(m.X, m.Z, cur.X, cur.Z);
                if (d2 < bestD2) { bestD2 = d2; best = m; }
            }
            if (best == null) break;
            hit.Add(best.EntityId);
            coef *= falloff;
            spec.Coefficient = coef;
            HitMonster(p, best, spec);
            cur = best;
        }
    }

    // ───────────── burn (화상) ─────────────

    /// <summary>화상: a tick of damage every second until it runs out. Runs inside the monster update.</summary>
    void BurnTick(MonsterEntity m)
    {
        if (m.BurnUntil <= 0) return;
        if (m.Dead || m.BurnOwner == null || !_players.ContainsKey(m.BurnOwner.EntityId)) { m.BurnUntil = 0; return; }
        while (_now >= m.BurnNextTick && m.BurnNextTick <= m.BurnUntil + 0.001)
        {
            m.BurnNextTick += 1.0;
            DamageMonsterDirect(m.BurnOwner, m, m.BurnTickDamage, HitFlags.Burn);
            if (m.Dead) { m.BurnUntil = 0; return; }
        }
        if (_now > m.BurnUntil) m.BurnUntil = 0;
    }
}
