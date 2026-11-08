using System.Collections.Concurrent;
using System.Reflection;
using Dapper;
using Game.Server.Logic;
using Game.Server.Net;
using Game.Shared.Protocol;
using Npgsql;

namespace Game.Server.Db;

public sealed record AuditEntry(string Kind, string DataJson);

/// <summary>Where characters live. Postgres in real runs, memory in tests / --db memory.</summary>
public interface ICharacterStore
{
    /// <summary>Loads the character of this login, creating account + character on first login.</summary>
    Task<CharacterRecord> LoadOrCreateAsync(string loginName, Func<CharacterRecord> createNew);
    /// <summary>Checks the password (or creates / claims the account). Throws LoginException when the login must be refused.</summary>
    Task AuthenticateAsync(string loginName, LoginAuth auth);
    Task SaveAsync(CharacterRecord c, IReadOnlyList<AuditEntry> audits);
    string Describe { get; }
}

public sealed class MemoryCharacterStore : ICharacterStore
{
    readonly ConcurrentDictionary<string, CharacterRecord> _byLogin = new(StringComparer.OrdinalIgnoreCase);
    long _nextId;
    public string Describe => "memory (저장 안 됨: 서버를 끄면 사라짐)";
    public int SaveCount;
    public readonly ConcurrentQueue<AuditEntry> Audits = new();

    // login name → password hash (null/absent = an account from before passwords)
    readonly ConcurrentDictionary<string, string> _passwords = new(StringComparer.OrdinalIgnoreCase);

    public Task AuthenticateAsync(string loginName, LoginAuth auth)
    {
        bool exists = _byLogin.ContainsKey(loginName);
        _passwords.TryGetValue(loginName, out var hash);
        AuthRules.Check(exists, hash, auth, out bool setNew);
        if (setNew) _passwords[loginName] = PasswordHasher.Hash(auth.Password);
        return Task.CompletedTask;
    }

    public Task<CharacterRecord> LoadOrCreateAsync(string loginName, Func<CharacterRecord> createNew)
    {
        var c = _byLogin.GetOrAdd(loginName, _ =>
        {
            var n = createNew();
            n.Id = Interlocked.Increment(ref _nextId);
            n.AccountId = n.Id;
            return n;
        });
        return Task.FromResult(c.Clone());
    }

    public Task SaveAsync(CharacterRecord c, IReadOnlyList<AuditEntry> audits)
    {
        _byLogin[c.Name] = c.Clone();
        Interlocked.Increment(ref SaveCount);
        foreach (var a in audits) Audits.Enqueue(a);
        return Task.CompletedTask;
    }
}

public sealed class PgCharacterStore : ICharacterStore
{
    readonly string _cs;
    public PgCharacterStore(string connectionString) { _cs = connectionString; }
    public string Describe => "PostgreSQL " + new NpgsqlConnectionStringBuilder(_cs) { Password = null }.ConnectionString;

