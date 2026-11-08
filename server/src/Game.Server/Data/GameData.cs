using System.Text.Json;
using System.Text.Json.Serialization;
using Game.Shared.Data;

namespace Game.Server.Data;

/// <summary>
/// Loads every data table from /data, cross-checks them (validator), and prepares the JSON bundle sent to clients.
/// The server refuses to start with broken tables, so a typo in a JSON file is caught before anyone connects.
/// </summary>
public sealed class GameData
{
    public SharedData Shared { get; private set; }
    public GameConfig Config => Shared.config;
    public SpawnDef[] Spawns { get; private set; } = Array.Empty<SpawnDef>();
    /// <summary>data/server/server_config.json (admin nicknames).</summary>
    public ServerConfig Server { get; private set; } = new();
    public Dictionary<string, DropTableDef> Drops { get; private set; } = new();
    /// <summary>data/server/shops.json by id (the server decides prices; clients only see the open shop).</summary>
    public Dictionary<string, ShopDef> Shops { get; private set; } = new();
    /// <summary>data/shared bundled as one JSON object, forwarded to clients in Welcome.</summary>
    public string SharedJson { get; private set; }
    public string RepoRoot { get; }

    public const string MapFile = "maps/dawn_village.json";

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        IncludeFields = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    GameData(string repoRoot) { RepoRoot = repoRoot; }

    public static GameData Load(string repoRoot)
    {
        string sharedDir = Path.Combine(repoRoot, "data", "shared");
        string serverDir = Path.Combine(repoRoot, "data", "server");

        // Each file's top-level key matches a SharedData field, so they can be read straight into it.
        var s = new SharedData
        {
            config = Read<GameConfig>(sharedDir, "game_config.json"),
            exp = Read<ExpTable>(sharedDir, "exp_table.json"),
            character = Read<CharacterRules>(sharedDir, "character.json"),
            items = Read<SharedData>(sharedDir, "items.json").items,
            monsters = Read<SharedData>(sharedDir, "monsters.json").monsters,
            npcs = Read<SharedData>(sharedDir, "npcs.json").npcs,
            dialogues = Read<SharedData>(sharedDir, "dialogues.json").dialogues,
            quests = Read<SharedData>(sharedDir, "quests.json").quests,
            map = Read<MapDef>(sharedDir, MapFile),
            jobs = Read<SharedData>(sharedDir, "jobs.json").jobs,
            skillRules = Read<SharedData>(sharedDir, "skills.json").skillRules,
            skills = Read<SharedData>(sharedDir, "skills.json").skills,
            elements = Read<SharedData>(sharedDir, "elements.json").elements,
            itemRules = Read<SharedData>(sharedDir, "item_rules.json").itemRules,
        };
        var spawns = Read<SpawnTable>(serverDir, "spawns.json").spawns;
        var drops = Read<DropTableFile>(serverDir, "drop_tables.json").dropTables;
        var data = Build(s, spawns, drops, repoRoot);
        string shopPath = Path.Combine(serverDir, "shops.json");
        if (File.Exists(shopPath)) data.SetShops(Read<ShopFile>(serverDir, "shops.json").shops);
        string cfgPath = Path.Combine(serverDir, "server_config.json");
        if (File.Exists(cfgPath)) data.Server = Read<ServerConfig>(serverDir, "server_config.json");
        return data;
    }

    /// <summary>For tests: build from in-memory tables (still validated).</summary>
    public static GameData Build(SharedData s, SpawnDef[] spawns, DropTableDef[] drops, string repoRoot = ".")
    {
        s.Index();
        var d = new GameData(repoRoot)
        {
            Shared = s,
            Spawns = spawns ?? Array.Empty<SpawnDef>(),
            Drops = new Dictionary<string, DropTableDef>(),
        };
        var errors = new List<string>();
        foreach (var t in drops ?? Array.Empty<DropTableDef>())
        {
            if (string.IsNullOrEmpty(t.id)) { errors.Add("drop_tables: entry without id"); continue; }
            if (!d.Drops.TryAdd(t.id, t)) errors.Add($"drop_tables: duplicate id '{t.id}'");
        }
        DataValidator.Validate(s, d.Spawns, d.Drops, errors);
        if (errors.Count > 0)
            throw new InvalidDataException($"데이터 테이블 오류 {errors.Count}건:\n  " + string.Join("\n  ", errors));
        d.SharedJson = JsonSerializer.Serialize(s, JsonOpts);
        return d;
    }

    void SetShops(ShopDef[] shops)
    {
        var errors = new List<string>();
        DataValidator.ValidateShops(Shared, shops, errors);
        if (errors.Count > 0) throw new InvalidDataException($"데이터 테이블 오류 {errors.Count}건: " + string.Join(" / ", errors));
        Shops =shops.ToDictionary(x => x.id);
    }

    static T Read<T>(string dir, string file) where T : class
    {
        var path = Path.Combine(dir, file);
        if (!File.Exists(path)) throw new FileNotFoundException($"데이터 파일이 없어: {path}");
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOpts)
                   ?? throw new InvalidDataException($"{file} is empty");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{file}: JSON 형식 오류 (줄 {e.LineNumber + 1}): {e.Message}");
        }
    }

    /// <summary>Walk up from the executable until a folder containing data/shared is found.</summary>
    public static string FindRepoRoot(string start = null)
    {
        var dir = new DirectoryInfo(start ?? AppContext.BaseDirectory);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "shared"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not find repo root (folder with data/shared). Pass --root <path>.");
    }
}
