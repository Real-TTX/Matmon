using System.Security.Cryptography;
using System.Text;

namespace Matmon.Core.Domain;

/// <summary>
/// A token for the instance's own API (System > API tokens): what a CI/CD job, a deploy script or another tool presents
/// instead of a user's sign-in. It carries SCOPES - what it may do (today: maintenance windows) - an optional expiry, and is
/// revoked by hand. Only a SHA-256 hash of the secret is kept, so the secret is shown exactly once when it is created;
/// <see cref="Prefix"/> (its first characters) is what the list shows to tell tokens apart. Instance-bound: never in a backup.
/// </summary>
public sealed class ApiToken
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    /// <summary>The first characters of the secret ("mmt_AbC1"), for the list. Not enough to use it.</summary>
    public string Prefix { get; set; } = string.Empty;

    /// <summary>SHA-256 of the secret, lower-case hex.</summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>What it may do (<see cref="ApiTokenScopes"/>).</summary>
    public List<string> Scopes { get; set; } = [];

    public string? CreatedBy { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    /// <summary>After this it is refused; null = until revoked.</summary>
    public DateTimeOffset? ExpiresUtc { get; set; }

    public DateTimeOffset? LastUsedUtc { get; set; }

    /// <summary>The address it was last used from.</summary>
    public string? LastUsedFrom { get; set; }

    public DateTimeOffset? RevokedUtc { get; set; }

    public bool IsExpired(DateTimeOffset now) => ExpiresUtc is { } end && end <= now;

    public bool IsUsable(DateTimeOffset now) => RevokedUtc is null && !IsExpired(now);

    public bool Allows(string scope) => Scopes.Contains(scope, StringComparer.OrdinalIgnoreCase);

    public ApiToken Clone() => new()
    {
        Id = Id,
        Name = Name,
        Prefix = Prefix,
        Hash = Hash,
        Scopes = [.. Scopes],
        CreatedBy = CreatedBy,
        CreatedUtc = CreatedUtc,
        ExpiresUtc = ExpiresUtc,
        LastUsedUtc = LastUsedUtc,
        LastUsedFrom = LastUsedFrom,
        RevokedUtc = RevokedUtc
    };
}

/// <summary>What an API token may do. A new scope = a constant here and its entry in <see cref="All"/>.</summary>
public static class ApiTokenScopes
{
    /// <summary>Read, create, switch on and off and delete maintenance windows.</summary>
    public const string Maintenance = "maintenance";

    public static IReadOnlyList<(string Key, string Title, string Description)> All { get; } =
    [
        (Maintenance, "Maintenance windows", "List, create, switch on and off and delete maintenance windows - for a deploy job that silences the alerts of what it restarts.")
    ];

    public static bool IsKnown(string scope) => All.Any(entry => string.Equals(entry.Key, scope, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Making and checking the secret of an API token.</summary>
public static class ApiTokenSecret
{
    /// <summary>Every secret starts with this, so a leaked one is recognised by secret scanners and by people.</summary>
    public const string Marker = "mmt_";

    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
    private const int RandomLength = 40; // ~238 bits

    /// <summary>The longest expiry offered; a token for longer is "until revoked", a decision the admin makes in words.</summary>
    public static readonly TimeSpan MaxValidity = TimeSpan.FromDays(366 * 2);

    public static string Generate()
    {
        var builder = new StringBuilder(Marker, Marker.Length + RandomLength);
        for (var index = 0; index < RandomLength; index++)
        {
            builder.Append(Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]);
        }
        return builder.ToString();
    }

    public static string Hash(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret.Trim()))).ToLowerInvariant();

    public static string PrefixOf(string secret) => secret.Trim()[..Math.Min(secret.Trim().Length, Marker.Length + 6)];

    /// <summary>Whether the stored hash belongs to this secret, compared in constant time.</summary>
    public static bool Matches(string storedHash, string secret)
    {
        var candidate = Encoding.ASCII.GetBytes(Hash(secret));
        var stored = Encoding.ASCII.GetBytes(storedHash);
        return CryptographicOperations.FixedTimeEquals(candidate, stored);
    }

    /// <summary>The secret from a request's <c>Authorization: Bearer ...</c> or <c>X-Matmon-Token</c> header, or null.</summary>
    public static string? FromHeaders(string? authorization, string? tokenHeader)
    {
        if (!string.IsNullOrWhiteSpace(tokenHeader))
        {
            return tokenHeader.Trim();
        }
        if (authorization is { } auth && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var value = auth["Bearer ".Length..].Trim();
            return value.Length == 0 ? null : value;
        }
        return null;
    }
}
