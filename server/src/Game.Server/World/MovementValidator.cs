using Game.Shared.Data;
using Game.Shared.Protocol;
using Game.Shared.World;

namespace Game.Server.World;

/// <summary>Per-player movement state the server trusts.</summary>
public sealed class MoveAuthority
{
    public float X, Y, Z, Yaw;
    public MoveFlags Flags;
    public double LastTime;
    /// <summary>Horizontal meters the player may still travel (refilled over time).</summary>
    public float Budget;
    public float DashBonus;
    public double DashBonusUntil;
    public float LastGroundY;
    public double DashReadyAt;
    public double DodgeReadyAt;
    /// <summary>비행: until FlyUntil the player may be high and fast; until FlightGraceUntil the height check stays relaxed so the fall is not "TooHigh".</summary>
    public double FlyUntil, FlightGraceUntil;
    public float FlightMaxHeight = 25f, FlightSpeedMul = 1.4f;
    public byte Epoch;
    /// <summary>True when the last accepted move started a dodge roll (the zone then grants the invulnerable moment).</summary>
    public bool Dodged;
    public uint LastSeq;
    public int Violations;
}

/// <summary>
/// Validates client-predicted movement (doc 02 §2.2): max speed incl. dash/glide, dash cooldown,
/// jump apex, terrain floor, bounds. Pure logic so it can be unit-tested without sockets.
/// </summary>
public sealed class MovementValidator
{
    readonly GameConfig _cfg;
    readonly MapTerrain _terrain;

    public MovementValidator(GameConfig cfg, MapTerrain terrain)
    {
        _cfg = cfg;
        _terrain = terrain;
    }

    public float MaxHorizontalSpeed(float moveSpeedStat = -1)
    {
        var m = _cfg.movement;
        float stat = moveSpeedStat > 0 ? moveSpeedStat : m.baseMoveSpeedStat;
        float run = m.runSpeedMps * (stat / m.baseMoveSpeedStat);
        return Math.Max(run, m.glideSpeedMps);
    }

    public void Init(MoveAuthority a, float x, float y, float z, double now)
    {
        a.X = x; a.Y = y; a.Z = z;
        a.LastTime = now;
        a.Budget = 0;
        a.LastGroundY = y;
        a.DashReadyAt = 0;
        a.Flags = MoveFlags.Grounded;
    }

    /// <returns>null when accepted; otherwise the reason (the authority keeps the last valid position).</returns>
    /// <param name="areaLimit">If set, positions outside this circle are refused (start area before the first quest).</param>
    public CorrectionReason? Apply(MoveAuthority a, MoveState s, double now, CircleDef areaLimit = null)
    {
        var m = _cfg.movement;
        var v = _cfg.validation;

        double dt = Math.Clamp(now - a.LastTime, 0, 1.0);
        a.LastTime = now;

        float baseSpeed = MaxHorizontalSpeed();
        if (now <= a.FlyUntil) baseSpeed = Math.Max(baseSpeed, m.runSpeedMps * a.FlightSpeedMul);
        float maxSpeed = baseSpeed * v.speedTolerance;
        float cap = maxSpeed * v.budgetWindowSec;
        a.Budget = Math.Min(a.Budget + (float)(maxSpeed * dt), cap);
        if (now > a.DashBonusUntil) a.DashBonus = 0;

        if (!_terrain.InBounds(s.X, s.Z)) return CorrectionReason.OutOfBounds;
        if (areaLimit != null)
        {
            float ax = s.X - areaLimit.x, az = s.Z - areaLimit.z;
            // Small grace: the client clamps at the edge, rounding can put it a hair outside.
            if (ax * ax + az * az > (areaLimit.radius + 0.5f) * (areaLimit.radius + 0.5f)) return CorrectionReason.AreaLocked;
        }
        if (s.JumpCount > m.maxJumps) return CorrectionReason.TooManyJumps;

        if ((s.Flags & MoveFlags.DashStart) != 0)
        {
            // Small grace for packet timing.
            if (now < a.DashReadyAt - 0.1) return CorrectionReason.DashCooldown;
            a.DashReadyAt = now + m.dashCooldown;
            a.DashBonus = m.dashDistance * v.speedTolerance;
            a.DashBonusUntil = now + m.dashDuration + v.budgetWindowSec;
        }

        // Right-mouse dodge roll: its own cooldown, and the same kind of extra distance as a dash.
        bool dodged = false;
        if ((s.Flags & MoveFlags.DodgeStart) != 0)
        {
            if (now < a.DodgeReadyAt - 0.1) return CorrectionReason.DashCooldown;
            a.DodgeReadyAt = now + m.dodgeCooldown;
            a.DashBonus += m.dodgeDistance * v.speedTolerance;
            a.DashBonusUntil = Math.Max(a.DashBonusUntil, now + m.dodgeDuration + v.budgetWindowSec);
            dodged = true;
        }

        float dx = s.X - a.X, dz = s.Z - a.Z;
        float dist = MathF.Sqrt(dx * dx + dz * dz);
        if (dist > a.Budget + a.DashBonus + 0.05f) return CorrectionReason.TooFast;

        float ground = _terrain.Height(s.X, s.Z);
        if (s.Y < ground - v.underGroundTolerance) return CorrectionReason.UnderGround;

        // Jump height is measured from what you can stand on, so rooftops count as ground here.
        float stand = _terrain.StandHeight(s.X, s.Z);
        bool nearGround = s.Y <= stand + 0.3f;
        float baseY = Math.Max(a.LastGroundY, stand);
        float apex = baseY + m.jumpHeight + (m.maxJumps > 1 ? m.doubleJumpHeight * (m.maxJumps - 1) : 0) + v.heightTolerance;
        if (now <= a.FlightGraceUntil) apex = ground + a.FlightMaxHeight + v.heightTolerance;   // flying, or still coming down from a flight
        if (s.Y > apex) return CorrectionReason.TooHigh;

        // Accept. Dash bonus is consumed first.
        float fromDash = Math.Min(dist, a.DashBonus);
        a.DashBonus -= fromDash;
        a.Budget = Math.Max(0, a.Budget - (dist - fromDash));
        a.X = s.X; a.Y = s.Y; a.Z = s.Z; a.Yaw = s.Yaw;
        a.Flags = s.Flags & ~(MoveFlags.DashStart | MoveFlags.JumpStart | MoveFlags.DodgeStart);
        a.LastSeq = s.Seq;
        a.Dodged = dodged;
        if (nearGround || (s.Flags & MoveFlags.Grounded) != 0) a.LastGroundY = Math.Max(ground, Math.Min(s.Y, stand + 0.3f));
        return null;
    }

    /// <summary>A skill that moves the player (dash, back step): the client moves itself, so allow that much extra distance.</summary>
    public void GrantSkillMove(MoveAuthority a, float distance, double now)
    {
        a.DashBonus += distance * _cfg.validation.speedTolerance;
        a.DashBonusUntil = Math.Max(a.DashBonusUntil, now + 0.8);
    }
}
