using System.Collections.Concurrent;
using Game.Server.Data;
using Game.Server.Db;
using Game.Server.Net;
using Game.Server.World;
using Game.Shared.Protocol;
using Game.Shared.World;

namespace Game.Server.Tests;

/// <summary>Stands in for a WebSocket. Every message the zone sends is encoded and decoded again (tests the wire format too).</summary>
public sealed class FakeConnection : IClientConnection
{
    static int _next;
    public int Id { get; } = Interlocked.Increment(ref _next);
    /// <summary>"test" counts as the server PC; set an IP to act like a visitor coming through the public link.</summary>
    public string RemoteAddress { get; set; } = "test";
    public double LastReceivedAt => ServerClock.Now;
    public bool Closed { get; private set; }
    public readonly ConcurrentQueue<object> Inbox = new();

    public void Send(INetMessage msg) => SendRaw(Packet.Encode(msg));
    public void SendRaw(byte[] bytes) { if (!Closed) Inbox.Enqueue(Decode(bytes)); }
    public void Close() => Closed = true;

    public List<T> Take<T>()
    {
        var keep = new List<object>();
        var got = new List<T>();
        while (Inbox.TryDequeue(out var m)) { if (m is T t) got.Add(t); else keep.Add(m); }
        foreach (var k in keep) Inbox.Enqueue(k);
        return got;
    }

    public void Clear() { while (Inbox.TryDequeue(out _)) { } }

    static object Decode(byte[] bytes)
    {
        var r = new NetReader(bytes);
        var id = Packet.PeekId(r);
        object m = id switch
        {
            MsgId.Welcome => Welcome.Read(r),
            MsgId.Snapshot => Snapshot.Read(r),
            MsgId.EntitySpawn => EntitySpawn.Read(r),
            MsgId.EntityDespawn => EntityDespawn.Read(r),
            MsgId.PositionCorrection => PositionCorrection.Read(r),
            MsgId.Notice => Notice.Read(r),
            MsgId.CharacterSync => CharacterSync.Read(r),
            MsgId.InventorySync => InventorySync.Read(r),
            MsgId.QuestSync => QuestSync.Read(r),
            MsgId.CombatEvent => CombatEvent.Read(r),
            MsgId.HpUpdate => HpUpdate.Read(r),
            MsgId.DialogueOpen => DialogueOpen.Read(r),
            MsgId.GainEvent => GainEvent.Read(r),
            MsgId.ActionEvent => ActionEvent.Read(r),
            MsgId.DeathEvent => DeathEvent.Read(r),
            MsgId.StatsSync => StatsSync.Read(r),
            MsgId.SkillSync => SkillSync.Read(r),
            MsgId.SkillCast => SkillCast.Read(r),
            MsgId.SkillCooldown => SkillCooldown.Read(r),
            MsgId.ProjectileSpawn => ProjectileSpawn.Read(r),
            MsgId.ProjectileEnd => ProjectileEnd.Read(r),
            MsgId.AmmoState => AmmoState.Read(r),
            MsgId.CcApplied => CcApplied.Read(r),
            MsgId.ChatMessage => ChatMessage.Read(r),
            MsgId.ChargeCancel => ChargeCancel.Read(r),
            MsgId.TrialState => TrialState.Read(r),
            MsgId.ManaState => ManaState.Read(r),
            MsgId.ShopOpen => ShopOpen.Read(r),
            MsgId.FlightState => FlightState.Read(r),
            _ => throw new InvalidOperationException($"test decoder: unknown {id}"),
        };
        if (r.Remaining != 0) throw new InvalidOperationException($"{id}: {r.Remaining} trailing bytes");
        return m;
    }
}

/// <summary>A zone with the real data tables, a seeded RNG and a fake clock. Drives players like a client would.</summary>
public sealed class ZoneHarness
{
    public readonly GameData Data;
    public readonly ICharacterStore Store;
    public readonly DbWorker Db;
    public readonly Zone Zone;
    public double Now = 100;
    public const float Dt = 0.05f;

    public ZoneHarness(ICharacterStore store = null, int seed = 1)
    {
        Data = GameData.Load(GameData.FindRepoRoot());
        Store = store ?? new MemoryCharacterStore();
        Db = new DbWorker(Store);
        Zone = new Zone("test#1", Data, Db, new Random(seed));
    }

    public void Tick(int n = 1)
    {
        for (int i = 0; i < n; i++) { Now += Dt; Zone.TickOnce(Now, Dt); }
    }

