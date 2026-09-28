using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Matmon.Host.Services;

/// <summary>
/// The agent binaries this instance ships - the "bundled with the instance" half of the agent: the Docker
/// image publishes Matmon.Agent for every supported platform next to the Host (<c>/app/agent/&lt;rid&gt;/</c>),
/// so an instance always offers exactly the agent build that matches it, and the auto-update later has one
/// authoritative source to compare against.
///
/// Discovered from disk rather than listed in config: a local dev run has no packages (the page says how to
/// build them), an image has all of them, and nothing has to be kept in sync by hand.
/// </summary>
public sealed class AgentPackageStore
{
    /// <summary>The platforms we build, in the order the UI offers them.</summary>
    public static readonly IReadOnlyList<AgentPlatform> Platforms =
    [
        new("win-x64", "Windows (x64)", "matmon-agent.exe"),
        new("linux-x64", "Linux (x64)", "matmon-agent"),
        new("linux-arm64", "Linux (ARM64, e.g. Raspberry Pi)", "matmon-agent")
    ];

    private readonly string _root;
    private readonly ConcurrentDictionary<string, (DateTime WrittenUtc, long Length, string Sha256)> _hashes = new(StringComparer.Ordinal);

    public AgentPackageStore(IHostEnvironment environment, MatmonRuntimeOptions options)
    {
        var configured = string.IsNullOrWhiteSpace(options.AgentPackagesPath) ? "agent" : options.AgentPackagesPath;
        _root = Path.IsPathRooted(configured)
            ? configured
            : Path.GetFullPath(Path.Combine(environment.ContentRootPath, configured));
    }

    public string RootPath => _root;

    public IReadOnlyList<AgentPackage> GetPackages() =>
        Platforms
            .Select(platform => TryGet(platform.RuntimeId))
            .OfType<AgentPackage>()
            .ToArray();

    /// <summary>The package for one platform, or null when this instance does not carry it. Only the
    /// fixed platform list is ever resolved - the id never becomes part of a path unchecked.</summary>
    public AgentPackage? TryGet(string? runtimeId)
    {
        var platform = Platforms.FirstOrDefault(candidate =>
            string.Equals(candidate.RuntimeId, runtimeId, StringComparison.OrdinalIgnoreCase));
        if (platform is null)
        {
            return null;
        }

        var file = new FileInfo(Path.Combine(_root, platform.RuntimeId, platform.FileName));
        if (!file.Exists)
        {
            return null;
        }

        return new AgentPackage(platform, file.FullName, file.Length, file.LastWriteTimeUtc, Sha256(file));
    }

    // Hashing 40 MB takes a moment, and the page lists every package on each render - so once per file
    // version. The key includes write time + length, so a replaced binary is re-hashed, never mislabelled.
    private string Sha256(FileInfo file)
    {
        if (_hashes.TryGetValue(file.FullName, out var cached) &&
            cached.WrittenUtc == file.LastWriteTimeUtc &&
            cached.Length == file.Length)
        {
            return cached.Sha256;
        }

        using var stream = file.OpenRead();
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        _hashes[file.FullName] = (file.LastWriteTimeUtc, file.Length, hash);
        return hash;
    }
}

public sealed record AgentPlatform(string RuntimeId, string DisplayName, string FileName);

public sealed record AgentPackage(AgentPlatform Platform, string FilePath, long Length, DateTime WrittenUtc, string Sha256)
{
    public double Megabytes => Math.Round(Length / 1024.0 / 1024.0, 1);
}
