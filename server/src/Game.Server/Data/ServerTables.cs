namespace Game.Server.Data;

// Schemas for data/server/*.json — never sent to clients (drop chances, spawn layout).

public sealed class SpawnTable
{
    public SpawnDef[] spawns = Array.Empty<SpawnDef>();
}

public sealed class SpawnDef
{
    public string monster;
    public float x, z, radius;
    public int count = 1;
}

public sealed class DropTableFile
{
    public DropTableDef[] dropTables = Array.Empty<DropTableDef>();
}

public sealed class DropTableDef
{
    public string id;
    public int goldMin, goldMax;
    public DropItemDef[] items = Array.Empty<DropItemDef>();
}

public sealed class DropItemDef
{
    public string item;
    public float chance;
    public int min = 1, max = 1;
    /// <summary>Equipment drops: grade id → weight. Empty = a plain stack.</summary>
    public Dictionary<string, int> grades = new();
}

public sealed class ShopEntryDef
{
    public string item;
    public string grade = "common";
}

public sealed class ShopDef
{
    public string id;
    public string name;
    /// <summary>The NPC who runs it.</summary>
    public string npc;
    public ShopEntryDef[] entries = Array.Empty<ShopEntryDef>();
}

public sealed class ShopFile
{
    public ShopDef[] shops = Array.Empty<ShopDef>();
}

/// <summary>data/server/server_config.json — never sent to clients.</summary>
public sealed class ServerConfig
{
    /// <summary>Nicknames allowed to use the dev commands in chat. The account's password is the proof (an admin nickname can only be created / first claimed at the server PC).</summary>
    public string[] admins = Array.Empty<string>();

    public bool IsAdmin(string name) => admins.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
}
