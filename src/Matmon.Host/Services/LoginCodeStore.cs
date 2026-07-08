using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Matmon.Host.Services;

/// <summary>
/// In-memory one-time e-mail login codes - the 2FA fallback when the authenticator isn't available (login AND
/// disable). Codes are 6-digit, stored only as a hash, short-lived, single-use, rate-limited on re-send, and
/// locked out after a few wrong tries. In-memory only: a restart drops pending codes (fine for a 10-minute code).
/// </summary>
public sealed class LoginCodeStore
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MinResend = TimeSpan.FromSeconds(45);
    private const int MaxAttempts = 5;

    private sealed class Entry
    {
        public string Hash = string.Empty;
        public DateTimeOffset IssuedUtc;
        public DateTimeOffset ExpiresUtc;
        public int Attempts;
    }

    private readonly ConcurrentDictionary<Guid, Entry> _codes = new();

    /// <summary>Issue a fresh 6-digit code. Returns null if one was issued too recently (rate limit) - the existing
    /// code is still valid.</summary>
    public string? Issue(Guid userId, DateTimeOffset now)
    {
        if (_codes.TryGetValue(userId, out var existing) && now - existing.IssuedUtc < MinResend)
        {
            return null;
        }
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        _codes[userId] = new Entry { Hash = Hash(code), IssuedUtc = now, ExpiresUtc = now + Ttl, Attempts = 0 };
        return code;
    }

    /// <summary>Verify + consume a code. Single-use; expires; locks out after <see cref="MaxAttempts"/> tries.</summary>
    public bool Verify(Guid userId, string? code, DateTimeOffset now)
    {
        if (!_codes.TryGetValue(userId, out var entry)) { return false; }
        if (now > entry.ExpiresUtc || entry.Attempts >= MaxAttempts)
        {
            _codes.TryRemove(userId, out _);
            return false;
        }
        entry.Attempts++;
        var ok = CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(Hash((code ?? string.Empty).Trim())),
            Encoding.ASCII.GetBytes(entry.Hash));
        if (ok) { _codes.TryRemove(userId, out _); }
        return ok;
    }

    private static string Hash(string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
}
