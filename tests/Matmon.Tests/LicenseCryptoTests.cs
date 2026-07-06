using System.Security.Cryptography;
using Matmon.Core.Domain;
using Xunit;

namespace Matmon.Tests;

public class LicenseCryptoTests
{
    // The production private key is NOT in this repo (it lives only in the cloud's License__PrivateKey env),
    // so the crypto tests generate their own ephemeral P-256 keypair instead of a hardcoded one.
    private static (string PrivatePkcs8, string PublicSpki) FreshKeypair()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey()),
                Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo()));
    }

    [Fact]
    public void Sign_then_verify_round_trips_the_license()
    {
        var (priv, pub) = FreshKeypair();
        var license = new LicenseInfo
        {
            Tier = LicenseTier.Business,
            ProbeLimit = 5,
            ExpiresUtc = DateTimeOffset.FromUnixTimeSeconds(1_900_000_000),
            IssuedUtc = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000),
            InstanceId = "11111111-2222-3333-4444-555555555555"
        };

        var token = LicenseCrypto.Sign(license, priv);
        var verified = LicenseCrypto.Verify(token, pub);

        Assert.NotNull(verified);
        Assert.Equal(LicenseTier.Business, verified!.Tier);
        Assert.Equal(5, verified.ProbeLimit);
        Assert.Equal(license.ExpiresUtc, verified.ExpiresUtc);
        Assert.Equal(license.InstanceId, verified.InstanceId);
    }

    [Fact]
    public void Baked_public_key_is_a_valid_p256_spki()
    {
        // Guards against a malformed / truncated baked key (e.g. a bad copy-paste on rotation).
        using var ecdsa = ECDsa.Create();
        var spki = Convert.FromBase64String(LicensePublicKey.Spki);
        ecdsa.ImportSubjectPublicKeyInfo(spki, out var bytesRead);

        Assert.Equal(spki.Length, bytesRead);
        Assert.Equal(256, ecdsa.KeySize);
    }

    [Fact]
    public void Tampered_token_fails_verification()
    {
        var (priv, pub) = FreshKeypair();
        var token = LicenseCrypto.Sign(new LicenseInfo { Tier = LicenseTier.Business, ProbeLimit = 3 }, priv);
        var parts = token.Split('.');
        // Flip the payload (claim Enterprise/unlimited) but keep the old signature.
        var forgedPayload = LicenseCrypto.Sign(new LicenseInfo { Tier = LicenseTier.Enterprise, ProbeLimit = -1 }, priv).Split('.')[0];
        var forged = $"{forgedPayload}.{parts[1]}";

        Assert.Null(LicenseCrypto.Verify(forged, pub));
    }

    [Fact]
    public void A_token_signed_by_a_different_key_is_rejected_by_the_baked_key()
    {
        // A token signed by anything other than the production private key must not verify against the baked key.
        var (priv, _) = FreshKeypair();
        var token = LicenseCrypto.Sign(new LicenseInfo { Tier = LicenseTier.Enterprise, ProbeLimit = -1 }, priv);

        Assert.Null(LicenseCrypto.Verify(token, LicensePublicKey.Spki));
    }

    [Fact]
    public void Expired_license_is_flagged()
    {
        var license = new LicenseInfo { Tier = LicenseTier.Business, ProbeLimit = 3, ExpiresUtc = DateTimeOffset.UnixEpoch };
        Assert.True(license.IsExpired(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Garbage_and_null_tokens_return_null()
    {
        Assert.Null(LicenseCrypto.Verify(null, LicensePublicKey.Spki));
        Assert.Null(LicenseCrypto.Verify("not-a-token", LicensePublicKey.Spki));
        Assert.Null(LicenseCrypto.Verify("aaa.bbb", LicensePublicKey.Spki));
    }
}