    public void Send(FakeConnection c, object msg) => Zone.Post(new ZoneInbound(c, ZoneInboundKind.Message, msg, Now));

    public void Leave(FakeConnection c) { c.Close(); Zone.Post(new ZoneInbound(c, ZoneInboundKind.Leave, null, Now)); Tick(); }

    public TestPlayer Join(string name)
    {
        var c = new FakeConnection();
        Send(c, new Hello { ProtocolVersion = ProtocolInfo.Version, Name = name, Password = "pw1234", Create = true });
        Tick();
        Db.DrainAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        Tick();
        var w = c.Take<Welcome>().Single();
        var p = new TestPlayer(this, c, w);
        Tick(); // a real client's first move arrives a little after Welcome
        p.Absorb();
        return p;
    }

    public void FlushDb() => Db.DrainAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
}

public sealed class TestPlayer
{
    readonly ZoneHarness _h;
    public readonly FakeConnection Conn;
    public readonly uint EntityId;
    public float X, Y, Z, Yaw;
    uint _seq, _attackSeq;
    byte _epoch;
    public CharacterSync Char;
    public InventorySync Inv;
    public QuestSync Quests;
    public StatsSync Stats;
    public SkillSync Skills;
    public AmmoState Ammo;
    public TrialState Trial;
    public ManaState Mana;
    public ShopOpen Shop;
    public readonly List<FlightState> Flights = new();
    public readonly List<ChatMessage> Chats = new();
    public readonly List<SkillCooldown> Cooldowns = new();
    public readonly List<CcApplied> Ccs = new();
    public readonly List<Notice> Notices = new();
    public readonly List<ProjectileSpawn> Projectiles = new();
    public int ChargeCancels;
    public readonly List<CombatEvent> Combat = new();
    /// <summary>Combat events received since the last call.</summary>
    public List<CombatEvent> TakeCombat() { var l = new List<CombatEvent>(Combat); Combat.Clear(); return l; }
    public void ClearEvents() { Conn.Clear(); Combat.Clear(); Projectiles.Clear(); Ccs.Clear(); }
    public string LastNode;
    public string LastWalkFailure;
    public readonly List<PositionCorrection> Corrections = new();
    public readonly List<GainEvent> Gains = new();
    public readonly List<DeathEvent> Deaths = new();

    public TestPlayer(ZoneHarness h, FakeConnection c, Welcome w)
    {
        _h = h; Conn = c; EntityId = w.YourEntityId; X = w.X; Y = w.Y; Z = w.Z;
    }

    PlayerEntity Server => _h.Zone.Players.Single(p => p.EntityId == EntityId);
    public int ServerHp => Server.Rec.Hp;

    /// <summary>Reads everything received so far into the fields above (like a client's Update).</summary>
    public void Absorb()
    {
        while (Conn.Inbox.TryDequeue(out var m))
        {
            switch (m)
            {
                case CharacterSync s: Char = s; break;
                case InventorySync s: Inv = s; break;
                case QuestSync s: Quests = s; break;
                case DialogueOpen d: LastNode = d.NodeId; break;
                case GainEvent g: Gains.Add(g); break;
                case DeathEvent d: Deaths.Add(d); break;
                case StatsSync s: Stats = s; break;
                case SkillSync s: Skills = s; break;
                case AmmoState s: Ammo = s; break;
                case TrialState s: Trial = s; break;
                case ManaState s: Mana = s; break;
                case ShopOpen s: Shop = s; break;
                case FlightState s: Flights.Add(s); break;
                case ChatMessage c: Chats.Add(c); break;
                case SkillCooldown c: Cooldowns.Add(c); break;
                case CcApplied c: Ccs.Add(c); break;
                case Notice n: Notices.Add(n); break;
                case ProjectileSpawn pr: Projectiles.Add(pr); break;
                case ChargeCancel: ChargeCancels++; break;
                case CombatEvent ce: Combat.Add(ce); break;
                case PositionCorrection pc:
                    Corrections.Add(pc);
                    _epoch = pc.Epoch; X = pc.X; Y = pc.Y; Z = pc.Z;
                    break;
            }
        }
    }

    public void Tick(int n = 1) { _h.Tick(n); Absorb(); }

