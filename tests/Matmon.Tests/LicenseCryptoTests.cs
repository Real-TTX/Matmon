using System.Security.Cryptography;
using Matmon.Core.Domain;
using Xunit;

namespace Matmon.Tests;

public class LicenseCryptoTests
{
    // The dev private key that pairs with LicensePublicKey.Spki (baked into Matmon; the cloud's default).
    private const string DevPrivateKeyPkcs8 =
        "MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQgWQHWrti/AJbc7Mr7JRgEltUfj+z0I9j3rc1b/986LrGhRANCAATLlwKVXkOwKYDFPO/V4wxLUSG5KK/uV4G4lLnPG6XmsGzVcnuTH27guBpNraHa4uzS5n7PgqGobqbXiimDRp+R";

    [Fact]
    public void Sign_then_verify_round_trips_the_license()
    {
        var license = new LicenseInfo
        {
            Tier = LicenseTier.Business,
            ProbeLimit = 5,
            ExpiresUtc = DateTimeOffset.FromUnixTimeSeconds(1_900_000_000),
            IssuedUtc = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000),
            InstanceId = "11111111-2222-3333-4444-555555555555"
        };

        var token = LicenseCrypto.Sign(license, DevPrivateKeyPkcs8);
        var verified = LicenseCrypto.Verify(token, LicensePublicKey.Spki);

        Assert.NotNull(verified);
        Assert.Equal(LicenseTier.Business, verified!.Tier);
        Assert.Equal(5, verified.ProbeLimit);
        Assert.Equal(license.ExpiresUtc, verified.ExpiresUtc);
        Assert.Equal(license.InstanceId, verified.InstanceId);
    }

    [Fact]
    public void Baked_public_key_matches_the_cloud_dev_private_key()
    {
        // Guards against a copy-paste mismatch between the cloud signer key and Matmon's baked key.
        var token = LicenseCrypto.Sign(new LicenseInfo { Tier = LicenseTier.Enterprise, ProbeLimit = -1 }, DevPrivateKeyPkcs8);
        Assert.NotNull(LicenseCrypto.Verify(token, LicensePublicKey.Spki));
    }

    [Fact]
    public void Tampered_token_fails_verification()
    {
        var token = LicenseCrypto.Sign(new LicenseInfo { Tier = LicenseTier.Business, ProbeLimit = 3 }, DevPrivateKeyPkcs8);
        var parts = token.Split('.');
        // Flip the payload (claim Enterprise/unlimited) but keep the old signature.
        var forgedPayload = LicenseCrypto.Sign(new LicenseInfo { Tier = LicenseTier.Enterprise, ProbeLimit = -1 }, DevPrivateKeyPkcs8).Split('.')[0];
        var forged = $"{forgedPayload}.{parts[1]}";

        Assert.Null(LicenseCrypto.Verify(forged, LicensePublicKey.Spki));
    }

    [Fact]
    public void A_token_signed_by_a_different_key_is_rejected()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var otherPrivate = Convert.ToBase64String(other.ExportPkcs8PrivateKey());
        var token = LicenseCrypto.Sign(new LicenseInfo { Tier = LicenseTier.Enterprise, ProbeLimit = -1 }, otherPrivate);

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
