using System.Security.Cryptography;

namespace ETBattleRelay;

internal sealed class PasswordVerifier
{
    private const int Iterations = 150_000;
    private readonly byte[] _salt;
    private readonly byte[] _hash;
    private PasswordVerifier(byte[] salt, byte[] hash) { _salt = salt; _hash = hash; }

    internal static PasswordVerifier? Create(string? password)
    {
        if (string.IsNullOrEmpty(password)) return null;
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return new PasswordVerifier(salt, hash);
    }

    internal bool Verify(string? password)
    {
        if (password is null) return false;
        var candidate = Rfc2898DeriveBytes.Pbkdf2(password, _salt, Iterations, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(_hash, candidate);
    }
}

internal static class RelaySecrets
{
    private const string RoomAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    internal static string RoomCode()
    {
        Span<byte> random = stackalloc byte[8];
        RandomNumberGenerator.Fill(random);
        Span<char> code = stackalloc char[8];
        for (var i = 0; i < code.Length; i++) code[i] = RoomAlphabet[random[i] % RoomAlphabet.Length];
        return new string(code);
    }
    internal static string Token()
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        return token.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
    internal static byte[] TokenVerifier(string token) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
}

internal sealed class SlidingWindowLimiter
{
    private readonly object _gate = new();
    private DateTimeOffset _windowStart = DateTimeOffset.UtcNow;
    private long _count;
    private readonly TimeSpan _window;
    private readonly long _limit;
    internal SlidingWindowLimiter(long limit, TimeSpan window) { _limit = Math.Max(1, limit); _window = window; }
    internal bool TryConsume(long amount = 1, DateTimeOffset? now = null)
    {
        lock (_gate)
        {
            var current = now ?? DateTimeOffset.UtcNow;
            if (current - _windowStart >= _window) { _windowStart = current; _count = 0; }
            if (amount < 0 || _count + amount > _limit) return false;
            _count += amount;
            return true;
        }
    }
}
