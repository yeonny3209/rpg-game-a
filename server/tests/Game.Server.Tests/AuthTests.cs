using Game.Server.Db;
using Game.Server.Logic;
using Game.Shared.Protocol;
using Xunit;

namespace Game.Server.Tests;

/// <summary>Password login: accounts, old accounts, admin nicknames, lockout.</summary>
public class AuthTests
{
    const string Visitor = "203.0.113.7";   // someone coming through the public link

    static (FakeConnection c, Welcome w, string fatal) Login(ZoneHarness h, string name, string pw, bool create, string ip = "test")
    {
        var c = new FakeConnection { RemoteAddress = ip };
        h.Send(c, new Hello { ProtocolVersion = ProtocolInfo.Version, Name = name, Password = pw, Create = create });
        h.Tick();
        h.FlushDb();
        h.Tick();
        var w = c.Take<Welcome>().FirstOrDefault();
        var fatal = c.Take<Notice>().FirstOrDefault(n => n.Kind == NoticeKind.Fatal)?.Text;
        return (c, w, fatal);
    }

    [Fact]
    public void A_new_account_needs_create_and_a_password_of_4_to_32_characters()
    {
        var h = new ZoneHarness();
        var (_, w1, f1) = Login(h, "Newbie", "pw1234", create: false, Visitor);
        Assert.Null(w1);
        Assert.Contains("없는 닉네임", f1);

        var (_, w2, f2) = Login(h, "Newbie", "ab", create: true, Visitor);
        Assert.Null(w2);
        Assert.Contains("비밀번호는 4~32자", f2);

        var (_, w3, _) = Login(h, "Newbie", new string('x', 33), create: true, Visitor);
        Assert.Null(w3);

        var (_, w4, f4) = Login(h, "Newbie", "pw1234", create: true, Visitor);
        Assert.NotNull(w4);
        Assert.Null(f4);
    }

    [Fact]
    public void An_existing_account_needs_its_own_password()
    {
        var h = new ZoneHarness();
        var (c1, w1, _) = Login(h, "Mina", "right-pass", create: true, Visitor);
        Assert.NotNull(w1);
        h.Leave(c1);
        h.FlushDb();

        var (_, w2, f2) = Login(h, "Mina", "wrong-pass", create: false, Visitor);
        Assert.Null(w2);
        Assert.Contains("맞지 않아요", f2);

        var (_, w3, f3) = Login(h, "mina", "wrong-pass", create: true, Visitor);   // "만들기" on someone's nickname does not get in
        Assert.Null(w3);
        Assert.Contains("이미 있는 닉네임", f3);

        var (_, w4, _) = Login(h, "MINA", "right-pass", create: false, Visitor);   // nickname is case-insensitive
        Assert.NotNull(w4);
    }

    [Fact]
    public void Old_accounts_without_a_password_are_claimed_only_at_the_server_pc()
    {
        var store = new MemoryCharacterStore();
        var h = new ZoneHarness(store);
        // An account made before passwords existed: it has a character but no password.
        store.LoadOrCreateAsync("Oldie", () => Rules.NewCharacter(h.Data.Shared, "Oldie", "dawn_village")).GetAwaiter().GetResult();

        var (_, w1, f1) = Login(h, "Oldie", "stolen-1", create: false, Visitor);
        Assert.Null(w1);
        Assert.Contains("서버를 켠 PC", f1);                                       // a stranger cannot grab it through the link

        var (c2, w2, _) = Login(h, "Oldie", "mine-1234", create: false, "test");   // the owner at the server PC sets the password
        Assert.NotNull(w2);
        h.Leave(c2);
        h.FlushDb();

        var (_, w3, _) = Login(h, "Oldie", "mine-1234", create: false, Visitor);   // from now on it works from anywhere with the password
        Assert.NotNull(w3);
    }

    [Fact]
    public void Admin_nicknames_are_created_at_the_server_pc_and_then_work_through_the_link_with_the_password()
    {
        var h = new ZoneHarness();
        var (_, w1, f1) = Login(h, "세윤", "boss-pass", create: true, Visitor);
        Assert.Null(w1);
        Assert.Contains("서버를 켠 PC", f1);                                       // a stranger cannot register the admin name first

        var (c2, w2, _) = Login(h, "세윤", "boss-pass", create: true, "test");
        Assert.NotNull(w2);
        h.Leave(c2);
        h.FlushDb();

        var (_, wBad, fBad) = Login(h, "세윤", "guess-pass", create: false, Visitor);
        Assert.Null(wBad);
        Assert.Contains("맞지 않아요", fBad);

        var (c3, w3, _) = Login(h, "세윤", "boss-pass", create: false, Visitor);   // the real admin through the public link
        Assert.NotNull(w3);
        var p = new TestPlayer(h, c3, w3);
        p.Tick();
        p.Chat("/골드 77");
        Assert.Equal(77, p.Char.Gold);                                               // dev commands work there
    }

    [Fact]
    public void Five_wrong_passwords_lock_the_address_for_a_while_but_never_the_server_pc()
    {
        var h = new ZoneHarness();
        var (c0, w0, _) = Login(h, "Target", "good-pass", create: true, Visitor);
        Assert.NotNull(w0);
        h.Leave(c0);
        h.FlushDb();

        const string attacker = "198.51.100.9";
        for (int i = 0; i < 5; i++) Assert.Null(Login(h, "Target", "nope" + i, create: false, attacker).w);
        var (_, wLocked, fLocked) = Login(h, "Target", "good-pass", create: false, attacker);
        Assert.Null(wLocked);
        Assert.Contains("잠시 잠겼", fLocked);                                       // even the right password waits

        var (_, wLocal, _) = Login(h, "Target", "good-pass", create: false, "test");  // the server PC is never locked out
        Assert.NotNull(wLocal);
    }

    [Fact]
    public void Passwords_are_stored_as_salted_hashes()
    {
        string a = PasswordHasher.Hash("secret-1"), b = PasswordHasher.Hash("secret-1");
        Assert.DoesNotContain("secret-1", a);
        Assert.NotEqual(a, b);                                   // a different salt each time
        Assert.True(PasswordHasher.Verify("secret-1", a));
        Assert.False(PasswordHasher.Verify("secret-2", a));
        Assert.False(PasswordHasher.Verify("secret-1", "garbage"));
        Assert.False(PasswordHasher.Verify("secret-1", null));
    }
}
