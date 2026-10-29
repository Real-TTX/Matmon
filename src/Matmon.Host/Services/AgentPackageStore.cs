using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Matmon.Probe;

namespace Matmon.Host.Services;

/// <summary>
/// The agent binaries this instance hands out - on its Agents page, to install scripts and to the agents' own
/// auto-update. Two sources, in this order, per platform:
/// <list type="number">
/// <item><b>A folder</b> (<c>Matmon__AgentPackagesPath</c>, default <c>agent</c> under the content root:
/// <c>&lt;rid&gt;/matmon-agent[.exe]</c>, optionally with a <c>.sig</c>) - what <c>scripts/publish-agent.ps1</c> fills for
/// a dev run and what an offline / air-gapped instance is given by hand. A package placed there always wins.</item>
/// <item><b>The agent feed</b> of the instance's channel - the rolling GitHub releases <c>agent-latest</c> (release
/// builds) / <c>agent-nightly</c> (dev builds), each holding one file per platform plus <see cref="AgentFeed.ManifestFile"/>;
/// <c>Matmon__AgentFeedUrl</c> replaces the address. Fetched ON DEMAND: the manifest when someone asks what there is
/// (cached 30 min), a platform's file only when that platform is actually downloaded - verified (SHA-256 from the
/// manifest, the CI signature) BEFORE it is stored under <c>&lt;data&gt;/agent-packages/&lt;version&gt;/</c>, where the
/// current and the previous version are kept.</item>
/// </list>
/// The packages used to be built into every image and the Windows package (~230 MB for five platforms) - now an install
/// carries none and fetches the one it needs.
///
/// A rolling release holds only the NEWEST build of its channel, so the agent build an instance serves is not always
/// its own: an instance that has not updated yet serves the channel's newer agent (agents are backwards compatible with
/// their primary - the probe protocol only grows optional fields). The served version is said on the Agents page, and
/// the agents follow it (the manifest names the version per package), not the instance's.
/// </summary>
public sealed partial class AgentPackageStore : IDisposable
{
    /// <summary>The platforms we build, in the order the UI offers them.</summary>
    public static readonly IReadOnlyList<AgentPlatform> Platforms =
    [
        new("win-x64", "Windows (x64)", "matmon-agent.exe"),
        new("linux-x64", "Linux (x64)", "matmon-agent"),
        new("linux-arm64", "Linux (ARM64, e.g. Raspberry Pi)", "matmon-agent"),
        new("osx-arm64", "macOS (Apple Silicon)", "matmon-agent"),
        new("osx-x64", "macOS (Intel)", "matmon-agent")
    ];

    private static readonly TimeSpan ManifestLifetime = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ManifestTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _root;
    private readonly AgentFeedSettings _feed;
    private readonly ILogger? _logger;
    private readonly HttpClient? _http;
    private readonly ConcurrentDictionary<string, (DateTime WrittenUtc, long Length, string Sha256)> _hashes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _downloads = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _manifestGate = new(1, 1);
    private AgentFeedManifest? _manifest;
    private DateTimeOffset _manifestFetchedUtc;
    private DateTimeOffset _retryManifestUtc;
    private string? _problem;