    /// <summary>Walks at 5 m/s (under the 6 m/s limit) toward (tx,tz), stopping `stop` meters short.</summary>
    public bool WalkTo(float tx, float tz, float stop = 0.3f, int maxTicks = 2000)
    {
        var terrain = _h.Zone.Terrain;
        for (int i = 0; i < maxTicks; i++)
        {
            float dx = tx - X, dz = tz - Z, d = MathF.Sqrt(dx * dx + dz * dz);
            if (d <= stop + 0.01f) return true;
            float step = Math.Min(5f * ZoneHarness.Dt, d - stop);
            X += dx / d * step; Z += dz / d * step; Y = terrain.Height(X, Z);
            Yaw = Game.Shared.World.Yaw.FromDirection(dx, dz);
            int before = Corrections.Count;
            _h.Send(Conn, new MoveState { Seq = ++_seq, CorrectionEpoch = _epoch, X = X, Y = Y, Z = Z, Yaw = Yaw, Flags = MoveFlags.Grounded });
            Tick();
            if (Corrections.Count > before) { LastWalkFailure = $"{Corrections.Last().Reason} at ({X:F1},{Z:F1}) tick {i}"; return false; }
        }
        LastWalkFailure = "timeout";
        return false;
    }

    public void StandStill(int ticks)
    {
        for (int i = 0; i < ticks; i++)
        {
            _h.Send(Conn, new MoveState { Seq = ++_seq, CorrectionEpoch = _epoch, X = X, Y = Y, Z = Z, Yaw = Yaw, Flags = MoveFlags.Grounded });
            Tick();
        }
    }

    public void AttackToward(float tx, float tz)
    {
        Yaw = Game.Shared.World.Yaw.FromDirection(tx - X, tz - Z);
        _h.Send(Conn, new Attack { Seq = ++_attackSeq, Yaw = Yaw });
    }

    // ── 2a helpers ──
    public void UseSkill(int slot, float? yaw = null) { _h.Send(Conn, new UseSkill { Slot = (byte)slot, Yaw = yaw ?? Yaw }); Tick(); }
    /// <summary>Sends a chat line. By default waits out the chat rate limit first (so command sequences just work).</summary>
    public void Chat(string text, ChatChannel ch = ChatChannel.General, bool wait = true)
    {
        if (wait) _h.Now += 0.7;
        _h.Send(Conn, new ChatSend { Channel = ch, Text = text });
        Tick();
    }
    public void Allocate(int str = 0, int dex = 0, int intel = 0, int con = 0, int spi = 0)
    {
        _h.Send(Conn, new AllocateStats { Str = (ushort)str, Dex = (ushort)dex, Int = (ushort)intel, Con = (ushort)con, Spi = (ushort)spi });
        Tick();
    }
    public void Equip(int bagSlot) { _h.Send(Conn, new EquipItem { BagSlot = (ushort)bagSlot }); Tick(); }
    public void Unequip(int slot) { _h.Send(Conn, new UnequipItem { EquipSlot = (byte)slot }); Tick(); }
    public void UseItem(int bagSlot) { _h.Send(Conn, new UseItem { BagSlot = (ushort)bagSlot }); Tick(); }
    public void ExpandBag() { _h.Send(Conn, new ExpandBag()); Tick(); }
    public void Buy(int index, int count = 1) { _h.Send(Conn, new ShopBuy { Index = (ushort)index, Count = (ushort)count }); Tick(); }
    public void Sell(int bagSlot, int count = 1) { _h.Send(Conn, new ShopSell { BagSlot = (ushort)bagSlot, Count = (ushort)count }); Tick(); }
    public void Element(string id) { _h.Send(Conn, new ChooseElement { ElementId = id }); Tick(); }
    public void Fly() { _h.Send(Conn, new StartFlight()); Tick(); }
    public void ChargeStart() { _h.Send(Conn, new ChargeStart()); Tick(); }
    public void ChargeRelease(float? yaw = null) { _h.Send(Conn, new ChargeRelease { Yaw = yaw ?? Yaw }); Tick(); }
    public PlayerEntity ServerSide => Server;

    public void Talk(uint npcId) { _h.Send(Conn, new Interact { EntityId = npcId }); Tick(); }

    public void Choose(uint npcId, byte option) { _h.Send(Conn, new DialogueChoice { NpcEntityId = npcId, NodeId = LastNode, Option = option }); Tick(); }

    public int Count(string itemId) => Inv.Items.Where(i => i.ItemId == itemId).Sum(i => i.Count);

    public QuestState QuestState(string id) => Quests.Quests.FirstOrDefault(q => q.QuestId == id).State;
}
