using System.Security.Cryptography;
using Matmon.Host.Services;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Matmon.Tests;

// The package store turns a URL segment into a file on disk and hands it out, so what it must never do is
// resolve anything outside its fixed platform list - and it must never label a replaced binary with an old hash.
public sealed class AgentPackageStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("matmon-agent-packages-").FullName;

    private AgentPackageStore NewStore() =>
        new(new PackageHostEnvironment(_root), new MatmonRuntimeOptions { AgentPackagesPath = "agent" });

    private string WritePackage(string runtimeId, string fileName, byte[] content)
    {
        var directory = Path.Combine(_root, "agent", runtimeId);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        File.WriteAllBytes(path, content);
        return path;
    }

    [Fact]
    public void OnlyBundledPlatformsAreOfferedInCatalogOrder()
    {
        WritePackage("linux-x64", "matmon-agent", [1, 2, 3]);
        WritePackage("win-x64", "matmon-agent.exe", [4, 5]);
        WritePackage("osx-arm64", "matmon-agent", [6]);

        var packages = NewStore().GetPackages();

        Assert.Equal(["win-x64", "linux-x64"], packages.Select(package => package.Platform.RuntimeId));
        Assert.Equal(2, packages[0].Length);
    }

    [Theory]
    [InlineData("../agent/win-x64")]
    [InlineData("..")]
    [InlineData("osx-arm64")]
    [InlineData("")]
    [InlineData(null)]
    public void AnythingOutsideThePlatformListResolvesToNothing(string? runtimeId)
    {
        WritePackage("win-x64", "matmon-agent.exe", [1]);

        Assert.Null(NewStore().TryGet(runtimeId));
    }

    [Fact]
    public void TheHashIsTheFilesAndFollowsAReplacement()
    {
        var path = WritePackage("win-x64", "matmon-agent.exe", [1, 2, 3]);
        var store = NewStore();

        Assert.Equal(Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3 })).ToLowerInvariant(), store.TryGet("WIN-X64")?.Sha256);

        File.WriteAllBytes(path, [9, 9, 9, 9]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        Assert.Equal(Convert.ToHexString(SHA256.HashData(new byte[] { 9, 9, 9, 9 })).ToLowerInvariant(), store.TryGet("win-x64")?.Sha256);
    }

    [Fact]
    public void NoPackagesIsAnEmptyListNotAnError()
    {
        Assert.Empty(NewStore().GetPackages());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

file sealed class PackageHostEnvironment(string contentRoot) : IHostEnvironment
{
    public string ApplicationName { get; set; } = "Matmon.Tests";
    public string EnvironmentName { get; set; } = "Development";
    public string ContentRootPath { get; set; } = contentRoot;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
