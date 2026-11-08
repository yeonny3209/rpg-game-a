using System.Collections.Concurrent;
using System.Text.Json;
using Game.Server.Data;
using Game.Server.Db;
using Game.Server.Logic;
using Game.Server.Net;
using Game.Shared.Data;
using Game.Shared.Protocol;
using Game.Shared.World;

namespace Game.Server.World;

public enum ZoneInboundKind { Message, Leave, Callback }

public readonly record struct ZoneInbound(IClientConnection Conn, ZoneInboundKind Kind, object Message, double ReceivedAt);

/// <summary>
/// One region × one channel. Runs its own fixed-rate tick on a single thread, so game state needs no locks.
/// Everything that changes game state goes through the inbox (doc 02 §2.3). DB work runs on DbWorker and
/// comes back through the inbox as a callback.
/// Split into partial files: Zone.cs (login, movement, sync, save), Zone.Combat.cs, Zone.Npc.cs.
/// </summary>
public sealed partial class Zone
{
    public string ZoneKey { get; }
    public string MapId => _data.Shared.map.id;
    readonly GameData _data;
    readonly SharedData _d;
    readonly MapTerrain _terrain;
    readonly MovementValidator _validator;
    readonly DbWorker _db;
    readonly Random _rng;
    readonly ConcurrentQueue<ZoneInbound> _inbox = new();
    readonly Dictionary<IClientConnection, PlayerEntity> _byConn = new();
    readonly Dictionary<uint, PlayerEntity> _players = new();
    readonly HashSet<string> _pendingLogins = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<IClientConnection> _pendingConns = new();
    readonly LoginThrottle _throttle = new();
    uint _nextEntityId = 1;
    uint _tick;
    double _now;
    Thread _thread;
    volatile bool _running;

    // Read by the /status endpoint (other thread) — informational only.
    public int PlayerCount => _players.Count;
    public int MonsterCount => _monsters.Count;
    public uint Tick => _tick;
    public double LastTickMs { get; private set; }

    public Zone(string zoneKey, GameData data, DbWorker db, Random rng = null)
    {
        ZoneKey = zoneKey;
        _data = data;
        _d = data.Shared;
        _db = db;
        _rng = rng ?? new Random();
        _terrain = new MapTerrain(_d.map);
        _validator = new MovementValidator(data.Config, _terrain);
        SpawnNpcs();
        SpawnAllMonsters();
    }

    public MapTerrain Terrain => _terrain;

    public void Post(ZoneInbound msg) => _inbox.Enqueue(msg);
    void PostCallback(Action a) => _inbox.Enqueue(new ZoneInbound(null, ZoneInboundKind.Callback, a, 0));

