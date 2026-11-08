using System.Net.WebSockets;
using System.Threading.Channels;
using Game.Server.World;
using Game.Shared.Protocol;

namespace Game.Server.Net;

public static class ServerClock
{
    static readonly System.Diagnostics.Stopwatch Sw = System.Diagnostics.Stopwatch.StartNew();
    /// <summary>Seconds since server start. All server-side timing uses this.</summary>
    public static double Now => Sw.Elapsed.TotalSeconds;
}

/// <summary>
/// One WebSocket connection. Receives on its own task and forwards messages to the Zone inbox;
/// sends from a dedicated writer task so the zone tick never blocks on a slow socket.
/// </summary>
public sealed class Session : IClientConnection
{
    const int MaxMessageBytes = 64 * 1024;
    const int MaxQueuedSends = 2000;

    static int _nextId;

    readonly WebSocket _ws;
    readonly Zone _zone;
    readonly int _maxMsgPerSec;
    readonly Channel<byte[]> _sendQueue = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    int _queued;
    int _rateCount;
    double _rateWindowStart;

    public int Id { get; } = Interlocked.Increment(ref _nextId);
    public string RemoteAddress { get; }
    public double LastReceivedAt { get; private set; } = ServerClock.Now;
    public bool Closed { get; private set; }

    public Session(WebSocket ws, Zone zone, string remoteAddress, int maxMessagesPerSecond)
    {
        _ws = ws;
        _zone = zone;
        RemoteAddress = remoteAddress;
        _maxMsgPerSec = maxMessagesPerSecond;
    }

    public void Send(INetMessage msg) => SendRaw(Packet.Encode(msg));

    public void SendRaw(byte[] bytes)
    {
        if (Closed) return;
        if (Interlocked.Increment(ref _queued) > MaxQueuedSends)
        {
            Log.Warn($"session {Id}: send queue overflow, closing");
            Close();
            return;
        }
        _sendQueue.Writer.TryWrite(bytes);
    }

    public void Close()
    {
        if (Closed) return;
        Closed = true;
        _sendQueue.Writer.TryComplete();
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var writer = Task.Run(() => WriteLoop(ct), ct);
        try
        {
            await ReadLoop(ct);
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (ProtocolException e) { Log.Warn($"session {Id}: protocol error: {e.Message}"); }
        catch (Exception e) { Log.Error($"session {Id}: {e}"); }
        finally
        {
            Close();
            _zone.Post(new ZoneInbound(this, ZoneInboundKind.Leave, null, ServerClock.Now));
            try { await writer; } catch { /* ignore */ }
            if (_ws.State == WebSocketState.Open)
            {
                try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
            }
        }
    }

    async Task ReadLoop(CancellationToken ct)
    {
        var buf = new byte[8192];
        using var acc = new MemoryStream();
        while (!ct.IsCancellationRequested && !Closed && _ws.State == WebSocketState.Open)
        {
            acc.SetLength(0);
            WebSocketReceiveResult res;
            do
            {
                res = await _ws.ReceiveAsync(buf, ct);
                if (res.MessageType == WebSocketMessageType.Close) return;
                acc.Write(buf, 0, res.Count);
                if (acc.Length > MaxMessageBytes) throw new ProtocolException("message too large");
            } while (!res.EndOfMessage);

            if (res.MessageType != WebSocketMessageType.Binary) throw new ProtocolException("text frames not allowed");

            LastReceivedAt = ServerClock.Now;
            // Flood guard: a normal client sends ~20 moves + a few actions per second.
            if (LastReceivedAt - _rateWindowStart >= 1.0) { _rateWindowStart = LastReceivedAt; _rateCount = 0; }
            if (++_rateCount > _maxMsgPerSec) throw new ProtocolException($"too many messages (> {_maxMsgPerSec}/s)");

            var bytes = acc.GetBuffer();
            var r = new NetReader(bytes, 0, (int)acc.Length);
            var id = Packet.PeekId(r);
            object msg;
            switch (id)
            {
                case MsgId.Ping:
                    // Answer immediately for accurate RTT; does not need the zone thread.
                    var p = Ping.Read(r);
                    Send(new Pong { ClientTime = p.ClientTime, ServerTime = ServerClock.Now });
                    continue;
                case MsgId.Hello: msg = Hello.Read(r); break;
                case MsgId.MoveState: msg = MoveState.Read(r); break;
                case MsgId.Attack: msg = Attack.Read(r); break;
                case MsgId.Interact: msg = Interact.Read(r); break;
                case MsgId.DialogueChoice: msg = DialogueChoice.Read(r); break;
                case MsgId.RespawnRequest: msg = RespawnRequest.Read(r); break;
                case MsgId.ChooseGender: msg = ChooseGender.Read(r); break;
                case MsgId.AllocateStats: msg = AllocateStats.Read(r); break;
                case MsgId.UseSkill: msg = UseSkill.Read(r); break;
                case MsgId.ChargeStart: msg = ChargeStart.Read(r); break;
                case MsgId.ChargeRelease: msg = ChargeRelease.Read(r); break;
                case MsgId.ChatSend: msg = ChatSend.Read(r); break;
                case MsgId.ChooseElement: msg = ChooseElement.Read(r); break;
                case MsgId.EquipItem: msg = EquipItem.Read(r); break;
                case MsgId.UnequipItem: msg = UnequipItem.Read(r); break;
                case MsgId.UseItem: msg = UseItem.Read(r); break;
                case MsgId.ExpandBag: msg = ExpandBag.Read(r); break;
                case MsgId.ShopBuy: msg = ShopBuy.Read(r); break;
                case MsgId.ShopSell: msg = ShopSell.Read(r); break;
                case MsgId.StartFlight: msg = StartFlight.Read(r); break;
                default:
                    throw new ProtocolException($"unknown message id {(ushort)id}");
            }
            _zone.Post(new ZoneInbound(this, ZoneInboundKind.Message, msg, LastReceivedAt));
        }
    }

    async Task WriteLoop(CancellationToken ct)
    {
        try
        {
            await foreach (var bytes in _sendQueue.Reader.ReadAllAsync(ct))
            {
                Interlocked.Decrement(ref _queued);
                if (_ws.State != WebSocketState.Open) break;
                await _ws.SendAsync(bytes, WebSocketMessageType.Binary, true, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally { Close(); }
    }
}

public static class Log
{
    static string Ts => DateTime.Now.ToString("HH:mm:ss.fff");
    public static void Info(string m) => Console.WriteLine($"[{Ts}] {m}");
    public static void Warn(string m) => Console.WriteLine($"[{Ts}] WARN {m}");
    public static void Error(string m) => Console.WriteLine($"[{Ts}] ERROR {m}");
}
