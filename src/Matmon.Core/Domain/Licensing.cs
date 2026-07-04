using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Matmon.Core.Domain;

public enum LicenseTier
{
    Free = 0,
    Business = 1,
    Enterprise = 2
}

/// <summary>
/// A validated license for an instance. Issued (signed) by Matmon.Cloud and verified locally, offline,
/// against a baked-in public key — so the monitor keeps enforcing the last known license even when the
/// cloud is unreachable.
/// </summary>
public sealed class LicenseInfo
{
    /// <summary>Sentinel probe limit meaning "no limit".</summary>
    public const int Unlimited = -1;

    public LicenseTier Tier { get; init; } = LicenseTier.Free;

    /// <summary>Max number of probes (nodes) allowed; <see cref="Unlimited"/> for no limit.</summary>
    public int ProbeLimit { get; init; }

    public DateTimeOffset? ExpiresUtc { get; init; }

    public DateTimeOffset IssuedUtc { get; init; }

    public string? InstanceId { get; init; }

    /// <summary>True when this is the built-in fallback (no signed license present).</summary>
    public bool IsFallback { get; init; }

    public bool IsExpired(DateTimeOffset nowUtc) => ExpiresUtc is { } expiry && expiry < nowUtc;

    public bool IsUnlimited => ProbeLimit == Unlimited;

    public string ProbeLimitDisplay => IsUnlimited ? "unlimited" : ProbeLimit.ToString();

    /// <summary>The default license when none is present: Free, a small probe allowance.</summary>
    public static LicenseInfo Fallback() => new()
    {
        Tier = LicenseTier.Free,
        ProbeLimit = 1,
        IssuedUtc = DateTimeOffset.UnixEpoch,
        IsFallback = true
    };
}

/// <summary>
/// Signs/verifies license tokens (ECDSA P-256 over a compact JSON payload).
/// Token = base64url(payload-json) + "." + base64url(signature). The cloud signs with its private key;
/// the local instance verifies with the baked public key (<see cref="LicensePublicKey.Spki"/>).
/// </summary>
public static class LicenseCrypto
{
    private sealed record Payload(int t, int p, long e, long iss, string id);

    public static string Sign(LicenseInfo license, string privateKeyPkcs8Base64)
    {
        var payload = new Payload(
            (int)license.Tier,
            license.ProbeLimit,
            license.ExpiresUtc?.ToUnixTimeSeconds() ?? 0,
            license.IssuedUtc.ToUnixTimeSeconds(),
            license.InstanceId ?? string.Empty);

        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKeyPkcs8Base64), out _);
        var signature = ecdsa.SignData(payloadBytes, HashAlgorithmName.SHA256);
        return $"{Base64Url.Encode(payloadBytes)}.{Base64Url.Encode(signature)}";
    }

    public static LicenseInfo? Verify(string? token, string publicKeySpkiBase64)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var parts = token.Split('.');
        if (parts.Length != 2)
        {
            return null;
        }

        try
        {
            var payloadBytes = Base64Url.Decode(parts[0]);
            var signature = Base64Url.Decode(parts[1]);

            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeySpkiBase64), out _);
            if (!ecdsa.VerifyData(payloadBytes, signature, HashAlgorithmName.SHA256))
            {
                return null;
            }

            var payload = JsonSerializer.Deserialize<Payload>(payloadBytes);
            if (payload is null)
            {
                return null;
            }

            return new LicenseInfo
            {
                Tier = (LicenseTier)payload.t,
                ProbeLimit = payload.p,
                ExpiresUtc = payload.e > 0 ? DateTimeOffset.FromUnixTimeSeconds(payload.e) : null,
                IssuedUtc = DateTimeOffset.FromUnixTimeSeconds(payload.iss),
                InstanceId = string.IsNullOrEmpty(payload.id) ? null : payload.id
            };
        }
        catch
        {
            return null;
        }
    }

    private static class Base64Url
    {
        public static string Encode(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        public static byte[] Decode(string value)
        {
            var s = value.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
            }
            return Convert.FromBase64String(s);
        }
    }
}

/// <summary>The baked-in public key used to verify cloud-issued license tokens offline.</summary>
public static class LicensePublicKey
{
    public const string Spki = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEy5cClV5DsCmAxTzv1eMMS1EhuSiv7leBuJS5zxul5rBs1XJ7kx9u4LgaTa2h2uLs0uZ+z4KhqG6m14opg0afkQ==";
}
