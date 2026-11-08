using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using Game.Shared.Data;
using Game.Shared.Protocol;
using Game.Shared.World;

// Fake players for testing without friends, and load testing later (stage 9: 50 bots).
// Usage: dotnet run --project server/tools/BotClient -- [--url ws://localhost:7777/ws] [--count 3] [--cheat] [--hunt] [--name Bot]
//   default : walk in circles near the village and hop
//   --cheat : bot 1 teleports every 5 s (server must correct it)
//   --hunt  : each bot plays the first quest: talk to the elder → hunt rabbits → hand in → level 2
string url = Arg("--url") ?? "ws://localhost:7777/ws";
int count = int.TryParse(Arg("--count"), out var c) ? c : 3;
bool cheat = args.Contains("--cheat");
bool hunt = args.Contains("--hunt");
string prefix = Arg("--name") ?? (hunt ? "Hunter" : "Bot");

Console.WriteLine($"bots: {count} → {url}{(cheat ? " (bot 1 cheats)" : "")}{(hunt ? " (quest mode)" : "")}  Ctrl+C로 종료");
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var tasks = Enumerable.Range(1, count).Select(i => new Bot($"{prefix}{i}", i, url, cheat && i == 1, hunt).Run(cts.Token)).ToArray();
await Task.WhenAll(tasks);
return;

string Arg(string n) { int i = Array.IndexOf(args, n); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }

sealed class Ent
{
    public EntityKind Kind;
    public string TypeId;
    public float X, Y, Z;
    public bool Dead;
}

sealed class Bot
{
    readonly string _name, _url;
    readonly int _index;
    readonly bool _cheater, _hunt;
    readonly ConcurrentQueue<object> _in = new();
    readonly Dictionary<uint, Ent> _ents = new();
    ClientWebSocket _ws;
    SharedData _d;
    MapTerrain _terrain;
    uint _self, _seq, _attackSeq;
    byte _epoch;
    float _x, _y, _z, _yaw;
    string _node = "";
    uint _dialogNpc;
    QuestState _quest;
    int _livers, _corrections, _kills, _level = 1;
    long _gold;
    bool _dead;
    Gender _gender;
    bool _genderSent;
    double _deadAt;

    public Bot(string name, int index, string url, bool cheater, bool hunt)
    {
        _name = name; _index = index; _url = url; _cheater = cheater; _hunt = hunt;
    }

    void Say(string s) => Console.WriteLine($"[{_name}] {s}");

    public async Task Run(CancellationToken ct)
    {
        try
        {
            _ws = new ClientWebSocket();
            await _ws.ConnectAsync(new Uri(_url), ct);
            await Send(new Hello { ProtocolVersion = ProtocolInfo.Version, Name = _name, Password = "bot-password", Create = true }, ct);
            var recv = Task.Run(() => ReceiveLoop(ct), ct);

            Welcome w = null;
            for (int i = 0; i < 500 && w == null && !ct.IsCancellationRequested; i++)
            {
                await Task.Delay(20, ct);
                while (_in.TryDequeue(out var m))
                {
                    if (m is Welcome ww) w = ww;
                    else if (m is Notice n) { Say($"공지({n.Kind}): {n.Text}"); if (n.Kind == NoticeKind.Fatal) return; }
                    else Absorb(m);
                }
            }
            if (w == null) { Say("Welcome을 못 받음"); return; }
            _d = JsonSerializer.Deserialize<SharedData>(w.SharedDataJson, new JsonSerializerOptions { IncludeFields = true });
            _d.Index();
            _terrain = new MapTerrain(_d.map);
            _self = w.YourEntityId; _x = w.X; _y = w.Y; _z = w.Z;
            Say($"접속 완료 entity #{_self} at ({_x:F1}, {_z:F1})");

            if (_hunt) await HuntLoop(ct); else await WanderLoop(ct);
            await recv;
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Say($"종료: {e.Message}"); }
    }

