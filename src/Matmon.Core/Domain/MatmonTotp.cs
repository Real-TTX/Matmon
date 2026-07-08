using System.Security.Cryptography;
using System.Text;

namespace Matmon.Core.Domain;

/// <summary>
/// RFC 6238 TOTP helpers for authenticator apps: secret generation, the otpauth:// provisioning URI, and code
/// verification. Dependency-free (framework crypto only). The QR rendering and the secret's encryption at rest
/// live in the host, which has QRCoder + DataProtection.
/// </summary>
public static class MatmonTotp
{
    private const int Period = 30;   // seconds per time-step
    private const int Digits = 6;

    /// <summary>A fresh 160-bit Base32 secret.</summary>
    public static string GenerateSecret() => Base32Encode(RandomNumberGenerator.GetBytes(20));

    /// <summary>The otpauth:// provisioning URI (QR + manual entry).</summary>
    public static string BuildOtpauthUri(string issuer, string accountName, string secretBase32)
    {
        var label = Uri.EscapeDataString($"{issuer}:{accountName}");
        var iss = Uri.EscapeDataString(issuer);
        return $"otpauth://totp/{label}?secret={secretBase32}&issuer={iss}&digits={Digits}&period={Period}&algorithm=SHA1";
    }

    /// <summary>Verify a 6-digit code against the Base32 secret, allowing a +/-1 step window for clock skew.</summary>
    public static bool Verify(string? secretBase32, string? code)
    {
        code = (code ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(secretBase32) || code.Length != Digits || !code.All(char.IsDigit))
        {
            return false;
        }
        byte[] key;
        try { key = Base32Decode(secretBase32); }
        catch { return false; }
        var step = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / Period;
        for (var window = -1; window <= 1; window++)
        {
            if (CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(Compute(key, step + window)),
                    Encoding.ASCII.GetBytes(code)))
            {
                return true;
            }
        }
        return false;
    }

    private static string Compute(byte[] key, long counter)
    {
        var bytes = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian) { Array.Reverse(bytes); }
        using var hmac = new HMACSHA1(key);
        var hash = hmac.ComputeHash(bytes);
        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24)
                   | ((hash[offset + 1] & 0xff) << 16)
                   | ((hash[offset + 2] & 0xff) << 8)
                   | (hash[offset + 3] & 0xff);
        return (binary % 1_000_000).ToString("D6");
    }

    // --- Base32 (RFC 4648, no padding) ---
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    private static string Base32Encode(byte[] data)
    {
        var sb = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                sb.Append(Alphabet[(buffer >> bits) & 31]);
            }
        }
        if (bits > 0) { sb.Append(Alphabet[(buffer << (5 - bits)) & 31]); }
        return sb.ToString();
    }

    private static byte[] Base32Decode(string value)
    {
        value = value.Trim().TrimEnd('=').ToUpperInvariant().Replace(" ", string.Empty);
        var bytes = new List<byte>(value.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in value)
        {
            var index = Alphabet.IndexOf(c);
            if (index < 0) { throw new FormatException("Invalid Base32 character."); }
            buffer = (buffer << 5) | index;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                bytes.Add((byte)((buffer >> bits) & 0xff));
            }
        }
        return bytes.ToArray();
    }
}
