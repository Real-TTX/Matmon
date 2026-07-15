using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace Matmon.Host.Services;

/// <summary>
/// An <see cref="IDataProtector"/> whose key is derived from a user passphrase (PBKDF2-SHA256) rather than the
/// instance's DataProtection key ring, so secrets it seals are <b>portable</b>: a config backup sealed with a
/// passphrase on one instance can be restored - credentials and all - on a different instance that supplies the
/// same passphrase. Payload = 12-byte nonce ++ 16-byte GCM tag ++ ciphertext (AES-256-GCM, authenticated).
/// The salt travels with the backup so the key can be re-derived on restore.
/// </summary>
internal sealed class PassphraseSecretProtector : IDataProtector
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _key;

    public PassphraseSecretProtector(string passphrase, byte[] salt, int iterations)
    {
        ArgumentException.ThrowIfNullOrEmpty(passphrase);
        _key = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passphrase), salt, iterations, HashAlgorithmName.SHA256, outputLength: 32);
    }

    // Purpose scoping doesn't apply - this protector is already a single dedicated backup key.
    public IDataProtector CreateProtector(string purpose) => this;

    public byte[] Protect(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using var gcm = new AesGcm(_key, TagSize);
        gcm.Encrypt(nonce, plaintext, cipher, tag);

        var output = new byte[NonceSize + TagSize + cipher.Length];
        nonce.CopyTo(output, 0);
        tag.CopyTo(output, NonceSize);
        cipher.CopyTo(output, NonceSize + TagSize);
        return output;
    }

    public byte[] Unprotect(byte[] protectedData)
    {
        ArgumentNullException.ThrowIfNull(protectedData);
        if (protectedData.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("The protected payload is malformed.");
        }

        var nonce = protectedData.AsSpan(0, NonceSize);
        var tag = protectedData.AsSpan(NonceSize, TagSize);
        var cipher = protectedData.AsSpan(NonceSize + TagSize);
        var plaintext = new byte[cipher.Length];
        using var gcm = new AesGcm(_key, TagSize);
        gcm.Decrypt(nonce, cipher, tag, plaintext); // throws CryptographicException on a wrong passphrase (auth tag mismatch)
        return plaintext;
    }
}
