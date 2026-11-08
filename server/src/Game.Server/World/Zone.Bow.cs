using Game.Shared.Data;
using Game.Shared.Protocol;

namespace Game.Server.World;

/// <summary>
/// Archer (spec vol.2 5.5): hold the button to draw the bow, release to shoot. The server times the hold itself.
/// 즉발 100% / 반차징 200% (1초) / 풀차징 300% (2초, 방어 20% 관통), 탄창 5발, 5발마다 2.5초 장전.
/// </summary>
public sealed partial class Zone
{
    /// <summary>Network timing allowance for the shot interval, in seconds.</summary>
    const double ShotIntervalGrace = 0.15;

    BowRules BowOf(PlayerEntity p)
    {
        var job = EffectiveJob(p);
        return job != null && job.bow != null && job.bow.enabled ? job.bow : null;
    }

    bool HasBowEquipped(PlayerEntity p)
    {
        var job = EffectiveJob(p);
        var weapon = p.TrialJob != null ? _d.Item(job?.starterWeapon) : p.Inv.Weapon;
        return weapon != null && weapon.weaponType == "bow";
    }

    /// <summary>Fill the magazine (job change, login, trial start / end).</summary>
    void ResetAmmo(PlayerEntity p)
    {
        var bow = BowOf(p);
        p.Ammo = bow?.magazine ?? 0;
        p.ReloadEndsAt = 0;
        p.ChargeStartedAt = -1;
        p.NextShotAt = 0;
        SendAmmo(p);
    }

    void SendAmmo(PlayerEntity p)
    {
        var bow = BowOf(p);
        if (bow == null) return;
        float reload = p.ReloadEndsAt > _now ? (float)(p.ReloadEndsAt - _now) : 0f;
        p.Conn.Send(new AmmoState { Ammo = (byte)Math.Clamp(p.Ammo, 0, 255), Max = (byte)bow.magazine, ReloadIn = reload });
    }

    void UpdateReload()
    {
        foreach (var p in _players.Values)
        {
            if (p.ReloadEndsAt <= 0 || _now < p.ReloadEndsAt) continue;
            var bow = BowOf(p);
            p.ReloadEndsAt = 0;
            p.Ammo = bow?.magazine ?? 0;
            SendAmmo(p);
        }
    }

    void OnChargeStart(PlayerEntity p)
    {
        if (p.Dead || p.CannotAct(_now)) return;
        var bow = BowOf(p);
        if (bow == null) return;
        if (!HasBowEquipped(p)) { p.Conn.Send(new Notice { Kind = NoticeKind.Error, Text = "활을 장착해야 쏠 수 있어." }); return; }
        if (p.Ammo <= 0 || _now < p.ReloadEndsAt) return;
        p.ChargeStartedAt = _now;
    }

    void OnChargeRelease(PlayerEntity p, ChargeRelease m)
    {
        if (p.ChargeStartedAt < 0) return;
        float hold = (float)(_now - p.ChargeStartedAt);
        p.ChargeStartedAt = -1;
        var bow = BowOf(p);
        if (bow == null || p.Dead || p.CannotAct(_now)) return;
        if (p.Ammo <= 0 || _now < p.ReloadEndsAt) return;
        // The previous shot's interval is not over yet (small tolerance for network timing): the shot is dropped.
        if (_now < p.NextShotAt - ShotIntervalGrace) { p.Conn.Send(new ChargeCancel()); return; }

        // 집중 차징 etc.: charging goes faster, so the same hold reaches a higher tier.
        float rate = p.BuffValue("chargeRate", _now);
        hold *= rate > 0 ? rate : 1f;
        int idx = 0;
        for (int i = 0; i < bow.tiers.Length; i++) if (hold >= bow.tiers[i].minHold) idx = i;
        var tier = bow.tiers[idx];

        float coef = tier.coefficient;
        float interval = bow.shotInterval;
        if (idx == 0)   // 속사: only the instant shot gets faster (and stronger)
        {
            float ic = p.BuffValue("instantCoefficient", _now), ii = p.BuffValue("instantInterval", _now);
            if (ic > 0) coef = ic;
            if (ii > 0) interval = ii;
        }
        p.NextShotAt = _now + interval / Math.Max(0.1f, p.Stats.AttackSpeed);
        p.LastCombatAt = _now;
        p.Move.Yaw = m.Yaw;
        Broadcast(new ActionEvent { EntityId = p.EntityId, Action = ActionKind.Attack, Yaw = m.Yaw }, except: p);

        p.Ammo--;
        if (p.Ammo <= 0) p.ReloadEndsAt = _now + bow.reloadSec;
        SendAmmo(p);

        var spec = new HitSpec { Coefficient = coef, Penetration = tier.penetration };
        SpawnProjectile(p, m.Yaw, bow.projectileSpeed, bow.range, bow.radius, 1, spec, ProjectileKind.Arrow, (byte)idx, null);
    }
}
