using System;
using Game.Shared.Protocol;
using UnityEngine;
using Ping = Game.Shared.Protocol.Ping; // UnityEngine.Ping would otherwise be ambiguous

namespace Game.Client.Net
{
    /// <summary>
    /// Owns the connection: handshake, message dispatch, ping/RTT and server-time estimate.
    /// All callbacks run on the Unity main thread (inside Update).
    /// </summary>
    public sealed class NetClient : MonoBehaviour
    {
        public event Action<Welcome> OnWelcome;
        public event Action<Snapshot> OnSnapshot;
        public event Action<EntitySpawn> OnSpawn;
        public event Action<EntityDespawn> OnDespawn;
        public event Action<PositionCorrection> OnCorrection;
        public event Action<Notice> OnNotice;
        public event Action<CharacterSync> OnCharacter;
        public event Action<InventorySync> OnInventory;
        public event Action<QuestSync> OnQuests;
        public event Action<CombatEvent> OnCombat;
        public event Action<HpUpdate> OnHp;
        public event Action<DialogueOpen> OnDialogue;
        public event Action<GainEvent> OnGain;
        public event Action<ActionEvent> OnAction;
        public event Action<DeathEvent> OnDeath;
        public event Action<StatsSync> OnStats;
        public event Action<SkillSync> OnSkills;
        public event Action<SkillCast> OnSkillCast;
        public event Action<SkillCooldown> OnSkillCooldown;
        public event Action<ProjectileSpawn> OnProjectileSpawn;
        public event Action<ProjectileEnd> OnProjectileEnd;
        public event Action<AmmoState> OnAmmo;
        public event Action<CcApplied> OnCc;
        public event Action<ChatMessage> OnChat;
        public event Action OnChargeCancel;
        public event Action<TrialState> OnTrial;
        public event Action<ManaState> OnMana;
        public event Action<FlightState> OnFlight;
        public event Action<ShopOpen> OnShop;
        public event Action OnDisconnected;

        ITransport _transport;
        string _pendingName;
        string _pendingPassword = "";
        bool _pendingCreate;
        bool _helloSent;
        bool _welcomed;
        float _pingTimer;
        float _pingInterval = 2f;

        // Server time estimate: serverNow ≈ Time.realtimeSinceStartupAsDouble + _offset
        double _offset;
        bool _hasOffset;
        double _bestRtt = double.MaxValue;

        public bool Connected => _transport != null && _transport.State == TransportState.Open;
        public bool InGame => _welcomed && Connected;
        public float RttMs { get; private set; }
        public int MessagesPerSecond { get; private set; }
        public int BytesPerSecond { get; private set; }
        int _msgCount, _byteCount;
        float _statTimer;

        public double LocalTime => Time.realtimeSinceStartupAsDouble;
        public double ServerNow => LocalTime + _offset;

        public void Connect(string url, string name, string password = "", bool create = false)
        {
            Disconnect(silent: true);
            _pendingName = name;
            _pendingPassword = password ?? "";
            _pendingCreate = create;
            _helloSent = false;
            _welcomed = false;
            _hasOffset = false;
            _bestRtt = double.MaxValue;
            _transport = TransportFactory.Create();
            Debug.Log($"[net] connecting {url} as '{name}'");
            _transport.Connect(url);
        }

        public void Disconnect(bool silent = false)
        {
            if (_transport == null) return;
            _transport.Dispose();
            _transport = null;
            bool was = _welcomed;
            _welcomed = false;
            if (!silent && was) OnDisconnected?.Invoke();
        }

        public void Send(INetMessage msg)
        {
            if (Connected) _transport.Send(Packet.Encode(msg));
        }

        public void SetPingInterval(float seconds) { _pingInterval = Mathf.Max(0.5f, seconds); }

        void Update()
        {
            if (_transport == null) return;

            if (_transport.State == TransportState.Closed)
            {
                bool was = _welcomed || _helloSent;
                _transport.Dispose();
                _transport = null;
                _welcomed = false;
                if (was) OnDisconnected?.Invoke();
                else OnNotice?.Invoke(new Notice { Kind = NoticeKind.Fatal, Text = "Cannot reach the server. Is it running?" });
                return;
            }

            if (_transport.State == TransportState.Open && !_helloSent)
            {
                _helloSent = true;
                Send(new Hello { ProtocolVersion = ProtocolInfo.Version, Name = _pendingName, Password = _pendingPassword, Create = _pendingCreate });
                SendPing();
            }

            byte[] data;
            int guard = 0;
            while (_transport != null && (data = _transport.Poll()) != null && guard++ < 500)
            {
                _msgCount++;
                _byteCount += data.Length;
                try { Dispatch(data); }
                catch (ProtocolException e) { Debug.LogError($"[net] bad packet: {e.Message}"); }
                catch (Exception e) { Debug.LogException(e); } // a handler bug must not kill the receive loop
            }

            if (_welcomed)
            {
                _pingTimer += Time.unscaledDeltaTime;
                if (_pingTimer >= _pingInterval) { _pingTimer = 0; SendPing(); }
            }

            _statTimer += Time.unscaledDeltaTime;
            if (_statTimer >= 1f)
            {
                MessagesPerSecond = _msgCount;
                BytesPerSecond = _byteCount;
                _msgCount = _byteCount = 0;
                _statTimer = 0;
            }
        }

