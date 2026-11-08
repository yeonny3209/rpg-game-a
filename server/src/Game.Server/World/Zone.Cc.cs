using Game.Shared.Data;
using Game.Shared.Protocol;
using Game.Shared.World;

namespace Game.Server.World;

/// <summary>Crowd control (spec vol.4 1.4): stun, freeze, root, knockback, slow. After a CC ends the target is immune for a short while; slow is exempt.</summary>
public sealed partial class Zone
{
    static CcType ParseCc(string s) => s switch
    {
        "stun" => CcType.Stun, "freeze" => CcType.Freeze, "root" => CcType.Root,
        "knockback" => CcType.Knockback, "slow" => CcType.Slow, _ => CcType.None,
    };

    // ───────────── monsters ─────────────

    void ApplyCcToMonster(PlayerEntity src, MonsterEntity m, SkillEffect fx)
    {
        if (m.Dead || m.Def.dummy) return;
        if (fx.chance < 1f && _rng.NextDouble() >= fx.chance) return;
        var type = ParseCc(fx.cc);
        if (type == CcType.None) return;
        var cfg = _data.Config.combat;
        if (type != CcType.Slow && _now < m.CcImmuneUntil) return;

        float dur = fx.duration;
        switch (type)
        {
            case CcType.Stun: m.StunUntil = Math.Max(m.StunUntil, _now + dur); m.CcImmuneUntil = m.StunUntil + cfg.ccImmunitySec; break;
            case CcType.Freeze: m.FrozenUntil = Math.Max(m.FrozenUntil, _now + dur); m.CcImmuneUntil = m.FrozenUntil + cfg.ccImmunitySec; break;
            case CcType.Root: m.RootUntil = Math.Max(m.RootUntil, _now + dur); m.CcImmuneUntil = m.RootUntil + cfg.ccImmunitySec; break;
            case CcType.Slow: m.SlowUntil = Math.Max(m.SlowUntil, _now + dur); break;
            case CcType.Knockback:
            {
                dur = cfg.knockbackStunSec;
                float dx = m.X - src.Move.X, dz = m.Z - src.Move.Z;
                float len = MathF.Sqrt(dx * dx + dz * dz);
                if (len < 0.01f) { Yaw.ToDirection(src.Move.Yaw, out dx, out dz); len = 1f; }
                float nx = m.X + dx / len * fx.distance, nz = m.Z + dz / len * fx.distance;
                if (_terrain.InBounds(nx, nz)) { m.X = nx; m.Z = nz; m.Y = _terrain.Height(nx, nz); }
                m.StunUntil = Math.Max(m.StunUntil, _now + dur);
                m.CcImmuneUntil = m.StunUntil + cfg.ccImmunitySec;
                break;
            }
        }
        Broadcast(new CcApplied { TargetId = m.EntityId, Type = type, Duration = dur });
    }

    // ───────────── players ─────────────

    /// <param name="srcX">Where the hit came from (for knockback direction).</param>
    void ApplyCcToPlayer(PlayerEntity p, string typeName, float duration, float distance, float srcX, float srcZ)
    {
        if (p.Dead) return;
        var type = ParseCc(typeName);
        if (type == CcType.None) return;
        var cfg = _data.Config.combat;
        if (type != CcType.Slow && _now < p.CcImmuneUntil) return;

        float dur = duration * (1f - p.Stats.CcResist);   // 정신 스탯의 CC 저항 (상한 50%)
        switch (type)
        {
            case CcType.Stun: p.StunUntil = Math.Max(p.StunUntil, _now + dur); p.CcImmuneUntil = p.StunUntil + cfg.ccImmunitySec; break;
            case CcType.Freeze: p.FrozenUntil = Math.Max(p.FrozenUntil, _now + dur); p.CcImmuneUntil = p.FrozenUntil + cfg.ccImmunitySec; break;
            case CcType.Root: p.RootUntil = Math.Max(p.RootUntil, _now + dur); p.CcImmuneUntil = p.RootUntil + cfg.ccImmunitySec; break;
            case CcType.Slow: p.SlowUntil = Math.Max(p.SlowUntil, _now + dur); break;
            case CcType.Knockback:
            {
                dur = cfg.knockbackStunSec;
                var a = p.Move;
                float dx = a.X - srcX, dz = a.Z - srcZ;
                float len = MathF.Sqrt(dx * dx + dz * dz);
                if (len < 0.01f) { dx = 0; dz = -1; len = 1f; }
                float nx = a.X + dx / len * distance, nz = a.Z + dz / len * distance;
                if (_terrain.InBounds(nx, nz) && (AreaLimitFor(p) is not { } lim || Dist2(nx, nz, lim.x, lim.z) <= lim.radius * lim.radius))
                {
                    a.X = nx; a.Z = nz; a.Y = _terrain.Height(nx, nz);
                    a.LastGroundY = a.Y;
                    Correct(p, CorrectionReason.Knockback);
                }
                p.StunUntil = Math.Max(p.StunUntil, _now + dur);
                p.CcImmuneUntil = p.StunUntil + cfg.ccImmunitySec;
                break;
            }
        }
        CancelCharge(p);   // 궁수: CC를 맞으면 차징이 끊긴다 (피격만으로는 안 끊김)
        Broadcast(new CcApplied { TargetId = p.EntityId, Type = type, Duration = dur });
    }

    void CancelCharge(PlayerEntity p)
    {
        if (p.ChargeStartedAt < 0) return;
        p.ChargeStartedAt = -1;
        p.Conn.Send(new ChargeCancel());
    }
}
