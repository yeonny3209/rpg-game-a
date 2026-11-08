using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Game.Client.Net
{
    public enum TransportState { Connecting, Open, Closed }

    /// <summary>Binary message transport. Poll-based so it behaves the same in WebGL (single thread) and the editor.</summary>
    public interface ITransport : IDisposable
    {
        TransportState State { get; }
        void Connect(string url);
        void Send(byte[] data);
        /// <summary>Returns the next received message, or null if none.</summary>
        byte[] Poll();
    }

    public static class TransportFactory
    {
        public static ITransport Create()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return new WebGLTransport();
#else
            return new DotNetTransport();
#endif
        }
    }

#if UNITY_WEBGL && !UNITY_EDITOR
    /// <summary>Browser WebSocket through Plugins/WebGL/websocket.jslib.</summary>
    public sealed class WebGLTransport : ITransport
    {
        [DllImport("__Internal")] static extern int WsCreate(string url);
        [DllImport("__Internal")] static extern int WsState(int id);
        [DllImport("__Internal")] static extern int WsSend(int id, byte[] data, int len);
        [DllImport("__Internal")] static extern int WsPeekSize(int id);
        [DllImport("__Internal")] static extern int WsReceive(int id, byte[] buffer, int maxLen);
        [DllImport("__Internal")] static extern void WsClose(int id);

        int _id = -1;

        public TransportState State
        {
            get
            {
                if (_id < 0) return TransportState.Closed;
                switch (WsState(_id))
                {
                    case 0: return TransportState.Connecting;
                    case 1: return TransportState.Open;
                    default: return TransportState.Closed;
                }
            }
        }

        public void Connect(string url) { _id = WsCreate(url); }

        public void Send(byte[] data)
        {
            if (_id >= 0) WsSend(_id, data, data.Length);
        }

        public byte[] Poll()
        {
            if (_id < 0) return null;
            int size = WsPeekSize(_id);
            if (size < 0) return null;
            var buf = new byte[size];
            int n = WsReceive(_id, buf, size);
            return n == size ? buf : null;
        }

        public void Dispose()
        {
            if (_id >= 0) WsClose(_id);
            _id = -1;
        }
    }
#endif

    /// <summary>Editor / standalone transport using System.Net.WebSockets on background tasks.</summary>
    public sealed class DotNetTransport : ITransport
    {
        ClientWebSocket _ws;
        CancellationTokenSource _cts;
        readonly ConcurrentQueue<byte[]> _inbox = new ConcurrentQueue<byte[]>();
        readonly BlockingCollection<byte[]> _outbox = new BlockingCollection<byte[]>();
        volatile TransportState _state = TransportState.Closed;

        public TransportState State => _state;

        public void Connect(string url)
        {
            _state = TransportState.Connecting;
            _ws = new ClientWebSocket();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            Task.Run(async () =>
            {
                try
                {
                    await _ws.ConnectAsync(new Uri(url), ct);
                    _state = TransportState.Open;
                    var sender = Task.Run(() => SendLoop(ct));
                    await ReceiveLoop(ct);
                }
                catch (Exception) { /* fallthrough to closed */ }
                finally { _state = TransportState.Closed; }
            });
        }

        async Task ReceiveLoop(CancellationToken ct)
        {
            var buf = new byte[1 << 16];
            while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                int len = 0;
                WebSocketReceiveResult res;
                do
                {
                    if (len == buf.Length) Array.Resize(ref buf, buf.Length * 2);
                    res = await _ws.ReceiveAsync(new ArraySegment<byte>(buf, len, buf.Length - len), ct);
                    if (res.MessageType == WebSocketMessageType.Close) return;
                    len += res.Count;
                } while (!res.EndOfMessage);
                var msg = new byte[len];
                Buffer.BlockCopy(buf, 0, msg, 0, len);
                _inbox.Enqueue(msg);
            }
        }

        async Task SendLoop(CancellationToken ct)
        {
            try
            {
                foreach (var data in _outbox.GetConsumingEnumerable(ct))
                {
                    if (_ws.State != WebSocketState.Open) break;
                    await _ws.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Binary, true, ct);
                }
            }
            catch (Exception) { }
        }

        public void Send(byte[] data)
        {
            if (_state == TransportState.Open) _outbox.Add(data);
        }

        public byte[] Poll()
        {
            return _inbox.TryDequeue(out var m) ? m : null;
        }

        public void Dispose()
        {
            _state = TransportState.Closed;
            try { _cts?.Cancel(); } catch { }
            try { _ws?.Abort(); } catch { }
            _ws?.Dispose();
            _ws = null;
        }
    }
}
