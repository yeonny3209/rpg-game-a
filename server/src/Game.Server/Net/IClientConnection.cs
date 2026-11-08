using Game.Shared.Protocol;

namespace Game.Server.Net;

/// <summary>What the zone needs from a client connection. Session (WebSocket) in real runs, a fake in tests.</summary>
public interface IClientConnection
{
    int Id { get; }
    string RemoteAddress { get; }
    double LastReceivedAt { get; }
    bool Closed { get; }
    void Send(INetMessage msg);
    void SendRaw(byte[] bytes);
    void Close();
}