    /// <summary>Creates the database if missing and applies pending migrations (embedded Db/Migrations/*.sql, in name order).</summary>
    public static async Task PrepareAsync(string connectionString)
    {
        var b = new NpgsqlConnectionStringBuilder(connectionString);
        string dbName = b.Database ?? "rpg";
        var adminCs = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres" }.ConnectionString;
        await using (var admin = new NpgsqlConnection(adminCs))
        {
            await admin.OpenAsync();
            bool exists = await admin.ExecuteScalarAsync<int?>("select 1 from pg_database where datname = @n", new { n = dbName }) != null;
            if (!exists)
            {
                // Identifier cannot be a parameter; dbName comes from our own config, quote it anyway.
                await admin.ExecuteAsync($"create database \"{dbName.Replace("\"", "")}\" encoding 'UTF8'");
                Log.Info($"DB '{dbName}' 새로 만듦");
            }
        }

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("create table if not exists schema_migrations (name text primary key, applied_at timestamptz not null default now())");
        var applied = (await conn.QueryAsync<string>("select name from schema_migrations")).ToHashSet();
        var asm = Assembly.GetExecutingAssembly();
        foreach (var res in asm.GetManifestResourceNames().Where(n => n.StartsWith("migrations/")).OrderBy(n => n, StringComparer.Ordinal))
        {
            string name = res["migrations/".Length..];
            if (applied.Contains(name)) continue;
            using var reader = new StreamReader(asm.GetManifestResourceStream(res)!);
            string sql = await reader.ReadToEndAsync();
            await using var tx = await conn.BeginTransactionAsync();
            await conn.ExecuteAsync(sql, transaction: tx);
            await conn.ExecuteAsync("insert into schema_migrations(name) values (@name)", new { name }, tx);
            await tx.CommitAsync();
            Log.Info($"DB migration 적용: {name}");
        }
    }

    sealed class CharRow
    {
        public long id { get; set; }
        public long account_id { get; set; }
        public string name { get; set; }
        public int level { get; set; }
        public long exp { get; set; }
        public long gold { get; set; }
        public int stat_points { get; set; }
        public int str { get; set; }
        public int dex { get; set; }
        public int int_ { get; set; }
        public int con { get; set; }
        public int spi { get; set; }
        public int hp { get; set; }
        public string zone { get; set; }
        public bool has_position { get; set; }
        public float pos_x { get; set; }
        public float pos_y { get; set; }
        public float pos_z { get; set; }
        public float yaw { get; set; }
        public int unlocks { get; set; }
        public short gender { get; set; }
        public string job { get; set; }
        public short job_tier { get; set; }
        public long training { get; set; }
        public int training_today { get; set; }
        public int training_day { get; set; }
        public int mana { get; set; }
        public string element { get; set; }
        public short circle { get; set; }
        public int holy { get; set; }
        public int bag_slots { get; set; }
        public DateTime last_level_up_at { get; set; }
    }

    sealed class ItemDbRow
    {
        public short container { get; set; }
        public short slot { get; set; }
        public string item_id { get; set; }
        public int count { get; set; }
        public short grade { get; set; }
        public string opts { get; set; }
    }

    sealed class SkillDbRow
    {
        public string skill_id { get; set; }
        public short level { get; set; }
        public int uses { get; set; }
    }

    sealed class QuestDbRow
    {
        public string quest_id { get; set; }
        public short state { get; set; }
        public int[] counters { get; set; }
    }

    public async Task AuthenticateAsync(string loginName, LoginAuth auth)
    {
        await using var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var row = await conn.QuerySingleOrDefaultAsync<(long id, string hash)?>(
            "select id, password_hash from accounts where lower(login_name) = lower(@loginName) for update", new { loginName }, tx);
        AuthRules.Check(row != null, row?.hash, auth, out bool setNew);
        if (setNew)
        {
            string hash = PasswordHasher.Hash(auth.Password);
            if (row != null)
                await conn.ExecuteAsync("update accounts set password_hash = @hash where id = @id", new { hash, id = row.Value.id }, tx);
            else
            {
                int n = await conn.ExecuteAsync(
                    "insert into accounts(login_name, password_hash) values (@loginName, @hash) on conflict ((lower(login_name))) do nothing",
                    new { loginName, hash }, tx);
                if (n == 0) throw LoginException.Of(LoginFailure.NameTaken);   // someone created it a moment ago
            }
        }
        await tx.CommitAsync();
    }

    public async Task<CharacterRecord> LoadOrCreateAsync(string loginName, Func<CharacterRecord> createNew)
    {
        await using var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        long accountId = await conn.ExecuteScalarAsync<long>(
            @"insert into accounts(login_name, last_login_at) values (@loginName, now())
              on conflict ((lower(login_name))) do update set last_login_at = now()
              returning id", new { loginName }, tx);

        var row = await conn.QuerySingleOrDefaultAsync<CharRow>(
            "select * from characters where account_id = @accountId and slot = 0", new { accountId }, tx);

        CharacterRecord c;
        if (row == null)
        {
            c = createNew();
            c.AccountId = accountId;
            c.Id = await conn.ExecuteScalarAsync<long>(
                @"insert into characters(account_id, slot, name, level, exp, gold, stat_points, str, dex, int_, con, spi, hp, zone, unlocks, gender, last_level_up_at)
                  values (@AccountId, 0, @Name, @Level, @Exp, @Gold, @StatPoints, @Str, @Dex, @Int, @Con, @Spi, @Hp, @Zone, @UnlocksInt, @GenderInt, @LastLevelUpAt)
                  returning id",
                new { c.AccountId, c.Name, c.Level, c.Exp, c.Gold, c.StatPoints, c.Str, c.Dex, c.Int, c.Con, c.Spi, c.Hp, c.Zone, UnlocksInt = (int)c.Unlocks, GenderInt = (short)c.Gender, c.LastLevelUpAt }, tx);
            await conn.ExecuteAsync("insert into audit_log(character_id, kind) values (@id, 'character_created')", new { id = c.Id }, tx);
        }
        else
        {
            c = new CharacterRecord
            {
                Id = row.id, AccountId = row.account_id, Name = row.name, Level = row.level, Exp = row.exp, Gold = row.gold,
                StatPoints = row.stat_points, Str = row.str, Dex = row.dex, Int = row.int_, Con = row.con, Spi = row.spi,
                Hp = row.hp, Zone = row.zone, HasPosition = row.has_position, X = row.pos_x, Y = row.pos_y, Z = row.pos_z, Yaw = row.yaw,
                Unlocks = (UnlockFlags)row.unlocks, Gender = (Gender)row.gender, Job = row.job ?? "", JobTier = row.job_tier,
                Training = row.training, TrainingToday = row.training_today, TrainingDay = row.training_day,
                Mana = row.mana, Element = row.element ?? "", Circle = row.circle, Holy = row.holy, BagSlots = row.bag_slots, LastLevelUpAt = DateTime.SpecifyKind(row.last_level_up_at, DateTimeKind.Utc),
            };
            var items = await conn.QueryAsync<ItemDbRow>("select container, slot, item_id, count, grade, opts from character_items where character_id = @id", new { id = c.Id }, tx);
            c.Items = items.Select(i => new ItemRow((ItemContainer)i.container, i.slot, i.item_id, i.count, (byte)i.grade, i.opts ?? "")).ToList();
            var skills = await conn.QueryAsync<SkillDbRow>("select skill_id, level, uses from character_skills where character_id = @id", new { id = c.Id }, tx);
            c.Skills = skills.Select(k => new SkillRow(k.skill_id, k.level, k.uses)).ToList();
            var quests = await conn.QueryAsync<QuestDbRow>("select quest_id, state, counters from quest_progress where character_id = @id", new { id = c.Id }, tx);
            c.Quests = quests.Select(q => new QuestRow(q.quest_id, (QuestState)q.state, q.counters ?? Array.Empty<int>())).ToList();
        }
        await tx.CommitAsync();
        return c;
    }

    public async Task SaveAsync(CharacterRecord c, IReadOnlyList<AuditEntry> audits)
    {
        await using var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        int n = await conn.ExecuteAsync(
            @"update characters set level=@Level, exp=@Exp, gold=@Gold, stat_points=@StatPoints,
                 str=@Str, dex=@Dex, int_=@Int, con=@Con, spi=@Spi, hp=@Hp, zone=@Zone,
                 has_position=@HasPosition, pos_x=@X, pos_y=@Y, pos_z=@Z, yaw=@Yaw,
                 unlocks=@UnlocksInt, gender=@GenderInt, job=@Job, job_tier=@JobTierInt, training=@Training, training_today=@TrainingToday, training_day=@TrainingDay,
                 mana=@Mana, element=@Element, circle=@CircleInt, holy=@Holy, bag_slots=@BagSlots, last_level_up_at=@LastLevelUpAt, updated_at=now()
              where id=@Id",
            new { c.Id, c.Level, c.Exp, c.Gold, c.StatPoints, c.Str, c.Dex, c.Int, c.Con, c.Spi, c.Hp, c.Zone,
                  c.HasPosition, c.X, c.Y, c.Z, c.Yaw, UnlocksInt = (int)c.Unlocks, GenderInt = (short)c.Gender, c.Job, JobTierInt = (short)c.JobTier, c.Training, c.TrainingToday, c.TrainingDay,
                  c.Mana, Element = c.Element ?? "", CircleInt = (short)c.Circle, c.Holy, c.BagSlots, c.LastLevelUpAt }, tx);
        if (n != 1) throw new InvalidOperationException($"character {c.Id} not found");

        await conn.ExecuteAsync("delete from character_items where character_id=@Id", new { c.Id }, tx);
        if (c.Items.Count > 0)
        {
            await conn.ExecuteAsync(
                @"insert into character_items(character_id, container, slot, item_id, count, grade, opts)
                  select @Id, unnest(@containers), unnest(@slots), unnest(@ids), unnest(@counts), unnest(@grades), unnest(@optss)",
                new
                {
                    c.Id,
                    containers = c.Items.Select(i => (short)i.Container).ToArray(),
                    slots = c.Items.Select(i => (short)i.Slot).ToArray(),
                    ids = c.Items.Select(i => i.ItemId).ToArray(),
                    counts = c.Items.Select(i => i.Count).ToArray(),
                    grades = c.Items.Select(i => (short)i.Grade).ToArray(),
                    optss = c.Items.Select(i => i.Opts ?? "").ToArray(),
                }, tx);
        }

        await conn.ExecuteAsync("delete from character_skills where character_id=@Id", new { c.Id }, tx);
        foreach (var k in c.Skills)
            await conn.ExecuteAsync("insert into character_skills(character_id, skill_id, level, uses) values (@Id, @SkillId, @Level, @Uses)",
                new { c.Id, k.SkillId, Level = (short)k.Level, k.Uses }, tx);

        await conn.ExecuteAsync("delete from quest_progress where character_id=@Id", new { c.Id }, tx);
        foreach (var q in c.Quests)
        {
            await conn.ExecuteAsync(
                "insert into quest_progress(character_id, quest_id, state, counters) values (@Id, @QuestId, @State, @Counters)",
                new { c.Id, q.QuestId, State = (short)q.State, q.Counters }, tx);
        }

        foreach (var a in audits)
        {
            await conn.ExecuteAsync("insert into audit_log(character_id, kind, data) values (@Id, @Kind, @Data::jsonb)",
                new { c.Id, a.Kind, Data = a.DataJson ?? "null" }, tx);
        }
        await tx.CommitAsync();
    }
}