    public void Start()
    {
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = $"zone:{ZoneKey}" };
        _thread.Start();
    }

    /// <summary>Stops the tick and queues a final save of everyone online. Call DbWorker.CloseAsync afterwards.</summary>
    public void Shutdown()
    {
        _running = false;
        _thread?.Join(TimeSpan.FromSeconds(3));
        foreach (var p in _players.Values) QueueSave(p);
        Log.Info($"zone {ZoneKey} stopped, saved {_players.Count} character(s)");
    }

    void Loop()
    {
        double dt = 1.0 / _data.Config.net.serverTickRate;
        double next = ServerClock.Now;
        Log.Info($"zone {ZoneKey} started at {_data.Config.net.serverTickRate} Hz ({_monsters.Count} monsters, {_npcs.Count} NPCs)");
        while (_running)
        {
            double now = ServerClock.Now;
            if (now < next)
            {
                int sleepMs = (int)((next - now) * 1000);
                if (sleepMs > 1) Thread.Sleep(sleepMs - 1); else Thread.Yield();
                continue;
            }
            // If we fall far behind (debugger pause), don't spiral.
            if (now - next > 1.0) next = now;
            next += dt;

            var t0 = ServerClock.Now;
            try { TickOnce(now, (float)dt); }
            catch (Exception e) { Log.Error($"zone {ZoneKey} tick: {e}"); }
            LastTickMs = (ServerClock.Now - t0) * 1000;
        }
    }

    /// <summary>Exposed for tests.</summary>
    internal void TickOnce(double now, float dt)
    {
        _now = now;
        _tick++;
        while (_inbox.TryDequeue(out var m)) Handle(m);
        RunScheduled();
        UpdateMonsters(dt);
        UpdateProjectiles(dt);
        UpdateTrials();
        UpdatePlayers(dt);
        UpdateMana(dt);
        UpdateFlight();
        UpdateReload();
        KickIdle();
        FlushSyncs();
        SaveDirty();
        BroadcastSnapshot();
    }

    void Handle(ZoneInbound m)
    {
        try
        {
            switch (m.Kind)
            {
                case ZoneInboundKind.Leave: RemovePlayer(m.Conn); return;
                case ZoneInboundKind.Callback: ((Action)m.Message)(); return;
            }
            if (m.Message is Hello h) { OnHello(m.Conn, h); return; }
            if (!_byConn.TryGetValue(m.Conn, out var p)) return; // not logged in yet
            switch (m.Message)
            {
                case MoveState s: OnMove(p, s, m.ReceivedAt); break;
                case Attack a: OnAttack(p, a); break;
                case Interact i: OnInteract(p, i); break;
                case DialogueChoice c: OnDialogueChoice(p, c); break;
                case RespawnRequest: OnRespawnRequest(p); break;
                case ChooseGender g: OnChooseGender(p, g); break;
                case AllocateStats a: OnAllocateStats(p, a); break;
                case UseSkill u: OnUseSkill(p, u); break;
                case ChargeStart: OnChargeStart(p); break;
                case ChargeRelease cr: OnChargeRelease(p, cr); break;
                case ChatSend cs: OnChatSend(p, cs); break;
                case ChooseElement ce: OnChooseElement(p, ce); break;
                case EquipItem ei: OnEquipItem(p, ei); break;
                case UnequipItem ui: OnUnequipItem(p, ui); break;
                case UseItem ui: OnUseItem(p, ui); break;
                case ExpandBag: OnExpandBag(p); break;
                case ShopBuy sb: OnShopBuy(p, sb); break;
                case ShopSell ss: OnShopSell(p, ss); break;
                case StartFlight: OnStartFlight(p); break;
            }
        }
        catch (Exception e)
        {
            Log.Error($"zone handle {m.Message?.GetType().Name}: {e}");
        }
    }

    // ───────────── login / logout ─────────────

    void OnHello(IClientConnection c, Hello h)
    {
        if (_byConn.ContainsKey(c) || _pendingConns.Contains(c)) return;

        if (h.ProtocolVersion != ProtocolInfo.Version)
        {
            Reject(c, $"클라이언트 버전이 서버와 달라요(클라 {h.ProtocolVersion}, 서버 {ProtocolInfo.Version}). 새로고침해 주세요.");
            return;
        }

        var name = (h.Name ?? "").Trim();
        int len = new System.Globalization.StringInfo(name).LengthInTextElements;
        if (len < 2 || len > 12 || name.Any(ch => char.IsControl(ch) || char.IsWhiteSpace(ch)))
        {
            Reject(c, "닉네임은 띄어쓰기 없이 2~12자로 정해 주세요.");
            return;
        }
        if (_pendingLogins.Contains(name) || _players.Values.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            Reject(c, "이미 접속 중인 닉네임이에요.");
            return;
        }
        if (_players.Count + _pendingLogins.Count >= _data.Config.net.maxPlayersPerZone)
        {
            Reject(c, "이 지역이 가득 찼어요.");
            return;
        }

        string address = c.RemoteAddress ?? "?";
        if (_throttle.IsLocked(address, name, ServerClock.Now))
        {
            Reject(c, LoginException.Of(LoginFailure.TooManyTries).Message);
            return;
        }
        var auth = new LoginAuth(h.Password ?? "", h.Create, PasswordRules.IsLocalAddress(address), _data.Server.IsAdmin(name));

        _pendingLogins.Add(name);
        _pendingConns.Add(c);
        _db.Load(name, () => Rules.NewCharacter(_d, name, MapId), auth, (rec, err) => PostCallback(() =>
        {
            _pendingLogins.Remove(name);
            _pendingConns.Remove(c);
            if (err is LoginException le)
            {
                if (le.Reason is LoginFailure.WrongPassword or LoginFailure.NameTaken) _throttle.Fail(address, name, ServerClock.Now);
                Log.Warn($"login refused '{name}' from {address}: {le.Reason}");
                Reject(c, le.Message);
                return;
            }
            if (err != null)
            {
                Log.Error($"load '{name}' failed: {err.Message}");
                Reject(c, "캐릭터를 불러오지 못했어요(서버 DB 오류). 잠시 뒤 다시 접속해 주세요.");
                return;
            }
            if (c.Closed) return; // left while loading; nothing changed, nothing to save
            _throttle.Success(address, name);
            FinishLogin(c, rec);
        }));
    }

    void FinishLogin(IClientConnection c, CharacterRecord rec)
    {
        var inv = Inventory.FromRows(_d, rec.BagSlots > 0 ? rec.BagSlots : _d.character.bagSlots, rec.Items);
        var p = new PlayerEntity
        {
            EntityId = _nextEntityId++,
            Conn = c,
            Rec = rec,
            Inv = inv,
            Quests = new QuestLog(_d, rec.Quests),
            LastSaveAt = _now,
        };
        RecalcStats(p);
        p.Mana = rec.Mana < 0 ? p.Stats.MaxMana : Math.Min(rec.Mana, p.Stats.MaxMana);

        // Where to appear: last saved spot if still legal, else the village spawn.
        var map = _d.map;
        float x = map.playerSpawn.x, z = map.playerSpawn.z, yaw = map.playerSpawn.yaw;
        bool fresh = !rec.HasPosition;
        if (rec.HasPosition && rec.Zone == MapId && _terrain.InBounds(rec.X, rec.Z) && rec.Hp > 0 && AreaLimitFor(p) is var lim
            && (lim == null || Dist2(rec.X, rec.Z, lim.x, lim.z) <= lim.radius * lim.radius))
        {
            x = rec.X; z = rec.Z; yaw = rec.Yaw;
        }
        else if (!fresh)
        {
            x = map.respawnPoint.x; z = map.respawnPoint.z; yaw = map.respawnPoint.yaw;
        }
        if (rec.Hp <= 0 || rec.Hp > p.Stats.MaxHp) rec.Hp = p.Stats.MaxHp;
        rec.Zone = MapId;
        float y = _terrain.Height(x, z);
        _validator.Init(p.Move, x, y, z, _now);
        p.Move.Yaw = yaw;

        _byConn[c] = p;
        _players[p.EntityId] = p;

        c.Send(new Welcome { YourEntityId = p.EntityId, ServerTime = _now, X = x, Y = y, Z = z, Yaw = yaw, SharedDataJson = _data.SharedJson });
        c.Send(CharSync(p));
        c.Send(BuildStatsSync(p));
        c.Send(BuildSkillSync(p));
        SendMana(p);
        ResetAmmo(p);
        c.Send(p.Inv.ToSync());
        c.Send(p.Quests.ToSync(p.Inv));

        foreach (var n in _npcs.Values) c.Send(SpawnOf(n));
        foreach (var m in _monsters.Values) if (!m.Dead) c.Send(SpawnOf(m));
        foreach (var other in _players.Values)
        {
            if (other == p) continue;
            c.Send(SpawnOf(other));
            other.Conn.Send(SpawnOf(p));
        }
        Log.Info($"join  #{p.EntityId} '{p.Name}' Lv{rec.Level} from {c.RemoteAddress} (online {_players.Count})");
    }

    static void Reject(IClientConnection c, string text)
    {
        c.Send(new Notice { Kind = NoticeKind.Fatal, Text = text });
        c.Close();
    }

    void RemovePlayer(IClientConnection c)
    {
        if (!_byConn.Remove(c, out var p)) return;
        _players.Remove(p.EntityId);
        RemoveTrialOpponent(p.Trial);
        p.Trial = null;
        foreach (var m in _monsters.Values) if (m.Target == p) m.Target = null;
        QueueSave(p);
        var bye = Packet.Encode(new EntityDespawn { EntityId = p.EntityId });
        foreach (var other in _players.Values) other.Conn.SendRaw(bye);
        Log.Info($"leave #{p.EntityId} '{p.Name}' (online {_players.Count})");
    }

    void KickIdle()
    {
        float timeout = _data.Config.net.idleTimeoutSec;
        foreach (var p in _players.Values)
        {
            if (ServerClock.Now - p.Conn.LastReceivedAt > timeout && !p.Conn.Closed)
            {
                Log.Info($"idle timeout #{p.EntityId} '{p.Name}'");
                p.Conn.Close();
            }
        }
    }

    // ───────────── look (gender, weapon in hand) ─────────────

    /// <summary>Others saw this player with the old look: replace their copy with the current one (gender, weapon).</summary>
    void ResendToOthers(PlayerEntity p)
    {
        var gone = Packet.Encode(new EntityDespawn { EntityId = p.EntityId });
        var back = Packet.Encode(SpawnOf(p));
        foreach (var other in _players.Values)
        {
            if (other == p) continue;
            other.Conn.SendRaw(gone);
            other.Conn.SendRaw(back);
        }
    }

    /// <summary>Right after the first login the client shows a picker; the choice is accepted once and then saved.</summary>
    void OnChooseGender(PlayerEntity p, ChooseGender m)
    {
        if (p.Rec.Gender != Gender.Unset) return;                       // already chosen: ignore (no changing by message)
        if (m.Value != Gender.Male && m.Value != Gender.Female) return;
        p.Rec.Gender = m.Value;
        p.CharDirty = p.SaveDirty = true;
        p.SaveNow = true;
        Audit(p, "gender_chosen", new { gender = m.Value.ToString() });
        ResendToOthers(p);
        Log.Info($"look #{p.EntityId} '{p.Name}' → {m.Value}");
    }

    // ───────────── movement ─────────────

    /// <summary>The start-area circle while the first quest hasn't unlocked leaving it, else null.</summary>
    CircleDef AreaLimitFor(PlayerEntity p) =>
        (p.Rec.Unlocks & UnlockFlags.LeaveStartArea) != 0 ? null : _d.map.startArea;

    void OnMove(PlayerEntity p, MoveState st, double receivedAt)
    {
        if (p.Dead) return;
        var a = p.Move;
        if (st.CorrectionEpoch != a.Epoch) return; // client hasn't applied our last correction yet
        if (st.Seq <= a.LastSeq && a.LastSeq != 0) return; // stale / duplicate

        // Stunned / frozen / rooted: you may not walk (the client stops itself; this holds the line for a modified one).
        if (p.Immobile(_now) && Dist2(st.X, st.Z, a.X, a.Z) > 0.09f)
        {
            Correct(p, CorrectionReason.CcLocked);
            return;
        }

        var reason = _validator.Apply(a, st, receivedAt, AreaLimitFor(p));
        if (reason is null)
        {
            if (a.Dodged) { p.InvulnUntil = _now + _data.Config.movement.dodgeInvulnSec; a.Dodged = false; }
            return;
        }

        if (reason != CorrectionReason.AreaLocked) a.Violations++;
        Correct(p, reason.Value);
        if (reason == CorrectionReason.AreaLocked)
            p.Conn.Send(new Notice { Kind = NoticeKind.Info, Text = _d.map.startAreaMessage ?? "아직 이 지역을 벗어날 수 없어." });
        else
            Log.Warn($"correct #{p.EntityId} '{p.Name}': {reason} (total {a.Violations}) sent ({st.X:F2}, {st.Y:F2}, {st.Z:F2}) ground {_terrain.Height(st.X, st.Z):F2}");
    }

    void Correct(PlayerEntity p, CorrectionReason reason)
    {
        var a = p.Move;
        a.Epoch++;
        p.Conn.Send(new PositionCorrection { Epoch = a.Epoch, Reason = reason, X = a.X, Y = a.Y, Z = a.Z });
    }

    // ───────────── per-tick player upkeep ─────────────

    void UpdatePlayers(float dt)
    {
        var r = _d.character;
        foreach (var p in _players.Values)
        {
            if (p.Dead || p.Rec.Hp >= p.Stats.MaxHp) { p.RegenAcc = 0; continue; }
            // Spec vol.2 2.3: 비전투 시 초당 최대 HP 2%, 전투 중에는 낮게(체력 × 0.1/초).
            float perSec = p.InCombat(_now, r.combatTimeoutSec) ? p.Rec.Con * r.regenInCombatPerCon : p.Stats.MaxHp * r.regenOutOfCombatPerSec;
            p.RegenAcc += perSec * dt;
            if (p.RegenAcc < 1f) continue;
            int add = (int)p.RegenAcc;
            p.RegenAcc -= add;
            p.Rec.Hp = Math.Min(p.Stats.MaxHp, p.Rec.Hp + add);
            p.SaveDirty = true;
            // HP ticks only matter to the owner; others see HP through combat events.
            if (_tick % 4 == 0 || p.Rec.Hp == p.Stats.MaxHp)
                p.Conn.Send(new HpUpdate { EntityId = p.EntityId, Hp = p.Rec.Hp, MaxHp = p.Stats.MaxHp });
        }
    }

    void RecalcStats(PlayerEntity p)
    {
        p.Stats = Rules.Stats(_d, p.Rec, p.Inv, p.TrialJob);
        string oldWeapon = p.WeaponType;
        var inHand = p.TrialJob != null ? _d.Item(_d.Job(p.TrialJob)?.starterWeapon) : p.Inv.Weapon;
        p.WeaponType = inHand?.weaponType ?? "";
        if (oldWeapon != p.WeaponType && _players.ContainsKey(p.EntityId)) ResendToOthers(p);
        if (p.Rec.Hp > p.Stats.MaxHp) p.Rec.Hp = p.Stats.MaxHp;
        p.CharDirty = true;
        if (_players.ContainsKey(p.EntityId)) CheckCircle(p);   // level / 지능 changes may open the next circle
    }

    // ───────────── client syncs ─────────────

    CharacterSync CharSync(PlayerEntity p)
    {
        var r = p.Rec;
        return new CharacterSync
        {
            Name = r.Name, Level = (ushort)r.Level, Exp = r.Exp, ExpToNext = _d.exp.ToNext(r.Level), Gold = r.Gold,
            Hp = r.Hp, MaxHp = p.Stats.MaxHp, Attack = p.Stats.Attack, Defense = p.Stats.Defense,
            StatPoints = r.StatPoints,
            Str = (ushort)r.Str, Dex = (ushort)r.Dex, Int = (ushort)r.Int, Con = (ushort)r.Con, Spi = (ushort)r.Spi,
            Unlocks = r.Unlocks, Dead = p.Dead, Gender = r.Gender, Job = r.Job ?? "", JobTier = (byte)r.JobTier,
        };
    }

    void FlushSyncs()
    {
        foreach (var p in _players.Values)
        {
            if (p.CharDirty) { p.Conn.Send(CharSync(p)); p.Conn.Send(BuildStatsSync(p)); p.CharDirty = false; }
            if (p.SkillDirty) { p.Conn.Send(BuildSkillSync(p)); p.SkillDirty = false; }
            if (p.InvDirty) { p.Conn.Send(p.Inv.ToSync()); p.InvDirty = false; }
            if (p.QuestDirty) { p.Conn.Send(p.Quests.ToSync(p.Inv)); p.QuestDirty = false; }
        }
    }

    void Gain(PlayerEntity p, GainKind kind, string refId, long amount) =>
        p.Conn.Send(new GainEvent { Kind = kind, RefId = refId ?? "", Amount = amount });

    void Audit(PlayerEntity p, string kind, object data)
    {
        p.PendingAudits.Add(new AuditEntry(kind, JsonSerializer.Serialize(data)));
        p.SaveDirty = true;
    }

    // ───────────── persistence ─────────────

    void SaveDirty()
    {
        double debounce = _data.Config.net.saveDebounceSec;
        foreach (var p in _players.Values)
        {
            if (!p.SaveDirty && !p.SaveNow) continue;
            if (!p.SaveNow && _now - p.LastSaveAt < debounce) continue;
            QueueSave(p);
        }
    }

    void QueueSave(PlayerEntity p)
    {
        var r = p.Rec;
        r.HasPosition = true;
        r.X = p.Move.X; r.Y = p.Move.Y; r.Z = p.Move.Z; r.Yaw = p.Move.Yaw;
        r.Items = p.Inv.ToRows();
        r.BagSlots = p.Inv.Bag.Length;
        r.Mana = (int)p.Mana;
        r.Quests = p.Quests.ToRows();
        var audits = p.PendingAudits.ToList();
        p.PendingAudits.Clear();
        _db.Save(r.Clone(), audits);
        p.SaveDirty = p.SaveNow = false;
        p.LastSaveAt = _now;
    }

    // ───────────── snapshots ─────────────

    readonly Snapshot _snap = new();

    void BroadcastSnapshot()
    {
        if (_players.Count == 0) return;
        // Stage 1: everyone sees everything in the zone. AOI grid comes with real regions (stage 4).
        int need = _players.Count + _monsters.Count;
        if (_snap.Entities.Length < need) _snap.Entities = new EntityState[need * 2];
        int i = 0;
        foreach (var p in _players.Values)
        {
            var a = p.Move;
            var f = a.Flags;
            if (p.Dead) f |= MoveFlags.Dead;
            _snap.Entities[i++] = new EntityState { EntityId = p.EntityId, X = a.X, Y = a.Y, Z = a.Z, Yaw = a.Yaw, Flags = f };
        }
        foreach (var m in _monsters.Values)
        {
            var f = MoveFlags.Grounded;
            if (m.Dead) f |= MoveFlags.Dead;
            if (m.Ai == MonsterAi.Returning) f |= MoveFlags.Returning;
            _snap.Entities[i++] = new EntityState { EntityId = m.EntityId, X = m.X, Y = m.Y, Z = m.Z, Yaw = m.Yaw, Flags = f };
        }
        _snap.Count = i;
        _snap.Tick = _tick;
        _snap.ServerTime = _now;
        Broadcast(_snap);
    }

    void Broadcast(INetMessage msg, PlayerEntity except = null)
    {
        if (_players.Count == 0) return;
        var bytes = Packet.Encode(msg);
        foreach (var p in _players.Values) if (p != except) p.Conn.SendRaw(bytes);
    }

    static EntitySpawn SpawnOf(PlayerEntity p) => new()
    {
        EntityId = p.EntityId, Kind = EntityKind.Player, TypeId = "", Name = p.Name, Level = (ushort)p.Rec.Level,
        Hp = p.Rec.Hp, MaxHp = p.Stats.MaxHp, Flags = p.Dead ? MoveFlags.Dead : MoveFlags.Grounded,
        X = p.Move.X, Y = p.Move.Y, Z = p.Move.Z, Yaw = p.Move.Yaw, Gender = p.Rec.Gender, Weapon = p.WeaponType ?? "",
    };

    static EntitySpawn SpawnOf(MonsterEntity m) => new()
    {
        EntityId = m.EntityId, Kind = EntityKind.Monster, TypeId = m.Def.id, Name = m.Def.name, Level = (ushort)m.Def.level,
        Hp = m.Hp, MaxHp = m.Def.maxHp, Flags = MoveFlags.Grounded, X = m.X, Y = m.Y, Z = m.Z, Yaw = m.Yaw,
    };

    static EntitySpawn SpawnOf(NpcEntity n) => new()
    {
        EntityId = n.EntityId, Kind = EntityKind.Npc, TypeId = n.Def.id, Name = n.Def.name, Level = 0,
        Hp = 1, MaxHp = 1, Flags = MoveFlags.Grounded, X = n.X, Y = n.Y, Z = n.Z, Yaw = n.Yaw,
    };

    static float Dist2(float ax, float az, float bx, float bz) => (ax - bx) * (ax - bx) + (az - bz) * (az - bz);

    // ───────────── test hooks ─────────────
    internal IEnumerable<PlayerEntity> Players => _players.Values;
    internal IEnumerable<MonsterEntity> Monsters => _monsters.Values;
    internal IEnumerable<NpcEntity> Npcs => _npcs.Values;
}
