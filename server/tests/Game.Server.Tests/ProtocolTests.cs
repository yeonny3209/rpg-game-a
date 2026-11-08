using Game.Shared.Protocol;
using Xunit;

namespace Game.Server.Tests;

public class ProtocolTests
{
    static NetReader Body(INetMessage m, out MsgId id)
    {
        var bytes = Packet.Encode(m);
        var r = new NetReader(bytes);
        id = Packet.PeekId(r);
        return r;
    }

    [Fact]
    public void Hello_roundtrip_keeps_korean_name()
    {
        var r = Body(new Hello { ProtocolVersion = ProtocolInfo.Version, Name = "세윤", Password = "비밀 pw 12", Create = true }, out var id);
        Assert.Equal(MsgId.Hello, id);
        var h = Hello.Read(r);
        Assert.Equal("세윤", h.Name);
        Assert.Equal("비밀 pw 12", h.Password);
        Assert.True(h.Create);
        Assert.Equal(ProtocolInfo.Version, h.ProtocolVersion);
    }

    [Fact]
    public void MoveState_roundtrip_quantizes_to_cm()
    {
        var src = new MoveState { Seq = 42, CorrectionEpoch = 3, X = 12.345f, Y = -1.2f, Z = 99.999f, Yaw = 271.5f, Flags = MoveFlags.Gliding | MoveFlags.DashStart, JumpCount = 2 };
        var m = MoveState.Read(Body(src, out _));
        Assert.Equal(42u, m.Seq);
        Assert.Equal(3, m.CorrectionEpoch);
        Assert.Equal(12.35f, m.X, 2);
        Assert.Equal(-1.2f, m.Y, 2);
        Assert.Equal(100.0f, m.Z, 2);
        Assert.Equal(271.5f, m.Yaw, 1);
        Assert.Equal(src.Flags, m.Flags);
        Assert.Equal(2, m.JumpCount);
    }

    [Fact]
    public void Snapshot_roundtrip()
    {
        var s = new Snapshot { Tick = 7, ServerTime = 123.456, Count = 2, Entities = new[]
        {
            new EntityState { EntityId = 1, X = 1, Y = 2, Z = 3, Yaw = 90, Flags = MoveFlags.Grounded },
            new EntityState { EntityId = 2, X = -4, Y = 5, Z = -6, Yaw = 180, Flags = MoveFlags.Gliding },
        }};
        var r = Snapshot.Read(Body(s, out _));
        Assert.Equal(2, r.Count);
        Assert.Equal(123.456, r.ServerTime, 6);
        Assert.Equal(-6f, r.Entities[1].Z, 2);
        Assert.Equal(MoveFlags.Gliding, r.Entities[1].Flags);
    }

    [Fact]
    public void Truncated_packet_throws_protocol_exception()
    {
        var bytes = Packet.Encode(new MoveState { Seq = 1 });
        var r = new NetReader(bytes, 0, bytes.Length - 3);
        Packet.PeekId(r);
        Assert.Throws<ProtocolException>(() => MoveState.Read(r));
    }
}