/// <summary>The one set of login rules, shared by both stores.</summary>
public static class AuthRules
{
    /// <param name="exists">The account row exists.</param>
    /// <param name="hash">Its password hash (null for an old account).</param>
    /// <param name="setNew">True when auth.Password must be stored as the account's password.</param>
    public static void Check(bool exists, string hash, LoginAuth auth, out bool setNew)
    {
        setNew = false;
        string pw = auth?.Password ?? "";
        if (!exists)
        {
            if (auth == null || !auth.Create) throw LoginException.Of(LoginFailure.NoAccount);
            if (!PasswordRules.Valid(pw)) throw LoginException.Of(LoginFailure.BadPassword);
            if (auth.AdminName && !auth.Local) throw LoginException.Of(LoginFailure.NeedLocal);   // an admin nickname can only be created at the server PC
            setNew = true;
            return;
        }
        if (string.IsNullOrEmpty(hash))
        {
            // Account from before passwords: whoever sits at the server PC claims it first.
            if (!auth.Local) throw LoginException.Of(LoginFailure.NeedLocal);
            if (!PasswordRules.Valid(pw)) throw LoginException.Of(LoginFailure.BadPassword);
            setNew = true;
            return;
        }
        if (!PasswordHasher.Verify(pw, hash))
            throw LoginException.Of(auth.Create ? LoginFailure.NameTaken : LoginFailure.WrongPassword);
    }
}
