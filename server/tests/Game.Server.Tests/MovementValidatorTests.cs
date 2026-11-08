using Game.Server.World;
using Game.Shared.Data;
using Game.Shared.Protocol;
using Game.Shared.World;
using Xunit;

namespace Game.Server.Tests;

public class MovementValidatorTests
{
    readonly GameConfig _cfg = new();
    readonly MapTerrain _terrain;
    readonly MovementValidator _v;

    public MovementValidatorTests()
    {
        _terrain = new MapTerrain(new MapDef
        {
            sizeMeters = 256,
            terrain = new TerrainDef { hillHeight = 5, flats = new[] { new CircleDef { x = 0, z = 0, radius = 25 } } },
            // A 4 m house (+ 2.7 m roof) east of spawn, turned 90°, for the rooftop test.
            buildings = new[] { new BuildingDef { x = 10, z = 0, yaw = 90, width = 8, depth = 6, height = 4 } },
        });
        _v = new MovementValidator(_cfg, _terrain);
    }

    MoveAuthority Spawned(double now = 0)
    {
        var a = new MoveAuthority();
        _v.Init(a, 0, _terrain.Height(0, 0), 0, now);
        return a;
    }

    MoveState At(uint seq, float x, float z, MoveFlags flags = MoveFlags.Grounded, float? y = null, byte jumps = 0) => new()
    {
        Seq = seq, X = x, Z = z, Y = y ?? _terrain.Height(x, z), Flags = flags, JumpCount = jumps,
    };

    [Fact]
    public void Running_at_normal_speed_is_accepted()
    {
        var a = Spawned();
        float step = _cfg.movement.runSpeedMps * 0.05f; // 20 Hz
        for (uint i = 1; i <= 100; i++)
            Assert.Null(_v.Apply(a, At(i, step * i, 0), i * 0.05));
        Assert.Equal(step * 100, a.X, 2);
    }

    [Fact]
    public void Teleport_is_rejected_and_position_kept()
    {
        var a = Spawned();
        Assert.Null(_v.Apply(a, At(1, 0.3f, 0), 0.05));
        Assert.Equal(CorrectionReason.TooFast, _v.Apply(a, At(2, 40f, 0), 0.10));
        Assert.Equal(0.3f, a.X, 2);
    }

    [Fact]
    public void Running_twice_as_fast_is_rejected_within_a_second()
    {
        var a = Spawned();
        float step = _cfg.movement.runSpeedMps * 2 * 0.05f;
        CorrectionReason? hit = null;
        for (uint i = 1; i <= 20 && hit == null; i++)
            hit = _v.Apply(a, At(i, step * i, 0), i * 0.05);
        Assert.Equal(CorrectionReason.TooFast, hit);
    }

    [Fact]
    public void Dash_is_allowed_once_per_cooldown()
    {
        var a = Spawned();
        float d = _cfg.movement.dashDistance;
        Assert.Null(_v.Apply(a, At(1, d, 0, MoveFlags.Grounded | MoveFlags.DashStart), 0.15));
        // Second dash immediately → cooldown violation.
        Assert.Equal(CorrectionReason.DashCooldown, _v.Apply(a, At(2, d * 2, 0, MoveFlags.Grounded | MoveFlags.DashStart), 0.30));
        // After cooldown it works again.
        Assert.Null(_v.Apply(a, At(3, d * 2, 0, MoveFlags.Grounded | MoveFlags.DashStart), 0.15 + _cfg.movement.dashCooldown + 0.05));
    }

    [Fact]
    public void Double_jump_height_is_allowed_but_flying_is_not()
    {
        var a = Spawned();
        float g = _terrain.Height(0, 0);
        float apex = g + _cfg.movement.jumpHeight + _cfg.movement.doubleJumpHeight;
        Assert.Null(_v.Apply(a, At(1, 0, 0, MoveFlags.None, apex, 2), 0.5));
        Assert.Equal(CorrectionReason.TooHigh, _v.Apply(a, At(2, 0, 0, MoveFlags.None, apex + 5f, 2), 1.0));
    }

    [Fact]
    public void Standing_and_jumping_on_a_rooftop_is_allowed_but_not_beside_it()
    {
        // Walk up to the house wall (x = 10 - 3 = 7), then appear on the roof as if climbed by double jump.
        var a = Spawned();
        double t = 0;
        uint seq = 0;
        for (float x = 0.25f; x <= 6.5f; x += 0.25f) { t += 0.05; Assert.Null(_v.Apply(a, At(++seq, x, 0), t)); }
        float roof = _terrain.StandHeight(10, 0);
        Assert.True(roof > 6f, $"roof top {roof}");
        // Standing on the house top for a while, then jumping up from it.
        for (int i = 0; i < 10; i++) { t += 0.05; Assert.Null(_v.Apply(a, At(++seq, 8f, 0, MoveFlags.Grounded, roof - 1f), t + 1)); }
        t += 1;
        Assert.Null(_v.Apply(a, At(++seq, 8.2f, 0, MoveFlags.None, roof + 1.5f, 1), t + 0.3));
        // The same height far from any building is flying.
        var b = Spawned();
        Assert.Equal(CorrectionReason.TooHigh, _v.Apply(b, At(1, -0.2f, 0, MoveFlags.None, roof + 1.5f, 1), 0.3));
    }

    [Fact]
    public void Three_jumps_are_rejected()
    {
        var a = Spawned();
        Assert.Equal(CorrectionReason.TooManyJumps, _v.Apply(a, At(1, 0, 0, MoveFlags.None, null, 3), 0.05));
    }

    [Fact]
    public void Falling_through_the_ground_is_rejected()
    {
        var a = Spawned();
        Assert.Equal(CorrectionReason.UnderGround, _v.Apply(a, At(1, 0, 0, MoveFlags.None, _terrain.Height(0, 0) - 5f), 0.05));
    }

    [Fact]
    public void Leaving_the_map_is_rejected()
    {
        var a = Spawned();
        a.X = _terrain.HalfSize - 0.1f;
        Assert.Equal(CorrectionReason.OutOfBounds, _v.Apply(a, At(1, _terrain.HalfSize + 0.2f, 0), 0.5));
    }

    [Fact]
    public void Leaving_the_start_area_is_refused_while_locked()
    {
        var a = Spawned();
        var limit = new CircleDef { x = 0, z = 0, radius = 2 };
        Assert.Null(_v.Apply(a, At(1, 0.25f, 0), 0.05, limit));
        Assert.Null(_v.Apply(a, At(2, 0.5f, 0), 0.10, limit));
        // Way past the edge (2 m + 0.5 m grace) → refused, position kept.
        float x = 0.5f;
        CorrectionReason? r = null;
        for (uint i = 3; i < 40 && r == null; i++) { x += 0.25f; r = _v.Apply(a, At(i, x, 0), i * 0.05, limit); }
        Assert.Equal(CorrectionReason.AreaLocked, r);
        Assert.True(a.X <= 2.5f);
        // Without the limit the same move is fine.
        Assert.Null(_v.Apply(a, At(100, a.X + 0.25f, 0), 5.0));
    }
}
