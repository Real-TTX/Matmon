// Creates a new key pair for signing agent packages (ECDSA P-256).
//   dotnet run scripts/new-agent-signing-key.cs -- <file for the private key>
// The PRIVATE key (PKCS#8, base64) is written to the file - never printed - and belongs into the CI secret
// MATMON_AGENT_SIGNING_KEY (GitHub: Settings > Secrets and variables > Actions); keep a backup offline.
// The PUBLIC key is printed: put it into AgentPackageSignature.PublicKeySpki (src/Matmon.Probe).
// Rotating means: agents built with the old key refuse packages signed with the new one, so after a rotation
// those agents have to be updated by hand once (re-run the install command).
using System.Security.Cryptography;

if (args.Length != 1)
{
    Console.Error.WriteLine("usage: dotnet run scripts/new-agent-signing-key.cs -- <file for the private key>");
    return 2;
}

var path = Path.GetFullPath(args[0]);
if (File.Exists(path))
{
    Console.Error.WriteLine($"{path} exists - refusing to overwrite a signing key.");
    return 1;
}

Directory.CreateDirectory(Path.GetDirectoryName(path)!);
using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
File.WriteAllText(path, Convert.ToBase64String(key.ExportPkcs8PrivateKey()));
Console.WriteLine($"private key written to {path}");
Console.WriteLine($"public key (AgentPackageSignature.PublicKeySpki): {Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())}");
return 0;
