using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Matmon.Core.Domain;

public enum SnmpV3AuthProtocol
{
    None = 0,
    Md5 = 1,
    Sha1 = 2,
    Sha224 = 3,
    Sha256 = 4,
    Sha384 = 5,
    Sha512 = 6
}

public enum SnmpV3PrivProtocol
{
    None = 0,
    Des = 1,
    Aes128 = 2,
    /// <summary>AES-192 with the Blumenthal key extension (draft-blumenthal-aes-usm) - what Net-SNMP calls AES-192.</summary>
    Aes192 = 3,
    /// <summary>AES-256 with the Blumenthal key extension - Net-SNMP's AES-256.</summary>
    Aes256 = 4,
    /// <summary>AES-192 with the Reeder key extension - what Cisco (and devices that copied it) means by AES-192.</summary>
    Aes192Reeder = 5,
    /// <summary>AES-256 with the Reeder key extension - Cisco's AES-256.</summary>
    Aes256Reeder = 6,
    /// <summary>3DES-EDE (draft-reeder-snmpv3-usm-3desede), Reeder key extension.</summary>
    TripleDes = 7
}

/// <summary>
/// The SNMPv3 User-based Security Model's cryptography, pure and tested: password-to-key, key localization, the
/// HMAC with its per-algorithm truncation, the privacy-key extension and the ciphers. Kept apart from the 2000-line
/// executor because it is exactly the part that has to match other implementations bit for bit.
///
/// Two traps worth naming. (1) The HMAC is TRUNCATED, and by a different amount per algorithm: 12 bytes for
/// MD5/SHA-1 (RFC 3414), 16/24/32/48 for SHA-224/256/384/512 (RFC 7860) - and msgAuthenticationParameters
/// must be a placeholder of exactly that length when the MAC is computed. (2) AES-192/256 and 3DES need more
/// key than one localized MD5/SHA-1 key holds, and the world never agreed how to stretch it: Net-SNMP uses
/// Blumenthal (append H(key so far)), Cisco uses Reeder (append localize(passwordToKey(previous chunk))). A
/// device configured with one cannot talk to a manager using the other, so both are offered.
/// </summary>
public static class SnmpV3Crypto
{
    /// <summary>The choices the sensor and credential editors offer, value = the stored parameter.</summary>
    public static readonly IReadOnlyList<(string Value, string Label)> AuthOptions =
    [
        ("none", "None"),
        ("md5", "MD5"),
        ("sha1", "SHA-1"),
        ("sha224", "SHA-224"),
        ("sha256", "SHA-256"),
        ("sha384", "SHA-384"),
        ("sha512", "SHA-512")
    ];

    public static readonly IReadOnlyList<(string Value, string Label)> PrivOptions =
    [
        ("none", "None"),
        ("des", "DES"),
        ("3des", "3DES"),
        ("aes128", "AES-128"),
        ("aes192", "AES-192 (Net-SNMP / Blumenthal)"),
        ("aes256", "AES-256 (Net-SNMP / Blumenthal)"),
        ("aes192c", "AES-192 (Cisco / Reeder)"),
        ("aes256c", "AES-256 (Cisco / Reeder)")
    ];

