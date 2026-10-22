using System.Security.Cryptography;
using Matmon.Agent;
using Matmon.Probe;

namespace Matmon.Tests;

/// <summary>
/// Signed agent packages: the signature covers platform, version and hash, so neither a swapped file nor a genuine
/// package relabelled as another platform or version passes; a signed agent refuses an unsigned update, and a
/// present-but-wrong signature is refused by every agent.
/// </summary>
public sealed class AgentPackageSignatureTests
{
    private const string Hash = "3f0b1a6c9d2e4f5a6b7c8d9e0f1a2b3c4d5e6f708192a3b4c5d6e7f8091a2b3c";

    [Fact]
    public void A_signature_verifies_for_exactly_its_platform_version_and_hash()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var signature = AgentPackageSignature.Sign(key, "win-x64", "0.8.0.450-20261023-0100", Hash);

        Assert.True(AgentPackageSignature.Verify(signature, "WIN-X64", "0.8.0.450-20261023-0100", Hash.ToUpperInvariant(), publicKey));
        Assert.False(AgentPackageSignature.Verify(signature, "linux-x64", "0.8.0.450-20261023-0100", Hash, publicKey));
        Assert.False(AgentPackageSignature.Verify(signature, "win-x64", "0.8.0.449-20261022-2100", Hash, publicKey));
        Assert.False(AgentPackageSignature.Verify(signature, "win-x64", "0.8.0.450-20261023-0100", Hash.Replace('3', '4'), publicKey));
    }

    [Fact]
    public void Another_key_or_garbage_never_verifies()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signature = AgentPackageSignature.Sign(key, "linux-arm64", "1.0", Hash);

        Assert.False(AgentPackageSignature.Verify(signature, "linux-arm64", "1.0", Hash, Convert.ToBase64String(other.ExportSubjectPublicKeyInfo())));
        Assert.False(AgentPackageSignature.Verify("not base64!", "linux-arm64", "1.0", Hash));
        Assert.False(AgentPackageSignature.Verify(null, "linux-arm64", "1.0", Hash));
        // Nothing but the CI key verifies against the baked public key.
        Assert.False(AgentPackageSignature.Verify(signature, "linux-arm64", "1.0", Hash));
    }

    [Fact]
    public void The_baked_public_key_is_a_P256_key()
    {
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(AgentPackageSignature.PublicKeySpki), out _);
        Assert.Equal(256, key.KeySize);
    }

    [Fact]
    public void An_unsigned_update_is_refused_only_by_a_signed_agent_and_a_bad_signature_by_every_agent()
    {
        Assert.Null(AgentUpdatePolicy.CheckSignature(required: false, signature: null, _ => false));
        Assert.Contains("not signed", AgentUpdatePolicy.CheckSignature(required: true, signature: null, _ => false));
        Assert.Contains("not valid", AgentUpdatePolicy.CheckSignature(required: false, signature: "x", _ => false));
        Assert.Null(AgentUpdatePolicy.CheckSignature(required: true, signature: "x", _ => true));
    }

    [Fact]
    public void A_test_run_is_not_a_signed_agent()
    {
        Assert.False(AgentPackageSignature.RequiredByThisBuild);
    }
}
