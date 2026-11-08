using Game.Server.Data;
using Game.Server.Db;
using Game.Server.Net;
using Game.Server.World;
using Game.Shared.Protocol;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

// Usage: dotnet run --project server/src/Game.Server [-- --root <repo> --port 7777 --db <connection string | memory>]
string root = ArgValue(args, "--root") ?? GameData.FindRepoRoot();
int port = int.TryParse(ArgValue(args, "--port"), out var pp) ? pp : 7777;
string dbArg = ArgValue(args, "--db") ?? Environment.GetEnvironmentVariable("RPG_DB")
               ?? "Host=localhost;Port=5433;Username=rpg;Database=rpg;Timeout=5";

GameData data;
try { data = GameData.Load(root); }
catch (Exception e)
{
    Console.Error.WriteLine("데이터 로드 실패:\n" + e.Message);
    return 1;
}

ICharacterStore store;
if (dbArg == "memory")
{
    store = new MemoryCharacterStore();
}
else
{
    try
    {
        await PgCharacterStore.PrepareAsync(dbArg);
        store = new PgCharacterStore(dbArg);
    }
    catch (Exception e)
    {
        Console.Error.WriteLine("DB 연결 실패: " + e.Message);
        Console.Error.WriteLine("  → tools\\db-start.cmd 로 DB를 켰는지 확인해 줘. (DB 없이 시험만 하려면 --db memory)");
        return 2;
    }
}
var db = new DbWorker(store);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = Array.Empty<string>() });
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
var app = builder.Build();

var zone = new Zone($"{data.Shared.map.id}#1", data, db);
zone.Start();

// Ctrl+C / window close: stop the tick, save everyone, wait for the DB queue.
app.Lifetime.ApplicationStopping.Register(() =>
{
    Log.Info("shutting down: saving characters...");
    zone.Shutdown();
    db.CloseAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
    Log.Info($"saved. (pending {db.Pending}, failed {db.FailedSaves})");
});

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });

app.Map(ProtocolInfo.WebSocketPath, async ctx =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    // Through the Cloudflare tunnel every connection looks like localhost; the real visitor address is in CF-Connecting-IP.
    // Only believe that header when the TCP peer is this PC (cloudflared itself): anyone else could just write it.
    // This is what tells "sitting at the server PC" from "coming through the public link" (login rules, lockout).
    var peer = ctx.Connection.RemoteIpAddress;
    string remote = peer?.ToString() ?? "?";
    if (peer != null && System.Net.IPAddress.IsLoopback(peer))
    {
        string via = ctx.Request.Headers["CF-Connecting-IP"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(via)) remote = via.Trim();
    }
    var session = new Session(ws, zone, remote, data.Config.validation.maxMessagesPerSecond);
    await session.RunAsync(ctx.RequestAborted);
});

app.MapGet("/status", () => Results.Json(new
{
    zone = zone.ZoneKey,
    players = zone.PlayerCount,
    monsters = zone.MonsterCount,
    tick = zone.Tick,
    lastTickMs = Math.Round(zone.LastTickMs, 3),
    protocol = ProtocolInfo.Version,
    db = db.Describe,
    dbPending = db.Pending,
    dbFailedSaves = db.FailedSaves,
}));

// Serve the Unity WebGL build (builds/webgl) from the same origin, so the page can reach /ws directly.
var webglDir = Path.Combine(root, "builds", "webgl");
Directory.CreateDirectory(webglDir); // so a build made while the server runs is picked up without restart
{
    var types = new FileExtensionContentTypeProvider();
    types.Mappings[".data"] = "application/octet-stream";
    types.Mappings[".wasm"] = "application/wasm";
    types.Mappings[".unityweb"] = "application/octet-stream";
    types.Mappings[".symbols.json"] = "application/json";
    var files = new PhysicalFileProvider(webglDir);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = files,
        ContentTypeProvider = types,
        ServeUnknownFileTypes = true,
        OnPrepareResponse = c =>
        {
            // Unity compressed builds (*.br / *.gz) need Content-Encoding and the inner content type.
            var name = c.File.Name;
            string enc = name.EndsWith(".br") ? "br" : name.EndsWith(".gz") ? "gzip" : null;
            if (enc != null)
            {
                c.Context.Response.Headers.ContentEncoding = enc;
                var inner = name[..name.LastIndexOf('.')];
                if (types.TryGetContentType(inner, out var ct)) c.Context.Response.ContentType = ct;
            }
            c.Context.Response.Headers.CacheControl = "no-cache";
        },
    });
    Log.Info($"serving WebGL build from {webglDir}");
    if (!File.Exists(Path.Combine(webglDir, "index.html")))
        Log.Info("  (아직 WebGL 빌드가 없어. Unity 메뉴 RPG > 2. WebGL 빌드 후 브라우저를 새로고침하면 돼)");
}

// Routing matches "/" before the static-file middleware runs, so serve index.html here when it exists.
app.MapGet("/", () => File.Exists(Path.Combine(webglDir, "index.html"))
    ? Results.File(Path.Combine(webglDir, "index.html"), "text/html; charset=utf-8")
    : Results.Content(
    "<meta charset=utf-8><body style='font-family:sans-serif;background:#111;color:#eee;padding:40px'>" +
    "<h2>서버는 켜져 있어</h2><p>아직 WebGL 빌드가 없어. Unity에서 <b>RPG &gt; 2. WebGL 빌드</b>를 실행한 뒤 새로고침해 줘.</p>" +
    "<p>상태: <a style='color:#8bf' href='/status'>/status</a></p>", "text/html; charset=utf-8"));

Log.Info($"data root: {root}");
Log.Info($"db: {db.Describe}");
Log.Info($"listening: http://localhost:{port}  (WebSocket {ProtocolInfo.WebSocketPath}, status /status)");
app.Run();
return 0;

static string ArgValue(string[] args, string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