    public static SnmpV3AuthProtocol? ParseAuth(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant().Replace("-", string.Empty) switch
    {
        "" => SnmpV3AuthProtocol.Sha1,
        "none" => SnmpV3AuthProtocol.None,
        "md5" => SnmpV3AuthProtocol.Md5,
        "sha" or "sha1" => SnmpV3AuthProtocol.Sha1,
        "sha224" => SnmpV3AuthProtocol.Sha224,
        "sha256" => SnmpV3AuthProtocol.Sha256,
        "sha384" => SnmpV3AuthProtocol.Sha384,
        "sha512" => SnmpV3AuthProtocol.Sha512,
        _ => null
    };

    public static SnmpV3PrivProtocol? ParsePriv(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant().Replace("-", string.Empty) switch
    {
        "" or "none" => SnmpV3PrivProtocol.None,
        "des" => SnmpV3PrivProtocol.Des,
        "3des" or "3desede" or "tripledes" => SnmpV3PrivProtocol.TripleDes,
        "aes" or "aes128" => SnmpV3PrivProtocol.Aes128,
        "aes192" => SnmpV3PrivProtocol.Aes192,
        "aes256" => SnmpV3PrivProtocol.Aes256,
        "aes192c" => SnmpV3PrivProtocol.Aes192Reeder,
        "aes256c" => SnmpV3PrivProtocol.Aes256Reeder,
        _ => null
    };

    /// <summary>Length of msgAuthenticationParameters, i.e. how far the HMAC is truncated.</summary>
    public static int MacLength(SnmpV3AuthProtocol protocol) => protocol switch
    {
        SnmpV3AuthProtocol.Md5 or SnmpV3AuthProtocol.Sha1 => 12,
        SnmpV3AuthProtocol.Sha224 => 16,
        SnmpV3AuthProtocol.Sha256 => 24,
        SnmpV3AuthProtocol.Sha384 => 32,
        SnmpV3AuthProtocol.Sha512 => 48,
        _ => 0
    };

    public static byte[] Hash(SnmpV3AuthProtocol protocol, ReadOnlySpan<byte> data) => protocol switch
    {
        SnmpV3AuthProtocol.Md5 => MD5.HashData(data),
        SnmpV3AuthProtocol.Sha1 => SHA1.HashData(data),
        SnmpV3AuthProtocol.Sha224 => Sha224.HashData(data),
        SnmpV3AuthProtocol.Sha256 => SHA256.HashData(data),
        SnmpV3AuthProtocol.Sha384 => SHA384.HashData(data),
        SnmpV3AuthProtocol.Sha512 => SHA512.HashData(data),
        _ => throw new InvalidOperationException("SNMP v3 hashing requires an authentication algorithm.")
    };

    /// <summary>The full (untruncated) HMAC over <paramref name="message"/>.</summary>
    public static byte[] Hmac(SnmpV3AuthProtocol protocol, byte[] key, byte[] message) => protocol switch
    {
        SnmpV3AuthProtocol.Md5 => HMACMD5.HashData(key, message),
        SnmpV3AuthProtocol.Sha1 => HMACSHA1.HashData(key, message),
        SnmpV3AuthProtocol.Sha224 => Sha224.Hmac(key, message),
        SnmpV3AuthProtocol.Sha256 => HMACSHA256.HashData(key, message),
        SnmpV3AuthProtocol.Sha384 => HMACSHA384.HashData(key, message),
        SnmpV3AuthProtocol.Sha512 => HMACSHA512.HashData(key, message),
        _ => throw new InvalidOperationException("Unsupported SNMP v3 authentication protocol.")
    };

    /// <summary>The truncated MAC that goes into msgAuthenticationParameters.</summary>
    public static byte[] Mac(SnmpV3AuthProtocol protocol, byte[] key, byte[] message) =>
        Hmac(protocol, key, message)[..MacLength(protocol)];

    /// <summary>RFC 3414 A.2 password-to-key: hash 1 MiB of the password repeated (RFC 7860 for SHA-2).</summary>
    public static byte[] PasswordToKey(SnmpV3AuthProtocol protocol, ReadOnlySpan<byte> password)
    {
        if (password.IsEmpty)
        {
            return [];
        }

        var buffer = new byte[1_048_576];
        for (var offset = 0; offset < buffer.Length; offset += password.Length)
        {
            password[..Math.Min(password.Length, buffer.Length - offset)].CopyTo(buffer.AsSpan(offset));
        }

        return Hash(protocol, buffer);
    }

    /// <summary>Kul = H(Ku || engineID || Ku).</summary>
    public static byte[] Localize(SnmpV3AuthProtocol protocol, byte[] ku, byte[] engineId) =>
        Hash(protocol, [.. ku, .. engineId, .. ku]);

    public static byte[] LocalizedKey(SnmpV3AuthProtocol protocol, string password, byte[] engineId) =>
        Localize(protocol, PasswordToKey(protocol, Encoding.UTF8.GetBytes(password)), engineId);

    /// <summary>How much key material a cipher consumes (3DES: 24 key + 8 pre-IV).</summary>
    public static int PrivKeyLength(SnmpV3PrivProtocol protocol) => protocol switch
    {
        SnmpV3PrivProtocol.Des or SnmpV3PrivProtocol.Aes128 => 16,
        SnmpV3PrivProtocol.Aes192 or SnmpV3PrivProtocol.Aes192Reeder => 24,
        SnmpV3PrivProtocol.Aes256 or SnmpV3PrivProtocol.Aes256Reeder or SnmpV3PrivProtocol.TripleDes => 32,
        _ => 0
    };

    /// <summary>The localized privacy key, stretched to what the cipher needs (see the class remarks).</summary>
    public static byte[] PrivacyKey(SnmpV3AuthProtocol auth, SnmpV3PrivProtocol priv, string password, byte[] engineId)
    {
        var key = LocalizedKey(auth, password, engineId);
        var needed = PrivKeyLength(priv);
        if (key.Length >= needed)
        {
            return key;
        }

        return priv is SnmpV3PrivProtocol.Aes192 or SnmpV3PrivProtocol.Aes256
            ? ExtendBlumenthal(auth, key, needed)
            : ExtendReeder(auth, key, engineId, needed);
    }

    /// <summary>Kul' = Kul || H(Kul) || H(Kul || H(Kul)) ... - each step hashes everything so far (Net-SNMP).</summary>
    public static byte[] ExtendBlumenthal(SnmpV3AuthProtocol auth, byte[] key, int needed)
    {
        var result = key.ToList();
        while (result.Count < needed)
        {
            result.AddRange(Hash(auth, result.ToArray()));
        }

        return [.. result.Take(needed)];
    }

    /// <summary>Kul' = Kul || localize(passwordToKey(Kul)) || ... - each new chunk treats the previous chunk as a
    /// password (draft-reeder-snmpv3-usm-3desede, Cisco).</summary>
    public static byte[] ExtendReeder(SnmpV3AuthProtocol auth, byte[] key, byte[] engineId, int needed)
    {
        var result = key.ToList();
        var chunk = key;
        while (result.Count < needed)
        {
            chunk = Localize(auth, PasswordToKey(auth, chunk), engineId);
            result.AddRange(chunk);
        }

        return [.. result.Take(needed)];
    }

    public static byte[] Encrypt(SnmpV3PrivProtocol protocol, byte[] privKey, int engineBoots, int engineTime, byte[] salt, byte[] plaintext) =>
        Transform(protocol, privKey, engineBoots, engineTime, salt, plaintext, encrypt: true);

    public static byte[] Decrypt(SnmpV3PrivProtocol protocol, byte[] privKey, int engineBoots, int engineTime, byte[] salt, byte[] ciphertext) =>
        Transform(protocol, privKey, engineBoots, engineTime, salt, ciphertext, encrypt: false);

    private static byte[] Transform(SnmpV3PrivProtocol protocol, byte[] privKey, int engineBoots, int engineTime, byte[] salt, byte[] data, bool encrypt)
    {
        if (protocol == SnmpV3PrivProtocol.None)
        {
            return data;
        }
        if (salt.Length != 8)
        {
            throw new InvalidOperationException("SNMP v3 privacy parameters must be 8 bytes.");
        }
        var needed = PrivKeyLength(protocol);
        if (privKey.Length < needed)
        {
            throw new InvalidOperationException("SNMP v3 privacy key is too short.");
        }

        if (protocol is SnmpV3PrivProtocol.Des or SnmpV3PrivProtocol.TripleDes)
        {
            // CBC with IV = preIV XOR salt (RFC 3414 8.1.1.1); the pre-IV is the 8 bytes after the key.
            var keyLength = protocol == SnmpV3PrivProtocol.Des ? 8 : 24;
            var key = AdjustDesParity(privKey[..keyLength]);
            var iv = Xor(privKey[keyLength..(keyLength + 8)], salt);
            using SymmetricAlgorithm cipher = protocol == SnmpV3PrivProtocol.Des ? DES.Create() : TripleDES.Create();
            cipher.Mode = CipherMode.CBC;
            cipher.Padding = PaddingMode.None;
            cipher.Key = key;
            cipher.IV = iv;
            if (encrypt)
            {
                var padded = new byte[(data.Length + 7) / 8 * 8];
                data.CopyTo(padded, 0);
                using var encryptor = cipher.CreateEncryptor();
                return encryptor.TransformFinalBlock(padded, 0, padded.Length);
            }
            using var decryptor = cipher.CreateDecryptor();
            return decryptor.TransformFinalBlock(data, 0, data.Length - data.Length % 8);
        }

        // AES (RFC 3826, and the same IV for 192/256): CFB-128 used as a stream cipher, IV = boots || time || salt.
        var aesIv = new byte[16];
        BinaryPrimitives.WriteInt32BigEndian(aesIv.AsSpan(0, 4), engineBoots);
        BinaryPrimitives.WriteInt32BigEndian(aesIv.AsSpan(4, 4), engineTime);
        salt.CopyTo(aesIv, 8);
        using var aes = Aes.Create();
        aes.KeySize = needed * 8;
        aes.Mode = CipherMode.CFB;
        aes.FeedbackSize = 128;
        aes.Padding = PaddingMode.None;
        aes.Key = privKey[..needed];
        aes.IV = aesIv;
        using var transform = encrypt ? aes.CreateEncryptor() : aes.CreateDecryptor();
        // .NET's CFB without padding wants whole blocks: pad, transform, cut back to the stream length.
        var blocks = new byte[(data.Length + 15) / 16 * 16];
        data.CopyTo(blocks, 0);
        return transform.TransformFinalBlock(blocks, 0, blocks.Length)[..data.Length];
    }

    private static byte[] AdjustDesParity(byte[] key)
    {
        var adjusted = new byte[key.Length];
        for (var index = 0; index < key.Length; index++)
        {
            var value = key[index];
            var ones = 0;
            for (var bit = 1; bit < 8; bit++)
            {
                ones ^= (value >> bit) & 1;
            }
            adjusted[index] = (byte)((value & 0xFE) | (ones == 0 ? 1 : 0));
        }

        return adjusted;
    }

    private static byte[] Xor(byte[] left, byte[] right)
    {
        var result = new byte[left.Length];
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = (byte)(left[index] ^ right[index]);
        }

        return result;
    }
}

/// <summary>
/// SHA-224 (FIPS 180-4) - .NET ships SHA-256/384/512 but not 224, and RFC 7860 lists HMAC-SHA-224 as an SNMPv3
/// auth protocol (usmHMAC128SHA224AuthProtocol). It is SHA-256 with different initial values, truncated to 28
/// bytes.
/// </summary>
public static class Sha224
{
    private static readonly uint[] K =
    [
        0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
        0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
        0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
        0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
        0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
        0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
        0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
        0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2
    ];