    async Task ReceiveLoop(CancellationToken ct)
    {
        var buf = new byte[1 << 20];
        while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
        {
            int len = 0;
            WebSocketReceiveResult res;
            do
            {
                res = await _ws.ReceiveAsync(new ArraySegment<byte>(buf, len, buf.Length - len), ct);
                if (res.MessageType == WebSocketMessageType.Close) return;
                len += res.Count;
            } while (!res.EndOfMessage);
            var r = new NetReader(buf, 0, len);
            object m = Packet.PeekId(r) switch
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
                MsgId.DialogueOpen => DialogueOpen.Read(r),
                MsgId.DeathEvent => DeathEvent.Read(r),
                MsgId.CombatEvent => CombatEvent.Read(r),
                _ => null, // HpUpdate, GainEvent, ActionEvent... not needed by bots
            };
            if (m != null) _in.Enqueue(m);
        }
    }

    void Absorb(object m)
    {
        switch (m)
        {
            case EntitySpawn s: _ents[s.EntityId] = new Ent { Kind = s.Kind, TypeId = s.TypeId, X = s.X, Y = s.Y, Z = s.Z, Dead = (s.Flags & MoveFlags.Dead) != 0 }; break;
            case EntityDespawn d: _ents.Remove(d.EntityId); break;
            case Snapshot s:
                for (int i = 0; i < s.Count; i++)
                {
                    var e = s.Entities[i];
                    if (_ents.TryGetValue(e.EntityId, out var en)) { en.X = e.X; en.Y = e.Y; en.Z = e.Z; en.Dead = (e.Flags & MoveFlags.Dead) != 0; }
                }
                break;
            case PositionCorrection pc:
                _epoch = pc.Epoch; _x = pc.X; _y = pc.Y; _z = pc.Z;
                if (pc.Reason != CorrectionReason.Respawn) { _corrections++; Say($"서버 보정: {pc.Reason} → ({pc.X:F1}, {pc.Y:F1}, {pc.Z:F1})"); }
                break;
            case Notice n: Say($"공지({n.Kind}): {n.Text}"); break;
            case CharacterSync cs:
                if (cs.Level > _level) Say($"레벨 업! Lv{cs.Level} (스탯 포인트 {cs.StatPoints})");
                _level = cs.Level; _gold = cs.Gold; _dead = cs.Dead; _gender = cs.Gender;
                break;
            case InventorySync inv: _livers = inv.Items.Where(i => i.ItemId == "rabbit_liver").Sum(i => i.Count); break;
            case QuestSync qs:
                var q = qs.Quests.FirstOrDefault(x => x.QuestId == "q_rabbit_liver");
                if (q.QuestId != null && q.State != _quest) Say($"퀘스트 {q.State} ({string.Join("/", q.Progress ?? Array.Empty<int>())})");
                _quest = q.QuestId != null ? q.State : QuestState.None;
                break;
            case DialogueOpen d: _node = d.NodeId; _dialogNpc = d.NpcEntityId; break;
            case DeathEvent de: _dead = true; _deadAt = Now; Say($"쓰러짐 (by {de.KillerName})"); break;
            case CombatEvent ce: if (ce.AttackerId == _self && (ce.Flags & HitFlags.Kill) != 0) _kills++; break;
        }
    }

    static readonly DateTime Start = DateTime.UtcNow;
    static double Now => (DateTime.UtcNow - Start).TotalSeconds;

    Task Send(INetMessage m, CancellationToken ct) => _ws.SendAsync(Packet.Encode(m), WebSocketMessageType.Binary, true, ct);

    Task SendMove(CancellationToken ct, MoveFlags flags = MoveFlags.Grounded, byte jumps = 0) =>
        Send(new MoveState { Seq = ++_seq, CorrectionEpoch = _epoch, X = _x, Y = _y, Z = _z, Yaw = _yaw, Flags = flags, JumpCount = jumps }, ct);

    void Pump() { while (_in.TryDequeue(out var m)) Absorb(m); }

    /// <summary>One 50 ms step toward (tx,tz) at 5 m/s. Returns true when within stop.</summary>
    bool Step(float tx, float tz, float stop)
    {
        float dx = tx - _x, dz = tz - _z, d = MathF.Sqrt(dx * dx + dz * dz);
        if (d <= stop + 0.01f) return true;
        float step = Math.Min(5f * 0.05f, d - stop);
        _x += dx / d * step; _z += dz / d * step; _y = _terrain.Height(_x, _z);
        _yaw = Yaw.FromDirection(dx, dz);
        return false;
    }

    async Task Tick(CancellationToken ct, bool move = true)
    {
        if (move && !_dead) await SendMove(ct);
        await Task.Delay(50, ct);
        Pump();
        if (_gender == Gender.Unset && !_genderSent)
        {
            _genderSent = true; // like the picker in the real client: bots pick a look right after login
            await Send(new ChooseGender { Value = (_self % 2 == 0) ? Gender.Female : Gender.Male }, ct);
        }
        if (_dead && Now - _deadAt > _d.character.respawnDelaySec + 0.5)
        {
            await Send(new RespawnRequest(), ct);
            _deadAt = Now; // retry later if it didn't take
        }
    }

    KeyValuePair<uint, Ent> Find(EntityKind kind, string type) =>
        _ents.Where(e => e.Value.Kind == kind && e.Value.TypeId == type && !e.Value.Dead)
             .OrderBy(e => (e.Value.X - _x) * (e.Value.X - _x) + (e.Value.Z - _z) * (e.Value.Z - _z)).FirstOrDefault();

    async Task TalkUntilClosed(uint npc, CancellationToken ct)
    {
        _node = "";
        await Send(new Interact { EntityId = npc }, ct);
        for (int i = 0; i < 40 && _node == ""; i++) await Tick(ct);
        for (int guard = 0; guard < 10 && _node != ""; guard++)
        {
            var node = _d.Dialogue(_node);
            Say($"대화 [{_node}] {node?.text}");
            string before = _node;
            await Send(new DialogueChoice { NpcEntityId = npc, NodeId = _node, Option = 0 }, ct);
            for (int i = 0; i < 40 && _node == before; i++) await Tick(ct);
        }
    }

    async Task HuntLoop(CancellationToken ct)
    {
        var started = Now;
        var elder = Find(EntityKind.Npc, "elder");
        if (elder.Value == null) { Say("이장을 못 찾음"); return; }

        if (_quest == QuestState.None)
        {
            while (!Step(elder.Value.X, elder.Value.Z, 2.5f)) await Tick(ct);
            await TalkUntilClosed(elder.Key, ct);
        }

        double lastSwing = 0;
        while (!ct.IsCancellationRequested && _quest == QuestState.Active)
        {
            var target = Find(EntityKind.Monster, "rabbit");
            if (target.Value == null) { await Tick(ct); continue; }
            if (!Step(target.Value.X, target.Value.Z, 1.6f)) { await Tick(ct); continue; }
            if (Now - lastSwing > 0.8)
            {
                lastSwing = Now;
                _yaw = Yaw.FromDirection(target.Value.X - _x, target.Value.Z - _z);
                await Send(new Attack { Seq = ++_attackSeq, Yaw = _yaw }, ct);
            }
            await Tick(ct);
        }

        if (_quest == QuestState.Ready)
        {
            Say($"토끼 간 {_livers}개 모음 (처치 {_kills}, 골드 {_gold}) → 이장에게");
            while (!Step(elder.Value.X, elder.Value.Z, 2.5f)) await Tick(ct);
            await TalkUntilClosed(elder.Key, ct);
        }
        Say($"끝: 퀘스트 {_quest}, Lv{_level}, 골드 {_gold}, 보정 {_corrections}회, {Now - started:F0}초");
        while (!ct.IsCancellationRequested) await Tick(ct);
    }

    async Task WanderLoop(CancellationToken ct)
    {
        float radius = 6f + _index * 3f;
        float speed = _d.config.movement.runSpeedMps * 0.8f;
        float omega = speed / radius * (_index % 2 == 0 ? 1 : -1);
        float phase = _index * 1.3f;
        float cx = _x - MathF.Cos(phase) * radius, cz = _z - MathF.Sin(phase) * radius;
        float vJump = MathF.Sqrt(2 * _d.config.movement.gravity * _d.config.movement.jumpHeight);
        float jumpPeriod = 4f + _index * 0.7f;
        double start = Now, lastCheat = 0;
        while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
        {
            double t = Now - start;
            float ang = phase + omega * (float)t;
            _x = cx + MathF.Cos(ang) * radius; _z = cz + MathF.Sin(ang) * radius;
            float ground = _terrain.Height(_x, _z);
            float tj = (float)(t % jumpPeriod), airT = 2 * vJump / _d.config.movement.gravity;
            _y = ground;
            var flags = MoveFlags.Grounded;
            byte jumps = 0;
            if (tj < airT) { _y = ground + vJump * tj - 0.5f * _d.config.movement.gravity * tj * tj; flags = tj < 0.05f ? MoveFlags.JumpStart : MoveFlags.None; jumps = 1; }
            _yaw = Yaw.FromDirection(-MathF.Sin(ang) * omega, MathF.Cos(ang) * omega);
            float sendX = _x;
            if (_cheater && t - lastCheat > 5) { lastCheat = t; _x += 30; }
            if (!_dead) await SendMove(ct, flags, jumps);
            _x = sendX;
            await Task.Delay(50, ct);
            Pump();
            if (_seq % 40 == 0) await Send(new Ping { ClientTime = t }, ct);
        }
    }
}
