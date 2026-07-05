using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Matmon.Core.Domain;

public enum LicenseTier
{
    Free = 0,
    Business = 1,
    Enterprise = 2,
    Custom = 3
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

    /// <summary>The plan's display name from the cloud template (e.g. "Business-2026"). Null for legacy tokens.</summary>
    public string? PlanName { get; init; }

    /// <summary>Max number of probes (nodes) allowed; <see cref="Unlimited"/> for no limit.</summary>
    public int ProbeLimit { get; init; }

    /// <summary>Max number of sensors allowed; <see cref="Unlimited"/> for no limit.</summary>
    public int SensorLimit { get; init; } = Unlimited;

    /// <summary>Max cloud sensors allowed (informational on the instance; the cloud enforces its own).</summary>
    public int CloudSensorLimit { get; init; }

    /// <summary>Whether Full Access (the outbound tunnel) is included in the plan.</summary>
    public bool TunnelEnabled { get; init; } = true;

    /// <summary>Whether cloud sensors are included in the plan.</summary>
    public bool CloudSensorsEnabled { get; init; }

    public DateTimeOffset? ExpiresUtc { get; init; }

    public DateTimeOffset IssuedUtc { get; init; }

    public string? InstanceId { get; init; }

    /// <summary>True when this is the built-in fallback (no signed license present).</summary>
    public bool IsFallback { get; init; }

    public bool IsExpired(DateTimeOffset nowUtc) => ExpiresUtc is { } expiry && expiry < nowUtc;

    public bool IsUnlimited => ProbeLimit == Unlimited;

    public string ProbeLimitDisplay => IsUnlimited ? "unlimited" : ProbeLimit.ToString();

    public bool IsSensorUnlimited => SensorLimit == Unlimited;

    public string SensorLimitDisplay => IsSensorUnlimited ? "unlimited" : SensorLimit.ToString();

    /// <summary>The plan's display name, or the tier as a fallback for legacy tokens.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(PlanName) ? Tier.ToString() : PlanName;

    /// <summary>The default license when none is present: Free — a small allowance, no premium features.</summary>
    public static LicenseInfo Fallback() => new()
    {
        Tier = LicenseTier.Free,
        PlanName = null,
        ProbeLimit = 1,
        SensorLimit = 10,
        CloudSensorLimit = 0,
        TunnelEnabled = false,
        CloudSensorsEnabled = false,
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
    // New fields (n/sl/csl/te/cse) are nullable so a legacy token that lacks them verifies fine and maps to
    // permissive defaults (current behavior), rather than falsely reading 0/false and blocking things.
    private sealed record Payload(int t, int p, long e, long iss, string id,
        string? n = null, int? sl = null, int? csl = null, bool? te = null, bool? cse = null);

    public static string Sign(LicenseInfo license, string privateKeyPkcs8Base64)
    {
        var payload = new Payload(
            (int)license.Tier,
            license.ProbeLimit,
            license.ExpiresUtc?.ToUnixTimeSeconds() ?? 0,
            license.IssuedUtc.ToUnixTimeSeconds(),
            license.InstanceId ?? string.Empty,
            license.PlanName,
            license.SensorLimit,
            license.CloudSensorLimit,
            license.TunnelEnabled,
            license.CloudSensorsEnabled);

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
                InstanceId = string.IsNullOrEmpty(payload.id) ? null : payload.id,
                PlanName = string.IsNullOrEmpty(payload.n) ? null : payload.n,
                // Legacy tokens lack these → permissive defaults (grandfather the prior no-limit behavior).
                SensorLimit = payload.sl ?? LicenseInfo.Unlimited,
                CloudSensorLimit = payload.csl ?? 0,
                TunnelEnabled = payload.te ?? true,
                CloudSensorsEnabled = payload.cse ?? false
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