    public static byte[] HashData(ReadOnlySpan<byte> data)
    {
        uint[] h = [0xc1059ed8, 0x367cd507, 0x3070dd17, 0xf70e5939, 0xffc00b31, 0x68581511, 0x64f98fa7, 0xbefa4fa4];
        var bitLength = (ulong)data.Length * 8;
        var paddedLength = (data.Length + 9 + 63) / 64 * 64;
        var message = new byte[paddedLength];
        data.CopyTo(message);
        message[data.Length] = 0x80;
        BinaryPrimitives.WriteUInt64BigEndian(message.AsSpan(paddedLength - 8), bitLength);

        Span<uint> w = stackalloc uint[64];
        for (var block = 0; block < paddedLength; block += 64)
        {
            for (var t = 0; t < 16; t++)
            {
                w[t] = BinaryPrimitives.ReadUInt32BigEndian(message.AsSpan(block + t * 4, 4));
            }
            for (var t = 16; t < 64; t++)
            {
                var s0 = uint.RotateRight(w[t - 15], 7) ^ uint.RotateRight(w[t - 15], 18) ^ (w[t - 15] >> 3);
                var s1 = uint.RotateRight(w[t - 2], 17) ^ uint.RotateRight(w[t - 2], 19) ^ (w[t - 2] >> 10);
                w[t] = w[t - 16] + s0 + w[t - 7] + s1;
            }

            uint a = h[0], b = h[1], c = h[2], d = h[3], e = h[4], f = h[5], g = h[6], hh = h[7];
            for (var t = 0; t < 64; t++)
            {
                var sum1 = uint.RotateRight(e, 6) ^ uint.RotateRight(e, 11) ^ uint.RotateRight(e, 25);
                var choice = (e & f) ^ (~e & g);
                var temp1 = hh + sum1 + choice + K[t] + w[t];
                var sum0 = uint.RotateRight(a, 2) ^ uint.RotateRight(a, 13) ^ uint.RotateRight(a, 22);
                var majority = (a & b) ^ (a & c) ^ (b & c);
                var temp2 = sum0 + majority;
                hh = g; g = f; f = e; e = d + temp1; d = c; c = b; b = a; a = temp1 + temp2;
            }

            h[0] += a; h[1] += b; h[2] += c; h[3] += d; h[4] += e; h[5] += f; h[6] += g; h[7] += hh;
        }

        var digest = new byte[28];
        for (var index = 0; index < 7; index++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(digest.AsSpan(index * 4), h[index]);
        }

        return digest;
    }

    /// <summary>HMAC-SHA-224 (RFC 2104, block size 64).</summary>
    public static byte[] Hmac(byte[] key, byte[] message)
    {
        const int blockSize = 64;
        var k = key.Length > blockSize ? HashData(key) : key;
        var inner = new byte[blockSize + message.Length];
        var outer = new byte[blockSize + 28];
        for (var index = 0; index < blockSize; index++)
        {
            var value = index < k.Length ? k[index] : (byte)0;
            inner[index] = (byte)(value ^ 0x36);
            outer[index] = (byte)(value ^ 0x5c);
        }
        message.CopyTo(inner, blockSize);
        HashData(inner).CopyTo(outer, blockSize);
        return HashData(outer);
    }
}
