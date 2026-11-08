using Game.Server.Db;
using Game.Server.Logic;
using Game.Shared.Protocol;
using Npgsql;
using Xunit;

namespace Game.Server.Tests;

/// <summary>
/// Real PostgreSQL round trip against a throwaway database (rpg_test). Skips itself when the dev DB is not running
/// (start it with tools\db-start.cmd).
/// </summary>
public class PgStoreTests
{
    const string Cs = "Host=localhost;Port=5433;Username=rpg;Database=rpg_test;Timeout=3";

    static bool DbAvailable()
    {
        try
        {
            using var c = new NpgsqlConnection("Host=localhost;Port=5433;Username=rpg;Database=postgres;Timeout=2");
            c.Open();
            return true;
        }
        catch { return false; }
    }

    [Fact]
    public async Task Character_round_trip_and_unique_names()
    {
        if (!DbAvailable()) return; // no DB on this machine: nothing to test
        await PgCharacterStore.PrepareAsync(Cs);
        await PgCharacterStore.PrepareAsync(Cs); // idempotent
        var store = new PgCharacterStore(Cs);
        var data = Game.Server.Data.GameData.Load(Game.Server.Data.GameData.FindRepoRoot()).Shared;
        string name = "t" + Guid.NewGuid().ToString("N")[..8];

        var c = await store.LoadOrCreateAsync(name, () => Rules.NewCharacter(data, name, "dawn_village"));
        Assert.True(c.Id > 0);
        Assert.Equal(1, c.Level);
        Assert.False(c.HasPosition);

        c.Level = 2; c.Exp = 7; c.Gold = 33; c.StatPoints = 5; c.Hp = 123;
        c.HasPosition = true; c.X = 1.5f; c.Y = 0.25f; c.Z = -40; c.Yaw = 90;
        c.Unlocks = UnlockFlags.Equipment | UnlockFlags.LeaveStartArea;
        c.Items = new() { new ItemRow(ItemContainer.Equipment, 0, "wood_club", 1), new ItemRow(ItemContainer.Bag, 3, "rabbit_foot", 2) };
        c.Quests = new() { new QuestRow("q_rabbit_liver", QuestState.Completed, new[] { 0 }) };
        await store.SaveAsync(c, new[] { new AuditEntry("quest_completed", "{\"quest\":\"q_rabbit_liver\"}") });

        // Different letter case = same account (unique, case-insensitive).
        var back = await store.LoadOrCreateAsync(name.ToUpperInvariant(), () => throw new Exception("should not create"));
        Assert.Equal(c.Id, back.Id);
        Assert.Equal(2, back.Level);
        Assert.Equal(7, back.Exp);
        Assert.Equal(33, back.Gold);
        Assert.Equal(123, back.Hp);
        Assert.True(back.HasPosition);
        Assert.Equal(-40, back.Z, 2);
        Assert.Equal(c.Unlocks, back.Unlocks);
        Assert.Equal(2, back.Items.Count);
        Assert.Contains(back.Items, i => i.Container == ItemContainer.Bag && i.Slot == 3 && i.ItemId == "rabbit_foot" && i.Count == 2);
        Assert.Equal(QuestState.Completed, back.Quests.Single().State);

        // Saving fewer items removes the rest.
        back.Items = new() { new ItemRow(ItemContainer.Equipment, 0, "wood_club", 1) };
        await store.SaveAsync(back, Array.Empty<AuditEntry>());
        var again = await store.LoadOrCreateAsync(name, () => throw new Exception("no"));
        Assert.Single(again.Items);
    }

    [Fact]
    public async Task Passwords_are_checked_in_the_real_database()
    {
        if (!DbAvailable()) return;
        await PgCharacterStore.PrepareAsync(Cs);
        var store = new PgCharacterStore(Cs);
        var data = Game.Server.Data.GameData.Load(Game.Server.Data.GameData.FindRepoRoot()).Shared;
        string name = "a" + Guid.NewGuid().ToString("N")[..8];
        var remote = new LoginAuth("pw1234", Create: true, Local: false, AdminName: false);

        // No account and no "계정 만들기": refused.
        var no = await Assert.ThrowsAsync<LoginException>(() => store.AuthenticateAsync(name, remote with { Create = false }));
        Assert.Equal(LoginFailure.NoAccount, no.Reason);
        // Create, then the right and a wrong password.
        await store.AuthenticateAsync(name, remote);
        await store.LoadOrCreateAsync(name, () => Rules.NewCharacter(data, name, "dawn_village"));
        await store.AuthenticateAsync(name.ToUpperInvariant(), remote with { Create = false });
        var bad = await Assert.ThrowsAsync<LoginException>(() => store.AuthenticateAsync(name, remote with { Create = false, Password = "other-pw" }));
        Assert.Equal(LoginFailure.WrongPassword, bad.Reason);

        // An account without a password (old): only the server PC can claim it.
        string old = "o" + Guid.NewGuid().ToString("N")[..8];
        await store.LoadOrCreateAsync(old, () => Rules.NewCharacter(data, old, "dawn_village"));
        var far = await Assert.ThrowsAsync<LoginException>(() => store.AuthenticateAsync(old, remote with { Create = false }));
        Assert.Equal(LoginFailure.NeedLocal, far.Reason);
        await store.AuthenticateAsync(old, new LoginAuth("claimed-1", false, Local: true, AdminName: false));
        await store.AuthenticateAsync(old, remote with { Create = false, Password = "claimed-1" });
    }
}
