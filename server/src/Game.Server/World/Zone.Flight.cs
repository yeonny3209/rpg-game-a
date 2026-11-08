using Game.Server.Logic;
using Game.Server.Net;
using Game.Shared.Data;
using Game.Shared.Protocol;
using Game.Shared.World;

namespace Game.Server.World;

/// <summary>
/// 비행 (spec vol.4 1.5): 마법사·궁수 can fly for a while (10 s, cooldown 30 s, speed ×1.4). You can attack while flying,
/// and getting hit shoots you down with fall damage. The client flies itself; the server checks height / speed in MovementValidator.
/// </summary>
public sealed partial class Zone
{
    FlightRules FlightOf(PlayerEntity p)
    {
        var job = _d.Job(p.Rec.Job);                       // only the real job: not lent during a trial
        return job != null && job.flight != null && job.flight.enabled ? job.flight : null;
    }

    bool Flying(PlayerEntity p) => _now < p.Move.FlyUntil;

    void SendFlight(PlayerEntity p, bool active, bool fell)
    {
        float cd = (float)Math.Max(0, p.FlyReadyAt - _now);
        Broadcast(new FlightState { EntityId = p.EntityId, Active = active, Fell = fell, Duration = active ? (float)(p.Move.FlyUntil - _now) : 0f, CooldownLeft = cd });
    }

    void OnStartFlight(PlayerEntity p)
    {
        var f = FlightOf(p);
        if (f == null || p.Dead || p.CannotAct(_now)) return;

        if (Flying(p))                                     // pressing C again lands early (the cooldown keeps running)
        {
            p.Move.FlyUntil = _now;
            p.Move.FlightGraceUntil = _now + f.graceSec;
            SendFlight(p, false, false);
            return;
        }
        if (_now < p.FlyReadyAt)
        {
            p.Conn.Send(new Notice { Kind = NoticeKind.Info, Text = $"비행은 {Math.Ceiling(p.FlyReadyAt - _now):0}초 뒤에 다시 쓸 수 있어." });
            return;
        }
        CancelCharge(p);
        p.Move.FlyUntil = _now + f.duration;
        p.Move.FlightGraceUntil = p.Move.FlyUntil + f.graceSec;
        p.Move.FlightMaxHeight = f.maxHeight;
        p.Move.FlightSpeedMul = f.speedMultiplier;
        p.FlyReadyAt = _now + f.cooldown;
        SendFlight(p, true, false);
    }

    /// <summary>The flight time ran out: the client glides down; the height check stays relaxed for a while so the descent is not corrected.</summary>
    void UpdateFlight()
    {
        foreach (var p in _players.Values)
        {
            if (p.Move.FlyUntil <= 0 || _now < p.Move.FlyUntil) continue;
            p.Move.FlyUntil = 0;
            SendFlight(p, false, false);
        }
    }

    internal void TestFall(PlayerEntity p) => FallFromFlight(p);

    /// <summary>Hit while flying: shot down, and the height you fall from hurts (spec vol.4 1.5 제안).</summary>
    void FallFromFlight(PlayerEntity p)
    {
        var f = FlightOf(p);
        if (f == null || !Flying(p)) return;
        float altitude = Math.Max(0f, p.Move.Y - _terrain.Height(p.Move.X, p.Move.Z));
        p.Move.FlyUntil = 0;
        p.Move.FlightGraceUntil = _now + f.graceSec;
        SendFlight(p, false, true);
        int dmg = (int)Math.Round(p.Stats.MaxHp * f.fallDamagePerTenMeters * altitude / 10f);
        if (dmg <= 0 || p.Dead) return;
        p.Rec.Hp = Math.Max(0, p.Rec.Hp - dmg);
        p.SaveDirty = true;
        var flags = p.Rec.Hp <= 0 ? HitFlags.Kill | HitFlags.Fixed : HitFlags.Fixed;
        Broadcast(new CombatEvent { AttackerId = p.EntityId, TargetId = p.EntityId, Damage = dmg, Flags = flags, TargetHp = p.Rec.Hp, TargetMaxHp = p.Stats.MaxHp });
        if (p.Rec.Hp <= 0) Die(p, 0, "추락");
    }
}
