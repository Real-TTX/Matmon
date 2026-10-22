// Signs the agent packages of one build: for every <root>/<rid>/matmon-agent[.exe] it writes the signature next to
// it (matmon-agent[.exe].sig) - over platform, version and SHA-256 (AgentPackageSignature). Run by the image build
// when the signing key is present (a BuildKit secret; CI: MATMON_AGENT_SIGNING_KEY):
//   dotnet run scripts/sign-agent-packages.cs -- <agent root> <version> <file with the private key>
// Every signature is checked against the PUBLIC key baked into the agent before the build goes on, so a CI secret
// that does not belong to that key fails the build instead of shipping agents that refuse every update.
#:project ../src/Matmon.Probe/Matmon.Probe.csproj
using System.Security.Cryptography;
using Matmon.Probe;

if (args.Length != 3)
{
    Console.Error.WriteLine("usage: dotnet run scripts/sign-agent-packages.cs -- <agent root> <version> <private key file>");
    return 2;
}

var (root, version, keyFile) = (args[0], args[1], args[2]);
if (string.IsNullOrWhiteSpace(version))
{
    Console.Error.WriteLine("A version is needed - the signature covers it.");
    return 2;
}

using var key = ECDsa.Create();
key.ImportPkcs8PrivateKey(Convert.FromBase64String(File.ReadAllText(keyFile).Trim()), out _);

var signed = 0;
foreach (var directory in Directory.GetDirectories(root))
{
    var runtimeId = Path.GetFileName(directory);
    foreach (var name in new[] { "matmon-agent.exe", "matmon-agent" })
    {
        var package = Path.Combine(directory, name);
        if (!File.Exists(package))
        {
            continue;
        }

        string sha256;
        using (var stream = File.OpenRead(package))
        {
            sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        var signature = AgentPackageSignature.Sign(key, runtimeId, version, sha256);
        if (!AgentPackageSignature.Verify(signature, runtimeId, version, sha256))
        {
            Console.Error.WriteLine("The signing key does not belong to the public key in AgentPackageSignature.PublicKeySpki - refusing to sign.");
            return 1;
        }

        File.WriteAllText(package + AgentPackageSignature.SignatureSuffix, signature);
        Console.WriteLine($"signed {runtimeId}/{name} ({version}, sha256 {sha256[..12]}...)");
        signed++;
    }
}

if (signed == 0)
{
    Console.Error.WriteLine($"No agent package found under {root}.");
    return 1;
}

return 0;
