// Packs the published Windows instance into the update feed the Windows service reads (WindowsUpdateFeed):
//   matmon-windows-x64.zip   the program folder (Matmon.Host.exe, wwwroot, mibs, the agent packages)
//   matmon-windows-x64.json  the manifest: version, file, size, sha256 and - when the signing key is given - the CI
//                            signature over platform "instance-win-x64", version and hash (AgentPackageSignature)
//   MatmonSetup-x64.exe      with --setup: the setup program with the zip appended (SetupPayload) - the one file a person
//                            downloads to install Matmon on Windows
//   MatmonSetup-x64-online.exe  with --setup: the same program WITHOUT the zip - the online setup, which downloads the
//                            newest package of its channel from this feed and verifies it like the self-update does
// Run by the image build's windows-package stage:
//   dotnet run scripts/package-windows.cs -- <published folder> <version> <output folder> [--key <private key file>] [--setup <MatmonSetup.exe>]
// A key that does not belong to the public key baked into Matmon fails the build instead of shipping a package every
// signed installation refuses.
#:project ../src/Matmon.Probe/Matmon.Probe.csproj
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Matmon.Probe;

if (args.Length < 3)
{
    Console.Error.WriteLine("usage: dotnet run scripts/package-windows.cs -- <published folder> <version> <output folder> [--key <file>] [--setup <exe>]");
    return 2;
}

var (published, version, output) = (args[0], args[1], args[2]);
string? Option(string name) => Array.IndexOf(args, name) is var index and >= 0 && index + 1 < args.Length ? args[index + 1] : null;
var keyFile = Option("--key");
var setupProgram = Option("--setup");
const string Platform = "instance-win-x64";
const string PackageFile = "matmon-windows-x64.zip";
const string ManifestFile = "matmon-windows-x64.json";

if (string.IsNullOrWhiteSpace(version) || !File.Exists(Path.Combine(published, "Matmon.Host.exe")))
{
    Console.Error.WriteLine("A version and a published win-x64 Matmon.Host are needed.");
    return 2;
}

Directory.CreateDirectory(output);
var package = Path.Combine(output, PackageFile);
File.Delete(package);
ZipFile.CreateFromDirectory(published, package, CompressionLevel.Optimal, includeBaseDirectory: false);

string sha256;
using (var stream = File.OpenRead(package))
{
    sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
}

string? signature = null;
if (!string.IsNullOrWhiteSpace(keyFile) && File.Exists(keyFile) && new FileInfo(keyFile).Length > 0)
{
    using var key = ECDsa.Create();
    key.ImportPkcs8PrivateKey(Convert.FromBase64String(File.ReadAllText(keyFile).Trim()), out _);
    signature = AgentPackageSignature.Sign(key, Platform, version, sha256);
    if (!AgentPackageSignature.Verify(signature, Platform, version, sha256))
    {
        Console.Error.WriteLine("The signing key does not belong to the public key in AgentPackageSignature.PublicKeySpki - refusing to sign.");
        return 1;
    }
}

// Written field by field: a file-based app runs with reflection-based JSON switched off.
var size = new FileInfo(package).Length;
using (var stream = File.Create(Path.Combine(output, ManifestFile)))
using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
{
    writer.WriteStartObject();
    writer.WriteString("version", version);
    writer.WriteString("file", PackageFile);
    writer.WriteNumber("size", size);
    writer.WriteString("sha256", sha256);
    if (signature is null)
    {
        writer.WriteNull("signature");
    }
    else
    {
        writer.WriteString("signature", signature);
    }

    writer.WriteEndObject();
}

Console.WriteLine($"{PackageFile}: {version}, {size / 1024 / 1024} MB, sha256 {sha256[..12]}..., {(signature is null ? "NOT signed" : "signed")}");
if (!string.IsNullOrWhiteSpace(setupProgram))
{
    var setupFile = Path.Combine(output, "MatmonSetup-x64.exe");
    using (var setup = File.OpenRead(setupProgram))
    using (var zip = File.OpenRead(package))
    using (var combined = File.Create(setupFile))
    {
        SetupPayload.Combine(setup, zip, combined);
    }

    Console.WriteLine($"MatmonSetup-x64.exe: {new FileInfo(setupFile).Length / 1024 / 1024} MB");

    // The online setup: the same program with nothing appended - it downloads the package of its channel instead.
    var onlineFile = Path.Combine(output, "MatmonSetup-x64-online.exe");
    File.Copy(setupProgram, onlineFile, overwrite: true);
    Console.WriteLine($"MatmonSetup-x64-online.exe: {new FileInfo(onlineFile).Length / 1024 / 1024} MB");
}

return 0;
