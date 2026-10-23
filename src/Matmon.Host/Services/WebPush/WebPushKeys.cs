using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Matmon.Host.Services.WebPush;

/// <summary>
/// The key pair that proves to the push services who sends (VAPID, RFC 8292) - the MatMail pattern. Made once, on first use,
/// and kept beside the workspace (<c>push-keys.json</c>, the private half protected with the instance's DataProtection keys,
/// like every secret). The public half is what browsers are told when they subscribe, so a subscription belongs to this
/// pair: a new pair (the file lost, a different data folder) means the devices have to switch notifications on again. The
/// file is not in any backup for that reason - devices are not either.
/// </summary>
public sealed class WebPushKeys
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly IDataProtector _protector;
    private ECDsa? _key;
    private string? _publicKey;

    public WebPushKeys(IDataProtectionProvider protection, string path)
    {
        _path = path;
        _protector = protection.CreateProtector("Matmon.WebPush.Vapid");
    }

    /// <summary>The public key as browsers want it (<c>applicationServerKey</c>): 65 bytes, base64url.</summary>
    public string PublicKey
    {
        get
        {
            Ensure();
            return _publicKey!;
        }
    }

    /// <summary>The Authorization header for a push to this endpoint: a token signed with the private key, valid 12 hours.</summary>
    public string Authorization(string endpoint, string subject, DateTimeOffset now)
    {
        var key = Ensure();
        var audience = new Uri(endpoint).GetLeftPart(UriPartial.Authority);
        var header = WebPushEncryption.ToBase64Url("{\"typ\":\"JWT\",\"alg\":\"ES256\"}"u8);
        var claims = WebPushEncryption.ToBase64Url(JsonSerializer.SerializeToUtf8Bytes(new { aud = audience, exp = now.AddHours(12).ToUnixTimeSeconds(), sub = subject }));
        var signingInput = header + "." + claims;
        var signature = key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"vapid t={signingInput}.{WebPushEncryption.ToBase64Url(signature)}, k={_publicKey}";
    }

    /// <summary>Whether a signature was made with this pair (tests).</summary>
    internal bool Verify(string signingInput, byte[] signature) =>
        Ensure().VerifyData(Encoding.ASCII.GetBytes(signingInput), signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    private ECDsa Ensure()
    {
        lock (_gate)
        {
            if (_key is not null)
            {
                return _key;
            }

            _key = TryLoad() ?? Create();
            _publicKey = WebPushEncryption.ToBase64Url(PublicPoint(_key));
            return _key;
        }
    }

    private static byte[] PublicPoint(ECDsa key)
    {
        var parameters = key.ExportParameters(false);
        var point = new byte[65];
        point[0] = 0x04;
        parameters.Q.X!.CopyTo(point, 1 + 32 - parameters.Q.X.Length);
        parameters.Q.Y!.CopyTo(point, 33 + 32 - parameters.Q.Y.Length);
        return point;
    }

    private ECDsa? TryLoad()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            using var file = JsonDocument.Parse(File.ReadAllText(_path));
            var publicPoint = WebPushEncryption.FromBase64Url(file.RootElement.GetProperty("publicKey").GetString());
            var d = WebPushEncryption.FromBase64Url(_protector.Unprotect(file.RootElement.GetProperty("privateKey").GetString() ?? string.Empty));
            if (publicPoint is not { Length: 65 } || d is null)
            {
                return null; // damaged, or sealed with keys this installation no longer has: a new pair is made
            }

            return ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                D = d,
                Q = new ECPoint { X = publicPoint[1..33], Y = publicPoint[33..65] }
            });
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or CryptographicException or IOException)
        {
            return null;
        }
    }

    private ECDsa Create()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = key.ExportParameters(true);
        var json = JsonSerializer.Serialize(new
        {
            publicKey = WebPushEncryption.ToBase64Url(PublicPoint(key)),
            privateKey = _protector.Protect(WebPushEncryption.ToBase64Url(parameters.D!))
        }, new JsonSerializerOptions { WriteIndented = true });

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _path, overwrite: true);
        }
        catch (IOException)
        {
            // Not writable: the pair holds until the next restart (devices would have to subscribe again after it).
        }
        catch (UnauthorizedAccessException)
        {
        }

        return key;
    }
}
