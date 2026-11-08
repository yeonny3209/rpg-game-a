using System.Security.Cryptography;

namespace Game.Server.Db;

public enum LoginFailure
{
    /// <summary>No such account and "계정 만들기" was not chosen.</summary>
    NoAccount,
    WrongPassword,
    /// <summary>The nickname already exists (creating was requested with a different password).</summary>
    NameTaken,
    /// <summary>An old account (from before passwords) or an admin nickname: its password can only be set from the server PC.</summary>
    NeedLocal,
    BadPassword,
    TooManyTries,
}

public sealed class LoginException : Exception
{
    public LoginFailure Reason { get; }
    public LoginException(LoginFailure reason, string message) : base(message) { Reason = reason; }

    public static LoginException Of(LoginFailure r) => new(r, r switch
    {
        LoginFailure.NoAccount => "없는 닉네임이야. 새로 만들려면 '계정 만들기'를 눌러 줘.",
        LoginFailure.WrongPassword => "닉네임이나 비밀번호가 맞지 않아요.",
        LoginFailure.NameTaken => "이미 있는 닉네임이에요. 다른 닉네임을 쓰거나, 내 계정이면 비밀번호를 확인해 줘.",
        LoginFailure.NeedLocal => "이 닉네임은 서버를 켠 PC(localhost)에서 처음 접속해 비밀번호를 정해야 해요.",
        LoginFailure.BadPassword => $"비밀번호는 {PasswordRules.Min}~{PasswordRules.Max}자로 정해 줘.",
        LoginFailure.TooManyTries => "비밀번호를 여러 번 틀려서 잠시 잠겼어요. 몇 분 뒤에 다시 해 줘.",
        _ => "로그인하지 못했어요.",
    });
}

/// <summary>What the login handshake carries into the DB check.</summary>
/// <param name="Create">The player pressed "계정 만들기".</param>
/// <param name="Local">The connection comes from the server PC itself (not through the public link).</param>
/// <param name="AdminName">The nickname is in server_config.json admins.</param>
public sealed record LoginAuth(string Password, bool Create, bool Local, bool AdminName);

public static class PasswordRules
{
    public const int Min = 4, Max = 32;
    public static bool Valid(string p) => p != null && p.Length >= Min && p.Length <= Max;

    public static bool IsLocalAddress(string a) => a is "127.0.0.1" or "::1" or "::ffff:127.0.0.1" or "test";
}

/// <summary>PBKDF2-HMAC-SHA256 with a random salt per account. Stored as "pbkdf2$iterations$salt$hash" (base64).</summary>
public static class PasswordHasher
{
    const int Iterations = 120_000, SaltBytes = 16, HashBytes = 32;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"pbkdf2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        if (string.IsNullOrEmpty(stored)) return false;
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2" || !int.TryParse(parts[1], out int iter) || iter < 1) return false;
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var want = Convert.FromBase64String(parts[3]);
            var got = Rfc2898DeriveBytes.Pbkdf2(password ?? "", salt, iter, HashAlgorithmName.SHA256, want.Length);
            return CryptographicOperations.FixedTimeEquals(got, want);
        }
        catch (FormatException) { return false; }
    }
}

/// <summary>Wrong-password limiter: 5 misses lock that address / nickname for 5 minutes. The server PC itself is never locked.</summary>
public sealed class LoginThrottle
{
    public const int MaxFails = 5;
    public const double LockSeconds = 300;
    readonly Dictionary<string, (int fails, double lockedUntil)> _state = new();

    static string Key(string kind, string v) => kind + ":" + (v ?? "").ToLowerInvariant();

    public bool IsLocked(string address, string name, double now) =>
        !PasswordRules.IsLocalAddress(address) && (Locked(Key("ip", address), now) || Locked(Key("name", name), now));

    bool Locked(string key, double now) => _state.TryGetValue(key, out var s) && s.lockedUntil > now;

    public void Fail(string address, string name, double now)
    {
        if (!PasswordRules.IsLocalAddress(address)) Bump(Key("ip", address), now);
        Bump(Key("name", name), now);
    }

    void Bump(string key, double now)
    {
        _state.TryGetValue(key, out var s);
        if (s.lockedUntil != 0 && s.lockedUntil <= now) s = (0, 0);   // the lock ran out: start counting again
        s.fails++;
        if (s.fails >= MaxFails) { s.lockedUntil = now + LockSeconds; s.fails = 0; }
        _state[key] = s;
    }

    public void Success(string address, string name)
    {
        _state.Remove(Key("ip", address));
        _state.Remove(Key("name", name));
    }
}
