// Packs the published agent packages of one build into the agent feed an instance fetches them from on demand
// (AgentPackageStore / AgentFeed in Matmon.Host):
//   matmon-agent-<rid>[.exe]   one file per platform (release assets need unique names)
//   matmon-agent.json          the manifest: version and, per platform, file, size, sha256 and - when the build signed the
//                              packages (scripts/sign-agent-packages.cs wrote <package>.sig) - the CI signature
// Run by the image build's agent-package stage:
//   dotnet run scripts/package-agents.cs -- <agent root with <rid>/matmon-agent[.exe]> <version> <output folder>
// A signature that does not verify against the key baked into the agent fails the build.
#:project ../src/Matmon.Probe/Matmon.Probe.csproj
using System.Security.Cryptography;
using System.Text.Json;
using Matmon.Probe;

if (args.Length != 3)
{
    Console.Error.WriteLine("usage: dotnet run scripts/package-agents.cs -- <agent root> <version> <output folder>");
    return 2;
}

var (root, version, output) = (args[0], args[1], args[2]);
if (string.IsNullOrWhiteSpace(version))
{
    Console.Error.WriteLine("A version is needed.");
    return 2;
}

// The same order as AgentPackageStore.Platforms.
string[] platforms = ["win-x64", "linux-x64", "linux-arm64", "osx-arm64", "osx-x64"];
Directory.CreateDirectory(output);

var entries = new List<(string RuntimeId, string File, long Size, string Sha256, string? Signature)>();
foreach (var runtimeId in platforms)
{
    var windows = runtimeId.StartsWith("win-", StringComparison.Ordinal);
    var source = Path.Combine(root, runtimeId, windows ? "matmon-agent.exe" : "matmon-agent");
    if (!File.Exists(source))
    {
        Console.Error.WriteLine($"missing: {source}");
        return 1;
    }

    var asset = windows ? $"matmon-agent-{runtimeId}.exe" : $"matmon-agent-{runtimeId}";
    var target = Path.Combine(output, asset);
    File.Copy(source, target, overwrite: true);

    string sha256;
    using (var stream = File.OpenRead(target))
    {
        sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    var signatureFile = source + AgentPackageSignature.SignatureSuffix;
    var signature = File.Exists(signatureFile) ? File.ReadAllText(signatureFile).Trim() : null;
    if (!string.IsNullOrEmpty(signature) && !AgentPackageSignature.Verify(signature, runtimeId, version, sha256))
    {
        Console.Error.WriteLine($"The signature of {runtimeId} does not verify against the baked public key - refusing to publish it.");
        return 1;
    }

    var size = new FileInfo(target).Length;
    entries.Add((runtimeId, asset, size, sha256, string.IsNullOrEmpty(signature) ? null : signature));
    Console.WriteLine($"{asset}: {size / 1024 / 1024} MB, sha256 {sha256[..12]}..., {(string.IsNullOrEmpty(signature) ? "NOT signed" : "signed")}");
}

// Written field by field: a file-based app runs with reflection-based JSON switched off.
using (var stream = File.Create(Path.Combine(output, "matmon-agent.json")))
using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
{
    writer.WriteStartObject();
    writer.WriteString("version", version);
    writer.WriteStartArray("packages");
    foreach (var entry in entries)
    {
        writer.WriteStartObject();
        writer.WriteString("runtimeId", entry.RuntimeId);
        writer.WriteString("file", entry.File);
        writer.WriteNumber("size", entry.Size);
        writer.WriteString("sha256", entry.Sha256);
        if (entry.Signature is null)
        {
            writer.WriteNull("signature");
        }
        else
        {
            writer.WriteString("signature", entry.Signature);
        }

        writer.WriteEndObject();
    }

    writer.WriteEndArray();
    writer.WriteEndObject();
}

Console.WriteLine($"matmon-agent.json: {version}, {entries.Count} platforms");
return 0;