    public AgentPackageStore(IHostEnvironment environment, MatmonRuntimeOptions options, AgentFeedSettings? feed = null, ILogger<AgentPackageStore>? logger = null)
    {
        var configured = string.IsNullOrWhiteSpace(options.AgentPackagesPath) ? "agent" : options.AgentPackagesPath;
        _root = Path.IsPathRooted(configured)
            ? configured
            : Path.GetFullPath(Path.Combine(environment.ContentRootPath, configured));
        _feed = feed ?? new AgentFeedSettings(null, null);
        _logger = logger;
        if (_feed.FeedUrl is not null && _feed.CacheDirectory is not null)
        {
            _http = new HttpClient(_feed.Handler ?? new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All }, disposeHandler: _feed.Handler is null)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd($"Matmon/{_feed.InstanceVersion}");
        }
    }

    /// <summary>The folder a package can be placed in by hand (and where publish-agent.ps1 writes).</summary>
    public string RootPath => _root;

    /// <summary>The feed this instance follows, or null (a local build, unless Matmon__AgentFeedUrl names one).</summary>
    public string? FeedUrl => _http is null ? null : _feed.FeedUrl;

    public string InstanceVersion => _feed.InstanceVersion;

    /// <summary>The packages placed in the folder by hand - no network.</summary>
    public IReadOnlyList<AgentPackage> GetPackages() =>
        Platforms
            .Select(platform => TryGet(platform.RuntimeId))
            .OfType<AgentPackage>()
            .ToArray();

    /// <summary>The hand-placed package for one platform, or null. Only the fixed platform list is ever resolved - the id
    /// never becomes part of a path unchecked.</summary>
    public AgentPackage? TryGet(string? runtimeId)
    {
        var platform = FindPlatform(runtimeId);
        if (platform is null)
        {
            return null;
        }

        var file = new FileInfo(Path.Combine(_root, platform.RuntimeId, platform.FileName));
        if (!file.Exists)
        {
            return null;
        }

        // The build writes the signature next to the package (scripts/sign-agent-packages.cs); a local build has none.
        var signatureFile = file.FullName + AgentPackageSignature.SignatureSuffix;
        var signature = File.Exists(signatureFile) ? File.ReadAllText(signatureFile).Trim() : null;
        return new AgentPackage(platform, file.FullName, file.Length, file.LastWriteTimeUtc, Sha256(file),
            string.IsNullOrWhiteSpace(signature) ? null : signature, _feed.InstanceVersion, AgentPackageSource.Folder);
    }

    /// <summary>
    /// Every platform this instance can hand out right now: hand-placed packages, then what the feed offers (read from its
    /// manifest - nothing is downloaded here). <see cref="AgentCatalog.Problem"/> says in words why a platform is missing.
    /// </summary>
    public async Task<AgentCatalog> GetCatalogAsync(CancellationToken cancellationToken)
    {
        var manifest = await GetManifestAsync(cancellationToken);
        var packages = new List<AgentPackage>();
        foreach (var platform in Platforms)
        {
            if (TryGet(platform.RuntimeId) is { } placed)
            {
                packages.Add(placed);
                continue;
            }

            var entry = manifest?.Packages.FirstOrDefault(candidate => string.Equals(candidate.RuntimeId, platform.RuntimeId, StringComparison.OrdinalIgnoreCase));
            if (manifest is not null && entry is not null)
            {
                var cached = CachePath(manifest.Version, entry.File);
                var exists = File.Exists(cached);
                packages.Add(new AgentPackage(platform, exists ? cached : null, entry.Size,
                    exists ? File.GetLastWriteTimeUtc(cached) : DateTime.MinValue, entry.Sha256.ToLowerInvariant(), entry.Signature,
                    manifest.Version, exists ? AgentPackageSource.Cache : AgentPackageSource.Feed));
            }
        }

        string? problem = null;
        if (packages.Count < Platforms.Count)
        {
            problem = _http is null
                ? $"This instance follows no agent feed (a local build). Put the packages into {_root} (<platform>/matmon-agent[.exe]) - ./scripts/publish-agent.ps1 does that - or set Matmon__AgentFeedUrl."
                : manifest is null
                    ? $"The agent packages could not be fetched from {_feed.FeedUrl} ({_problem ?? "no answer"}). Without internet access, put them into {_root} (<platform>/matmon-agent[.exe], from the agent release of this channel)."
                    : null;
        }

        return new AgentCatalog(packages, manifest?.Version, problem, FeedUrl);
    }

    /// <summary>
    /// The file of one platform, ready to stream: the hand-placed one, else the cached one, else downloaded from the feed
    /// now and verified first. <see cref="AgentPackageUnavailableException"/> carries the reason in words.
    /// </summary>
    public async Task<AgentPackage> GetFileAsync(string? runtimeId, CancellationToken cancellationToken)
    {
        var platform = FindPlatform(runtimeId) ?? throw new AgentPackageUnavailableException($"There is no agent for '{runtimeId}'.", notFound: true);
        if (TryGet(platform.RuntimeId) is { } placed)
        {
            return placed;
        }

        var catalog = await GetCatalogAsync(cancellationToken);
        var offered = catalog.Packages.FirstOrDefault(package => package.Platform == platform);
        if (offered is null)
        {
            throw new AgentPackageUnavailableException(catalog.Problem ?? $"The agent feed offers no package for {platform.DisplayName}.", notFound: false);
        }

        var manifest = _manifest!;
        var entry = manifest.Packages.First(candidate => string.Equals(candidate.RuntimeId, platform.RuntimeId, StringComparison.OrdinalIgnoreCase));
        var target = CachePath(manifest.Version, entry.File);
        var gate = _downloads.GetOrAdd(platform.RuntimeId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(target) && string.Equals(Sha256(new FileInfo(target)), entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return Cached(platform, manifest, entry, target);
            }

            await DownloadAsync(platform, manifest, entry, target, cancellationToken);
            return Cached(platform, manifest, entry, target);
        }
        finally
        {
            gate.Release();
        }
    }

    private AgentPackage Cached(AgentPlatform platform, AgentFeedManifest manifest, AgentFeedPackage entry, string path) =>
        new(platform, path, new FileInfo(path).Length, File.GetLastWriteTimeUtc(path), entry.Sha256.ToLowerInvariant(), entry.Signature,
            manifest.Version, AgentPackageSource.Cache);

    private async Task DownloadAsync(AgentPlatform platform, AgentFeedManifest manifest, AgentFeedPackage entry, string target, CancellationToken cancellationToken)
    {
        // A signed instance never hands out an unsigned agent - known from the manifest, so it is not even downloaded.
        if (_feed.RequireSignature && string.IsNullOrWhiteSpace(entry.Signature))
        {
            throw new AgentPackageUnavailableException($"The agent feed offers an unsigned package for {platform.DisplayName}, and this instance only hands out signed ones.", notFound: false);
        }

        var directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        var partial = $"{target}.part-{Guid.NewGuid():N}";
        try
        {
            _logger?.LogInformation("Downloading the agent for {RuntimeId} ({Version}) from {Feed}", platform.RuntimeId, manifest.Version, _feed.FeedUrl);
            using (var response = await _http!.GetAsync($"{_feed.FeedUrl}/{entry.File}", HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw new AgentPackageUnavailableException(
                        $"The agent feed answered {(int)response.StatusCode} for {entry.File}. Put the package into {_root}/{platform.RuntimeId}/ instead.", notFound: false);
                }

                using var sha = SHA256.Create();
                using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                await using (var body = await response.Content.ReadAsStreamAsync(cancellationToken))
                await using (var file = File.Create(partial))
                await using (var hashing = new CryptoStream(file, sha, CryptoStreamMode.Write))
                {
                    var buffer = new byte[81920];
                    while (true)
                    {
                        stall.CancelAfter(StallTimeout);
                        int read;
                        try
                        {
                            read = await body.ReadAsync(buffer, stall.Token);
                        }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                        {
                            throw new AgentPackageUnavailableException($"The download of the agent for {platform.DisplayName} stalled.", notFound: false);
                        }

                        if (read <= 0)
                        {
                            break;
                        }

                        await hashing.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    }
                }

                var actual = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
                if (!string.Equals(actual, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new AgentPackageUnavailableException(
                        $"The downloaded agent for {platform.DisplayName} does not match the feed's manifest (sha256 {actual[..12]}…, expected {entry.Sha256[..Math.Min(12, entry.Sha256.Length)]}…) - refused.", notFound: false);
                }

                // The same rule as the agent's own update: a signature that is there must be valid, always.
                if (!string.IsNullOrWhiteSpace(entry.Signature) &&
                    !AgentPackageSignature.Verify(entry.Signature, platform.RuntimeId, manifest.Version, actual, _feed.PublicKeySpki))
                {
                    throw new AgentPackageUnavailableException($"The agent for {platform.DisplayName} from the feed carries an invalid signature - refused.", notFound: false);
                }
            }

            File.Move(partial, target, overwrite: true);
            if (!string.IsNullOrWhiteSpace(entry.Signature))
            {
                File.WriteAllText(target + AgentPackageSignature.SignatureSuffix, entry.Signature);
            }

            // The manifest beside the files: after a restart without internet the cached packages are still served.
            await File.WriteAllTextAsync(Path.Combine(directory, AgentFeed.ManifestFile), JsonSerializer.Serialize(manifest, Json), cancellationToken);
            PruneVersions(directory);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException && !cancellationToken.IsCancellationRequested)
        {
            throw new AgentPackageUnavailableException(
                $"The agent for {platform.DisplayName} could not be downloaded from {_feed.FeedUrl} ({ex.Message}). Without internet access, put it into {_root}/{platform.RuntimeId}/.", notFound: false);
        }
        finally
        {
            try
            {
                File.Delete(partial);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Keeps the version just written and the newest other one (agents of the previous build may still ask).</summary>
    private void PruneVersions(string current)
    {
        try
        {
            var stale = new DirectoryInfo(_feed.CacheDirectory!).GetDirectories()
                .Where(directory => !string.Equals(directory.FullName, Path.GetFullPath(current), StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(directory => directory.LastWriteTimeUtc)
                .Skip(1);
            foreach (var directory in stale)
            {
                directory.Delete(recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogDebug(ex, "Could not tidy the agent package cache");
        }
    }

    private async Task<AgentFeedManifest?> GetManifestAsync(CancellationToken cancellationToken)
    {
        if (_http is null)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        if (_manifest is not null && now - _manifestFetchedUtc < ManifestLifetime)
        {
            return _manifest;
        }

        if (now < _retryManifestUtc)
        {
            return _manifest ??= ReadCachedManifest();
        }

        await _manifestGate.WaitAsync(cancellationToken);
        try
        {
            if (_manifest is not null && DateTimeOffset.UtcNow - _manifestFetchedUtc < ManifestLifetime)
            {
                return _manifest;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ManifestTimeout);
            try
            {
                var fetched = await _http.GetFromJsonAsync<AgentFeedManifest>($"{_feed.FeedUrl}/{AgentFeed.ManifestFile}", Json, timeout.Token);
                var problem = Validate(fetched);
                if (problem is not null)
                {
                    throw new InvalidDataException(problem);
                }

                _manifest = fetched;
                _manifestFetchedUtc = DateTimeOffset.UtcNow;
                _problem = null;
                return _manifest;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException or NotSupportedException ||
                                       (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                _problem = ex is OperationCanceledException ? "no answer within 20 seconds" : ex.Message;
                _retryManifestUtc = DateTimeOffset.UtcNow + RetryAfterFailure;
                _logger?.LogWarning("The agent feed {Feed} could not be read: {Reason}", _feed.FeedUrl, _problem);
                // Keep serving what is known: the last manifest in memory, else the one beside the cached files.
                return _manifest ??= ReadCachedManifest();
            }
        }
        finally
        {
            _manifestGate.Release();
        }
    }

    private AgentFeedManifest? ReadCachedManifest()
    {
        try
        {
            var root = new DirectoryInfo(_feed.CacheDirectory!);
            if (!root.Exists)
            {
                return null;
            }

            foreach (var directory in root.GetDirectories().OrderByDescending(directory => directory.LastWriteTimeUtc))
            {
                var file = Path.Combine(directory.FullName, AgentFeed.ManifestFile);
                if (File.Exists(file) && JsonSerializer.Deserialize<AgentFeedManifest>(File.ReadAllText(file), Json) is { } manifest && Validate(manifest) is null)
                {
                    return manifest;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger?.LogDebug(ex, "Could not read a cached agent manifest");
        }

        return null;
    }

    /// <summary>A manifest whose version or file names could escape the cache folder is not one.</summary>
    internal static string? Validate(AgentFeedManifest? manifest)
    {
        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Version) || !SafeName().IsMatch(manifest.Version) || manifest.Packages is null)
        {
            return "the feed's manifest names no valid version";
        }

        foreach (var package in manifest.Packages)
        {
            if (string.IsNullOrWhiteSpace(package.File) || !SafeName().IsMatch(package.File) ||
                string.IsNullOrWhiteSpace(package.Sha256) || package.Sha256.Length != 64)
            {
                return $"the feed's manifest has an invalid entry ({package.RuntimeId})";
            }
        }

        return null;
    }

    private string CachePath(string version, string file) => Path.Combine(_feed.CacheDirectory!, version, file);

    private static AgentPlatform? FindPlatform(string? runtimeId) =>
        Platforms.FirstOrDefault(candidate => string.Equals(candidate.RuntimeId, runtimeId, StringComparison.OrdinalIgnoreCase));

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

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")]
    private static partial Regex SafeName();

    public void Dispose()
    {
        _http?.Dispose();
        _manifestGate.Dispose();
    }
}

/// <summary>Where the agent feed is and how far its packages are trusted.</summary>
/// <param name="CacheDirectory">Where downloaded packages are kept (<c>&lt;data&gt;/agent-packages</c>); null = no feed.</param>
/// <param name="FeedUrl">The feed folder (manifest + files); null = no feed.</param>
/// <param name="Handler">For the tests: a stand-in for the network.</param>
/// <param name="PublicKeySpki">For the tests: the key signatures are checked against (default: the baked CI key).</param>
/// <param name="RequireSignature">Refuse unsigned packages (a signed build of the instance).</param>
public sealed record AgentFeedSettings(
    string? CacheDirectory,
    string? FeedUrl,
    HttpMessageHandler? Handler = null,
    string? PublicKeySpki = null,
    bool RequireSignature = false,
    string? Version = null)
{
    public string InstanceVersion => Version ?? MatmonVersion.Current;

    /// <summary>What a running instance uses: its channel's feed, cached in its data folder.</summary>
    public static AgentFeedSettings ForInstance(MatmonRuntimeOptions options, string dataDirectory) => new(
        Path.Combine(dataDirectory, "agent-packages"),
        AgentFeed.FeedUrl(options.AgentFeedUrl, MatmonVersion.Channel),
        RequireSignature: AgentPackageSignature.RequiredByThisBuild);
}

/// <summary>The agent feed's layout - shared with the CI script that writes it (scripts/package-agents.cs mirrors it).</summary>
public static class AgentFeed
{
    public const string ManifestFile = "matmon-agent.json";

    public const string DefaultBaseUrl = "https://github.com/Real-TTX/Matmon/releases/download";

    /// <summary>The file a platform's package has in the feed (release assets must have unique names).</summary>
    public static string AssetName(string runtimeId) =>
        runtimeId.StartsWith("win-", StringComparison.OrdinalIgnoreCase) ? $"matmon-agent-{runtimeId}.exe" : $"matmon-agent-{runtimeId}";

    /// <summary>The feed folder for this build's channel, or null for a local build (nothing to follow).</summary>
    public static string? FeedUrl(string? configured, string channel)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim().TrimEnd('/');
        }

        return channel switch
        {
            "Release" => $"{DefaultBaseUrl}/agent-latest",
            "Nightly" => $"{DefaultBaseUrl}/agent-nightly",
            _ => null
        };
    }
}

public sealed record AgentFeedManifest(string Version, IReadOnlyList<AgentFeedPackage> Packages);

public sealed record AgentFeedPackage(string RuntimeId, string File, long Size, string Sha256, string? Signature);

public sealed record AgentPlatform(string RuntimeId, string DisplayName, string FileName)
{
    public bool IsWindows => RuntimeId.StartsWith("win-", StringComparison.Ordinal);
}

public enum AgentPackageSource
{
    /// <summary>Placed in the agent folder by hand (or by publish-agent.ps1).</summary>
    Folder,

    /// <summary>Downloaded from the feed and kept in the data folder.</summary>
    Cache,

    /// <summary>Offered by the feed, fetched when first downloaded.</summary>
    Feed
}

/// <param name="FilePath">The file on disk; null while a feed package has not been downloaded yet.</param>
/// <param name="Signature">The build's signature over platform, version and hash (base64); null for an unsigned (local) build.</param>
/// <param name="Version">The agent build this package is - what an agent updating to it will report.</param>
public sealed record AgentPackage(
    AgentPlatform Platform,
    string? FilePath,
    long Length,
    DateTime WrittenUtc,
    string Sha256,
    string? Signature = null,
    string? Version = null,
    AgentPackageSource Source = AgentPackageSource.Folder)
{
    public double Megabytes => Math.Round(Length / 1024.0 / 1024.0, 1);
}

/// <param name="FeedVersion">The version the feed offers (null without a feed or without an answer).</param>
/// <param name="Problem">Why platforms are missing, in words - null when nothing is.</param>
public sealed record AgentCatalog(IReadOnlyList<AgentPackage> Packages, string? FeedVersion, string? Problem, string? FeedUrl)
{
    public AgentPackage? Find(string runtimeId) =>
        Packages.FirstOrDefault(package => string.Equals(package.Platform.RuntimeId, runtimeId, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A package cannot be handed out - the message is meant for the person who asked.</summary>
public sealed class AgentPackageUnavailableException(string message, bool notFound) : Exception(message)
{
    public bool NotFound { get; } = notFound;
}
