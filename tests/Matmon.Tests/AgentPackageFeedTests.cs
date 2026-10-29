using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Matmon.Host.Services;
using Matmon.Probe;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Matmon.Tests;

// The agent packages are no longer in the image: the instance fetches them from the feed of its channel when one is
// first needed. What matters is what it must never do - store (and hand out) a file that does not match the manifest
// or carries a bad signature, download the same file twice, or answer "404" when it simply cannot reach the feed.
public sealed class AgentPackageFeedTests : IDisposable
{
    private const string Feed = "https://feed.example/agent-nightly";
    private const string Version = "nightly-500-20261030-0100";

    private readonly string _root = Directory.CreateTempSubdirectory("matmon-agent-feed-").FullName;
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly byte[] _linux = Encoding.UTF8.GetBytes("linux agent build");
    private readonly byte[] _windows = Encoding.UTF8.GetBytes("windows agent build");

    private string PublicKey => Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());

    private static string Sha(byte[] content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private string Manifest(Func<string, string>? signatureFor = null, string? linuxSha = null) => JsonSerializer.Serialize(new
    {
        version = Version,
        packages = new object[]
        {
            new { runtimeId = "win-x64", file = "matmon-agent-win-x64.exe", size = _windows.Length, sha256 = Sha(_windows), signature = AgentPackageSignature.Sign(_key, "win-x64", Version, Sha(_windows)) },
            new { runtimeId = "linux-x64", file = "matmon-agent-linux-x64", size = _linux.Length, sha256 = linuxSha ?? Sha(_linux), signature = signatureFor?.Invoke("linux-x64") ?? AgentPackageSignature.Sign(_key, "linux-x64", Version, Sha(_linux)) }
        }
    });

    private AgentPackageStore NewStore(StandInFeed feed, bool requireSignature = false) => new(
        new FeedHostEnvironment(_root),
        new MatmonRuntimeOptions { AgentPackagesPath = "agent" },
        new AgentFeedSettings(Path.Combine(_root, "data", "agent-packages"), Feed, feed, PublicKey, requireSignature, Version: "nightly-499-20261029-0100"));

    private StandInFeed FeedWith(string manifest) => new(new Dictionary<string, byte[]>
    {
        [$"{Feed}/matmon-agent.json"] = Encoding.UTF8.GetBytes(manifest),
        [$"{Feed}/matmon-agent-linux-x64"] = _linux,
        [$"{Feed}/matmon-agent-win-x64.exe"] = _windows
    });

    [Fact]
    public async Task TheCatalogComesFromTheManifestWithoutDownloadingAnyPackage()
    {
        var feed = FeedWith(Manifest());
        using var store = NewStore(feed);

        var catalog = await store.GetCatalogAsync(CancellationToken.None);

        Assert.Equal(["win-x64", "linux-x64"], catalog.Packages.Select(package => package.Platform.RuntimeId));
        Assert.All(catalog.Packages, package => Assert.Equal(AgentPackageSource.Feed, package.Source));
        Assert.All(catalog.Packages, package => Assert.Equal(Version, package.Version));
        Assert.Null(catalog.Problem);
        Assert.Equal([$"{Feed}/matmon-agent.json"], feed.Requests);
    }

    [Fact]
    public async Task AVerifiedPackageIsStoredOnceAndServedFromTheCacheAfterwards()
    {
        var feed = FeedWith(Manifest());
        using var store = NewStore(feed);

        var first = await store.GetFileAsync("linux-x64", CancellationToken.None);
        var second = await store.GetFileAsync("LINUX-X64", CancellationToken.None);

        Assert.Equal(_linux, File.ReadAllBytes(first.FilePath!));
        Assert.Equal(first.FilePath, second.FilePath);
        Assert.Equal(AgentPackageSource.Cache, second.Source);
        Assert.Contains(Path.Combine("agent-packages", Version), first.FilePath);
        Assert.Equal(1, feed.Requests.Count(request => request.EndsWith("matmon-agent-linux-x64", StringComparison.Ordinal)));
        // Only the platform that was asked for.
        Assert.DoesNotContain(feed.Requests, request => request.EndsWith(".exe", StringComparison.Ordinal));
    }

    [Fact]
    public async Task APackageThatDoesNotMatchItsManifestIsRefusedAndNotKept()
    {
        using var store = NewStore(FeedWith(Manifest(linuxSha: new string('a', 64))));

        var refused = await Assert.ThrowsAsync<AgentPackageUnavailableException>(() => store.GetFileAsync("linux-x64", CancellationToken.None));

        Assert.Contains("does not match", refused.Message);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "data", "agent-packages"), "matmon-agent-linux-x64*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task APackageWithABadSignatureIsRefused()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var store = NewStore(FeedWith(Manifest(runtimeId => AgentPackageSignature.Sign(other, runtimeId, Version, Sha(_linux)))));

        var refused = await Assert.ThrowsAsync<AgentPackageUnavailableException>(() => store.GetFileAsync("linux-x64", CancellationToken.None));

        Assert.Contains("invalid signature", refused.Message);
    }

    [Fact]
    public async Task ASignedInstanceRefusesAnUnsignedPackageWithoutDownloadingIt()
    {
        var feed = FeedWith(Manifest(_ => string.Empty));
        using var store = NewStore(feed, requireSignature: true);

        await Assert.ThrowsAsync<AgentPackageUnavailableException>(() => store.GetFileAsync("linux-x64", CancellationToken.None));

        Assert.DoesNotContain(feed.Requests, request => request.EndsWith("matmon-agent-linux-x64", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnUnreachableFeedIsSaidInWordsWithWhereToPutTheFiles()
    {
        using var store = NewStore(new StandInFeed(new Dictionary<string, byte[]>()) { Offline = true });

        var catalog = await store.GetCatalogAsync(CancellationToken.None);
        var refused = await Assert.ThrowsAsync<AgentPackageUnavailableException>(() => store.GetFileAsync("win-x64", CancellationToken.None));

        Assert.Empty(catalog.Packages);
        Assert.Contains("could not be fetched", catalog.Problem);
        Assert.Contains(store.RootPath, catalog.Problem);
        Assert.False(refused.NotFound);
    }

    [Fact]
    public async Task CachedPackagesAreStillServedAfterARestartWithoutTheFeed()
    {
        using (var online = NewStore(FeedWith(Manifest())))
        {
            await online.GetFileAsync("linux-x64", CancellationToken.None);
        }

        using var offline = NewStore(new StandInFeed(new Dictionary<string, byte[]>()) { Offline = true });
        var package = await offline.GetFileAsync("linux-x64", CancellationToken.None);

        Assert.Equal(_linux, File.ReadAllBytes(package.FilePath!));
        Assert.Equal(Version, package.Version);
    }

    [Fact]
    public async Task AHandPlacedPackageWinsOverTheFeed()
    {
        var placed = Path.Combine(_root, "agent", "linux-x64");
        Directory.CreateDirectory(placed);
        File.WriteAllBytes(Path.Combine(placed, "matmon-agent"), [7, 7, 7]);
        var feed = FeedWith(Manifest());
        using var store = NewStore(feed);

        var package = await store.GetFileAsync("linux-x64", CancellationToken.None);

        Assert.Equal(AgentPackageSource.Folder, package.Source);
        Assert.Equal("nightly-499-20261029-0100", package.Version);
        Assert.DoesNotContain(feed.Requests, request => request.EndsWith("matmon-agent-linux-x64", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OnlyTheCurrentAndThePreviousVersionAreKept()
    {
        var cache = Path.Combine(_root, "data", "agent-packages");
        foreach (var (old, age) in new[] { ("nightly-1", 3), ("nightly-2", 2) })
        {
            Directory.CreateDirectory(Path.Combine(cache, old));
            Directory.SetLastWriteTimeUtc(Path.Combine(cache, old), DateTime.UtcNow.AddDays(-age));
        }

        using var store = NewStore(FeedWith(Manifest()));
        await store.GetFileAsync("linux-x64", CancellationToken.None);

        Assert.Equal(["nightly-2", Version], Directory.GetDirectories(cache).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("../evil", "matmon-agent-linux-x64")]
    [InlineData("nightly-1", "../../evil")]
    [InlineData("nightly-1", "sub/file")]
    public void AManifestThatCouldEscapeTheCacheIsNotOne(string version, string file)
    {
        var manifest = new AgentFeedManifest(version, [new AgentFeedPackage("linux-x64", file, 1, new string('a', 64), null)]);

        Assert.NotNull(AgentPackageStore.Validate(manifest));
    }

    [Theory]
    [InlineData("Release", "https://github.com/Real-TTX/Matmon/releases/download/agent-latest")]
    [InlineData("Nightly", "https://github.com/Real-TTX/Matmon/releases/download/agent-nightly")]
    [InlineData("Local", null)]
    public void TheFeedFollowsTheChannelAndALocalBuildFollowsNone(string channel, string? expected)
    {
        Assert.Equal(expected, AgentFeed.FeedUrl(null, channel));
        Assert.Equal("https://mirror.example/agents", AgentFeed.FeedUrl("https://mirror.example/agents/", "Local"));
    }

    [Fact]
    public void ALocalBuildWithoutPackagesSaysHowToGetThem()
    {
        using var store = new AgentPackageStore(new FeedHostEnvironment(_root), new MatmonRuntimeOptions());

        var catalog = store.GetCatalogAsync(CancellationToken.None).GetAwaiter().GetResult();

        Assert.Empty(catalog.Packages);
        Assert.Contains("publish-agent.ps1", catalog.Problem);
    }

    public void Dispose()
    {
        _key.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class StandInFeed(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        public bool Offline { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);
            if (Offline)
            {
                throw new HttpRequestException("No such host is known. (feed.example:443)");
            }

            return Task.FromResult(files.TryGetValue(url, out var content)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}

file sealed class FeedHostEnvironment(string contentRoot) : IHostEnvironment
{
    public string ApplicationName { get; set; } = "Matmon.Tests";
    public string EnvironmentName { get; set; } = "Development";
    public string ContentRootPath { get; set; } = contentRoot;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
