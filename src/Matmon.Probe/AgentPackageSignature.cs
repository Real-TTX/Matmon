using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Matmon.Probe;

/// <summary>
/// Signed agent packages. The auto-update used to trust the binary because its SHA-256 matched the manifest - but the
/// manifest comes from the same instance over the same channel, so on plain http a man in the middle could swap both.
/// CI now signs every package it builds (ECDSA P-256, the licence pattern): the signature covers
/// <c>matmon-agent|platform|version|sha256</c>, so a genuine package cannot be relabelled as another platform or
/// version either. The private key exists only as the CI secret <c>MATMON_AGENT_SIGNING_KEY</c>; the public key is
/// baked in here.
/// </summary>
public static class AgentPackageSignature
{
    /// <summary>The public half of the CI signing key (SubjectPublicKeyInfo, base64). Rotate with
    /// <c>dotnet run scripts/new-agent-signing-key.cs</c> - and the CI secret together with it.</summary>
    public const string PublicKeySpki = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEdeIsXDMhuiJjr8Ev6+uQBcKmTncAvxdChCHv/L9DodNjsGrix/gdXZBeeIMWySvfc0Rf4xYwouhK/Utvc4p3GQ==";

    /// <summary>The file next to a package that carries its signature (base64).</summary>
    public const string SignatureSuffix = ".sig";

    public static string Payload(string runtimeId, string version, string sha256Hex) =>
        $"matmon-agent|{runtimeId.Trim().ToLowerInvariant()}|{version.Trim()}|{sha256Hex.Trim().ToLowerInvariant()}";

    public static string Sign(ECDsa privateKey, string runtimeId, string version, string sha256Hex)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        var signature = privateKey.SignData(Encoding.UTF8.GetBytes(Payload(runtimeId, version, sha256Hex)),
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return Convert.ToBase64String(signature);
    }

    /// <summary>Whether <paramref name="signature"/> is the CI key's signature over exactly this platform, version and
    /// hash. Anything unreadable is simply not valid.</summary>
    public static bool Verify(string? signature, string runtimeId, string version, string sha256Hex, string? publicKeySpki = null)
    {
        if (string.IsNullOrWhiteSpace(signature))
        {
            return false;
        }

        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeySpki ?? PublicKeySpki), out _);
            return key.VerifyData(Encoding.UTF8.GetBytes(Payload(runtimeId, version, sha256Hex)), Convert.FromBase64String(signature.Trim()),
                HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether this build refuses an unsigned update. Baked in at build time (<c>-p:MatmonAgentRequireSignature=true</c>,
    /// set by the image build exactly when it signs the packages): an agent that came signed never accepts an unsigned
    /// package again - otherwise stripping the signature from the manifest would be all an attacker had to do. A local
    /// build signs nothing and requires nothing.
    /// </summary>
    public static bool RequiredByThisBuild { get; } = ReadRequired();

    private static bool ReadRequired()
    {
        var assembly = Assembly.GetEntryAssembly();
        return assembly?.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Any(attribute => attribute.Key == "MatmonAgentRequireSignature" && string.Equals(attribute.Value, "true", StringComparison.OrdinalIgnoreCase)) == true;
    }
}
