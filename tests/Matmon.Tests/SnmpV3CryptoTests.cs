using System.Text;
using Matmon.Core.Domain;

namespace Matmon.Tests;

public class SnmpV3CryptoTests
{
    private static readonly byte[] RfcEngineId = Convert.FromHexString("000000000000000000000002");

    [Theory]
    // RFC 3414 A.3.1 / A.3.2: password "maplesyrup", engine ID 00..02.
    [InlineData(SnmpV3AuthProtocol.Md5, "526f5eed9fcce26f8964c2930787d82b")]
    [InlineData(SnmpV3AuthProtocol.Sha1, "6695febc9288e36282235fc7151f128497b38f3f")]
    public void KeyLocalizationMatchesRfc3414(SnmpV3AuthProtocol protocol, string expected)
    {
        Assert.Equal(expected, Convert.ToHexString(SnmpV3Crypto.LocalizedKey(protocol, "maplesyrup", RfcEngineId)).ToLowerInvariant());
    }

    [Theory]
    // FIPS 180-4 examples.
    [InlineData("abc", "23097d223405d8228642a477bda255b32aadbce4bda0b3f7e36c9da7")]
    [InlineData("", "d14a028c2a3a2bc9476102bb288234c415a2b01f828ea62ac5b3e42f")]
    [InlineData("abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq", "75388b16512776cc5dba5da1fd890150b0c6455cb4f58b1952522525")]
    public void Sha224MatchesFips(string input, string expected)
    {
        Assert.Equal(expected, Convert.ToHexString(Sha224.HashData(Encoding.ASCII.GetBytes(input))).ToLowerInvariant());
    }

    [Fact]
    public void HmacSha224MatchesRfc4231()
    {
        // RFC 4231 test case 1, and test case 6 (a key longer than the block size).
        Assert.Equal("896fb1128abbdf196832107cd49df33f47b4b1169912ba4f53684b22",
            Convert.ToHexString(Sha224.Hmac(Enumerable.Repeat((byte)0x0b, 20).ToArray(), Encoding.ASCII.GetBytes("Hi There"))).ToLowerInvariant());
        Assert.Equal("95e9a0db962095adaebe9b2d6f0dbce2d499f112f2d2b7273fa6870e",
            Convert.ToHexString(Sha224.Hmac(Enumerable.Repeat((byte)0xaa, 131).ToArray(), Encoding.ASCII.GetBytes("Test Using Larger Than Block-Size Key - Hash Key First"))).ToLowerInvariant());
    }

    [Theory]
    [InlineData(SnmpV3AuthProtocol.Md5, 12)]
    [InlineData(SnmpV3AuthProtocol.Sha1, 12)]
    [InlineData(SnmpV3AuthProtocol.Sha224, 16)]
    [InlineData(SnmpV3AuthProtocol.Sha256, 24)]
    [InlineData(SnmpV3AuthProtocol.Sha384, 32)]
    [InlineData(SnmpV3AuthProtocol.Sha512, 48)]
    public void TheMacIsTruncatedPerRfc7860(SnmpV3AuthProtocol protocol, int length)
    {
        Assert.Equal(length, SnmpV3Crypto.Mac(protocol, [1, 2, 3], [4, 5, 6]).Length);
    }

    [Fact]
    public void BothKeyExtensionsKeepTheLocalizedKeyAsPrefixButDiffer()
    {
        var blumenthal = SnmpV3Crypto.PrivacyKey(SnmpV3AuthProtocol.Sha1, SnmpV3PrivProtocol.Aes256, "maplesyrup", RfcEngineId);
        var reeder = SnmpV3Crypto.PrivacyKey(SnmpV3AuthProtocol.Sha1, SnmpV3PrivProtocol.Aes256Reeder, "maplesyrup", RfcEngineId);
        var localized = SnmpV3Crypto.LocalizedKey(SnmpV3AuthProtocol.Sha1, "maplesyrup", RfcEngineId);

        Assert.Equal(32, blumenthal.Length);
        Assert.Equal(32, reeder.Length);
        Assert.Equal(localized, blumenthal[..20]);
        Assert.Equal(localized, reeder[..20]);
        Assert.NotEqual(blumenthal, reeder);
        // Blumenthal's second chunk is H(Kul).
        Assert.Equal(SnmpV3Crypto.Hash(SnmpV3AuthProtocol.Sha1, localized)[..12], blumenthal[20..]);
    }

    [Fact]
    public void ASha512KeyIsLongEnoughWithoutExtension()
    {
        var key = SnmpV3Crypto.PrivacyKey(SnmpV3AuthProtocol.Sha512, SnmpV3PrivProtocol.Aes256, "maplesyrup", RfcEngineId);
        Assert.Equal(SnmpV3Crypto.LocalizedKey(SnmpV3AuthProtocol.Sha512, "maplesyrup", RfcEngineId), key);
    }

    [Theory]
    [InlineData(SnmpV3PrivProtocol.Des)]
    [InlineData(SnmpV3PrivProtocol.TripleDes)]
    [InlineData(SnmpV3PrivProtocol.Aes128)]
    [InlineData(SnmpV3PrivProtocol.Aes192)]
    [InlineData(SnmpV3PrivProtocol.Aes256)]
    [InlineData(SnmpV3PrivProtocol.Aes192Reeder)]
    [InlineData(SnmpV3PrivProtocol.Aes256Reeder)]
    public void EveryCipherRoundTrips(SnmpV3PrivProtocol protocol)
    {
        var key = SnmpV3Crypto.PrivacyKey(SnmpV3AuthProtocol.Sha256, protocol, "privpass123", RfcEngineId);
        var salt = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var plaintext = Encoding.ASCII.GetBytes("a scoped PDU of 37 bytes, not aligned"); // stream ciphers keep the length

        var ciphertext = SnmpV3Crypto.Encrypt(protocol, key, 7, 12345, salt, plaintext);
        var decrypted = SnmpV3Crypto.Decrypt(protocol, key, 7, 12345, salt, ciphertext);

        Assert.NotEqual(plaintext, ciphertext[..plaintext.Length]);
        Assert.Equal(plaintext, decrypted[..plaintext.Length]);
    }

    [Theory]
    [InlineData("SHA-256", SnmpV3AuthProtocol.Sha256)]
    [InlineData("sha", SnmpV3AuthProtocol.Sha1)]
    [InlineData("", SnmpV3AuthProtocol.Sha1)]
    [InlineData("sha512", SnmpV3AuthProtocol.Sha512)]
    public void AuthNamesParseLeniently(string value, SnmpV3AuthProtocol expected) =>
        Assert.Equal(expected, SnmpV3Crypto.ParseAuth(value));

    [Theory]
    [InlineData("AES", SnmpV3PrivProtocol.Aes128)]
    [InlineData("AES-256", SnmpV3PrivProtocol.Aes256)]
    [InlineData("aes256c", SnmpV3PrivProtocol.Aes256Reeder)]
    [InlineData("3DES", SnmpV3PrivProtocol.TripleDes)]
    public void PrivNamesParseLeniently(string value, SnmpV3PrivProtocol expected) =>
        Assert.Equal(expected, SnmpV3Crypto.ParsePriv(value));

    [Fact]
    public void UnknownNamesAreRejectedNotGuessed()
    {
        Assert.Null(SnmpV3Crypto.ParseAuth("sha3"));
        Assert.Null(SnmpV3Crypto.ParsePriv("chacha"));
    }
}