        void SendPing() { Send(new Ping { ClientTime = LocalTime }); }

        void Dispatch(byte[] data)
        {
            var r = new NetReader(data);
            switch (Packet.PeekId(r))
            {
                case MsgId.Welcome:
                {
                    var w = Welcome.Read(r);
                    if (!_hasOffset) { _offset = w.ServerTime - LocalTime; _hasOffset = true; }
                    _welcomed = true;
                    OnWelcome?.Invoke(w);
                    break;
                }
                case MsgId.Snapshot: OnSnapshot?.Invoke(Snapshot.Read(r)); break;
                case MsgId.EntitySpawn: OnSpawn?.Invoke(EntitySpawn.Read(r)); break;
                case MsgId.EntityDespawn: OnDespawn?.Invoke(EntityDespawn.Read(r)); break;
                case MsgId.PositionCorrection: OnCorrection?.Invoke(PositionCorrection.Read(r)); break;
                case MsgId.Notice: OnNotice?.Invoke(Notice.Read(r)); break;
                case MsgId.CharacterSync: OnCharacter?.Invoke(CharacterSync.Read(r)); break;
                case MsgId.InventorySync: OnInventory?.Invoke(InventorySync.Read(r)); break;
                case MsgId.QuestSync: OnQuests?.Invoke(QuestSync.Read(r)); break;
                case MsgId.CombatEvent: OnCombat?.Invoke(CombatEvent.Read(r)); break;
                case MsgId.HpUpdate: OnHp?.Invoke(HpUpdate.Read(r)); break;
                case MsgId.DialogueOpen: OnDialogue?.Invoke(DialogueOpen.Read(r)); break;
                case MsgId.GainEvent: OnGain?.Invoke(GainEvent.Read(r)); break;
                case MsgId.ActionEvent: OnAction?.Invoke(ActionEvent.Read(r)); break;
                case MsgId.DeathEvent: OnDeath?.Invoke(DeathEvent.Read(r)); break;
                case MsgId.StatsSync: OnStats?.Invoke(StatsSync.Read(r)); break;
                case MsgId.SkillSync: OnSkills?.Invoke(SkillSync.Read(r)); break;
                case MsgId.SkillCast: OnSkillCast?.Invoke(SkillCast.Read(r)); break;
                case MsgId.SkillCooldown: OnSkillCooldown?.Invoke(SkillCooldown.Read(r)); break;
                case MsgId.ProjectileSpawn: OnProjectileSpawn?.Invoke(ProjectileSpawn.Read(r)); break;
                case MsgId.ProjectileEnd: OnProjectileEnd?.Invoke(ProjectileEnd.Read(r)); break;
                case MsgId.AmmoState: OnAmmo?.Invoke(AmmoState.Read(r)); break;
                case MsgId.CcApplied: OnCc?.Invoke(CcApplied.Read(r)); break;
                case MsgId.ChatMessage: OnChat?.Invoke(ChatMessage.Read(r)); break;
                case MsgId.ChargeCancel: ChargeCancel.Read(r); OnChargeCancel?.Invoke(); break;
                case MsgId.TrialState: OnTrial?.Invoke(TrialState.Read(r)); break;
                case MsgId.ManaState: OnMana?.Invoke(ManaState.Read(r)); break;
                case MsgId.FlightState: OnFlight?.Invoke(FlightState.Read(r)); break;
                case MsgId.ShopOpen: OnShop?.Invoke(ShopOpen.Read(r)); break;
                case MsgId.Pong:
                {
                    var p = Pong.Read(r);
                    double now = LocalTime;
                    double rtt = now - p.ClientTime;
                    RttMs = (float)(rtt * 1000.0);
                    // Use the lowest-RTT sample for the clock offset (least queueing noise); slowly forget old bests.
                    _bestRtt = Math.Min(_bestRtt * 1.05, double.MaxValue);
                    if (rtt <= _bestRtt)
                    {
                        _bestRtt = rtt;
                        _offset = p.ServerTime + rtt * 0.5 - now;
                        _hasOffset = true;
                    }
                    break;
                }
                default:
                    Debug.LogWarning("[net] unknown message");
                    break;
            }
        }

        void OnDestroy() { Disconnect(silent: true); }
    }
}
