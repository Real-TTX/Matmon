using System.Net;
using System.Security.Cryptography;
using Matmon.Host.Services.SelfUpdate;

namespace Matmon.Tests;

/// <summary>The download both the self-update and the online setup trust a Windows package through.</summary>
public sealed class WindowsUpdateFeedTests
{
    private static readonly byte[] Package = Enumerable.Range(0, 300_000).Select(i => (byte)(i % 251)).ToArray();
    private static string Sha => Convert.ToHexString(SHA256.HashData(Package)).ToLowerInvariant();

    [Fact]
    public async Task A_package_that_matches_its_manifest_is_kept_and_progress_reaches_the_end()
    {
        var target = Path.GetTempFileName();
        var shares = new List<double>();
        try
        {
            using var client = new HttpClient(new Feed());
            var manifest = await WindowsUpdateFeed.ReadManifestAsync(client, "http://feed.test/windows-latest", default);
            Assert.Equal("0.1.42", manifest.Version);

            await WindowsUpdateFeed.DownloadAsync(client, "http://feed.test/windows-latest", manifest, target, requireSignature: false, shares.Add, default);
            Assert.Equal(Package, await File.ReadAllBytesAsync(target));
            Assert.Equal(1.0, shares[^1]);
            Assert.Equal(shares.Order(), shares); // only ever forward
        }
        finally
        {
            File.Delete(target);
        }
    }

    [Fact]
    public async Task A_package_that_does_not_match_is_deleted()
    {
        var target = Path.Combine(Path.GetTempPath(), $"feed-test-{Guid.NewGuid():N}.zip");
        using var client = new HttpClient(new Feed());
        var manifest = new WindowsUpdateManifest("0.1.42", WindowsUpdateFeed.PackageFile, Package.Length, new string('0', 64), null);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WindowsUpdateFeed.DownloadAsync(client, "http://feed.test/windows-latest", manifest, target, false, null, default));
        Assert.Contains("does not match", error.Message);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task A_signed_build_refuses_an_unsigned_package_before_downloading_it()
    {
        var feed = new Feed();
        using var client = new HttpClient(feed);
        var manifest = new WindowsUpdateManifest("0.1.42", WindowsUpdateFeed.PackageFile, Package.Length, Sha, null);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WindowsUpdateFeed.DownloadAsync(client, "http://feed.test/windows-latest", manifest, Path.GetTempFileName(), requireSignature: true, null, default));
        Assert.Equal(0, feed.PackageRequests);

        // A signature that is there but not the CI key's is refused after the download, and the file goes.
        var forged = manifest with { Signature = Convert.ToBase64String(new byte[64]) };
        var target = Path.Combine(Path.GetTempPath(), $"feed-test-{Guid.NewGuid():N}.zip");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WindowsUpdateFeed.DownloadAsync(client, "http://feed.test/windows-latest", forged, target, false, null, default));
        Assert.Contains("signature", error.Message);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void The_channel_decides_the_feed_and_a_configured_one_wins()
    {
        Assert.Equal($"{WindowsUpdateFeed.DefaultBaseUrl}/windows-latest", WindowsUpdateFeed.FeedUrl(null, "Release"));
        Assert.Equal($"{WindowsUpdateFeed.DefaultBaseUrl}/windows-nightly", WindowsUpdateFeed.FeedUrl(null, "Nightly"));
        Assert.Null(WindowsUpdateFeed.FeedUrl(null, "Local"));
        Assert.Equal("https://mirror.example/matmon", WindowsUpdateFeed.FeedUrl(" https://mirror.example/matmon/ ", "Nightly"));
    }

    private sealed class Feed : HttpMessageHandler
    {
        public int PackageRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith(WindowsUpdateFeed.ManifestFile, StringComparison.Ordinal))
            {
                var json = $$"""{"version":"0.1.42","file":"{{WindowsUpdateFeed.PackageFile}}","size":{{Package.Length}},"sha256":"{{Sha}}","signature":null}""";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") });
            }

            if (path.EndsWith(WindowsUpdateFeed.PackageFile, StringComparison.Ordinal))
            {
                PackageRequests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Package) });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
