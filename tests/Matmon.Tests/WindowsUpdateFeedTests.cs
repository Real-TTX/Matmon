using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Matmon.Host.Services.SelfUpdate;

namespace Matmon.Tests;

/// <summary>The download both the self-update and the online setup trust a Windows package through - and how it keeps
/// going over a line that breaks off (a real instance failed at 80 of 115 MB, then with "the response ended prematurely").</summary>
public sealed class WindowsUpdateFeedTests
{
    private const string FeedUrl = "http://feed.test/windows-latest";
    private static readonly byte[] Package = Enumerable.Range(0, 300_000).Select(i => (byte)(i % 251)).ToArray();
    private static string Sha => Convert.ToHexString(SHA256.HashData(Package)).ToLowerInvariant();
    private static WindowsUpdateManifest Manifest => new("0.1.42", WindowsUpdateFeed.PackageFile, Package.Length, Sha, null);

    // Quick for the tests: a stall is 300 ms, the waits between attempts a millisecond, three fruitless attempts at most.
    private static readonly UpdateDownloadPolicy Quick = new(TimeSpan.FromMilliseconds(300), [TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1)]);

    [Fact]
    public async Task A_package_that_matches_its_manifest_is_kept_and_progress_reaches_the_end()
    {
        var target = Path.GetTempFileName();
        var reports = new List<UpdateDownloadProgress>();
        try
        {
            using var client = new HttpClient(new Feed());
            var manifest = await WindowsUpdateFeed.ReadManifestAsync(client, FeedUrl, default);
            Assert.Equal("0.1.42", manifest.Version);

            await WindowsUpdateFeed.DownloadAsync(client, FeedUrl, manifest, target, requireSignature: false, reports.Add, default);
            Assert.Equal(Package, await File.ReadAllBytesAsync(target));
            Assert.True(reports[^1].Complete);
            Assert.Equal(1.0, reports[^1].Share);
            Assert.Equal(Package.Length, reports[^1].Total);
            Assert.Equal(reports.Select(report => report.Done).Order(), reports.Select(report => report.Done)); // only ever forward
            Assert.All(reports, report => Assert.True(report.BytesPerSecond >= 0));
            Assert.All(reports, report => Assert.Equal(1, report.Attempt));
            Assert.All(reports, report => Assert.Null(report.Note));
        }
        finally
        {
            File.Delete(target);
        }
    }

    [Fact]
    public async Task A_build_published_during_the_download_is_fetched_again_with_the_new_manifest()
    {
        // The rolling release: the package was already replaced, the manifest read first still named the old build
        // (a real instance: "does not match its manifest").
        var stale = new string('7', 64);
        var feed = new Feed { Manifests = call => call == 1 ? ("0.1.41", stale) : ("0.1.42", Sha) };
        var target = Path.GetTempFileName();
        var wait = WindowsUpdateFeed.RepublishWait;
        WindowsUpdateFeed.RepublishWait = TimeSpan.FromMilliseconds(1);
        try
        {
            using var client = new HttpClient(feed);
            var used = await WindowsUpdateFeed.DownloadLatestAsync(client, FeedUrl, target, requireSignature: false, null, default, Quick);

            Assert.Equal("0.1.42", used.Version);
            Assert.Equal(Package, await File.ReadAllBytesAsync(target));
            Assert.Equal(2, feed.ManifestRequests);
            Assert.Equal(2, feed.PackageRequests);
        }
        finally
        {
            WindowsUpdateFeed.RepublishWait = wait;
            File.Delete(target);
        }
    }

    [Fact]
    public async Task A_mismatch_that_stays_after_reading_the_manifest_again_is_an_error()
    {
        var feed = new Feed { Manifests = _ => ("0.1.42", new string('7', 64)) };
        var target = Path.GetTempFileName();
        var wait = WindowsUpdateFeed.RepublishWait;
        WindowsUpdateFeed.RepublishWait = TimeSpan.FromMilliseconds(1);
        try
        {
            using var client = new HttpClient(feed);
            await Assert.ThrowsAsync<PackageMismatchException>(() =>
                WindowsUpdateFeed.DownloadLatestAsync(client, FeedUrl, target, requireSignature: false, null, default, Quick));
            Assert.Equal(1, feed.PackageRequests);
            Assert.False(File.Exists(target));
        }
        finally
        {
            WindowsUpdateFeed.RepublishWait = wait;
        }
    }

    [Fact]
    public async Task A_connection_that_drops_is_resumed_with_a_range_request()
    {
        var feed = new Feed { Behaviour = (call, from) => call == 1 ? Respond.Drop(from, after: 120_000) : Respond.Normal(from) };
        var reports = new List<UpdateDownloadProgress>();
        await Download(feed, reports.Add);

        Assert.Equal([null, 120_000L], feed.RangeStarts);
        var note = reports.First(report => report.Note is not null);
        Assert.Contains("resuming at", note.Note);
        Assert.Contains("attempt 2", note.Note);
        Assert.Equal(2, reports[^1].Attempt);
        Assert.True(reports[^1].Complete);
    }

    [Fact]
    public async Task A_response_that_ends_early_is_resumed()
    {
        var feed = new Feed { Behaviour = (call, from) => call == 1 ? Respond.EndEarly(from, after: 50_000) : Respond.Normal(from) };
        var reports = new List<UpdateDownloadProgress>();
        await Download(feed, reports.Add);

        Assert.Equal([null, 50_000L], feed.RangeStarts);
        Assert.Contains(reports, report => report.Note?.Contains("ended early", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task A_server_that_ignores_the_range_makes_the_download_start_over()
    {
        var feed = new Feed { Behaviour = (call, _) => call == 1 ? Respond.Drop(null, after: 100_000) : Respond.Normal(null) };
        var reports = new List<UpdateDownloadProgress>();
        await Download(feed, reports.Add);

        Assert.Equal([null, 100_000L], feed.RangeStarts); // it asked to continue ...
        Assert.Contains(reports, report => report.Note?.Contains("starting over", StringComparison.Ordinal) == true); // ... got everything again
    }

    [Fact]
    public async Task A_stalled_download_is_resumed()
    {
        var feed = new Feed { Behaviour = (call, from) => call == 1 ? Respond.Stall(from, after: 90_000) : Respond.Normal(from) };
        var reports = new List<UpdateDownloadProgress>();
        await Download(feed, reports.Add);

        Assert.Equal([null, 90_000L], feed.RangeStarts);
        Assert.Contains(reports, report => report.Note?.Contains("Nothing arrived for 0.3 seconds - resuming at 0.1 MB", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task A_part_that_does_not_fit_is_not_stitched_on()
    {
        // The server answers the range with a part starting somewhere else: the download starts over rather than corrupt.
        var feed = new Feed
        {
            Behaviour = (call, from) => call switch
            {
                1 => Respond.Drop(from, after: 80_000),
                2 => Respond.Normal(from + 10),
                _ => Respond.Normal(from)
            }
        };
        await Download(feed, null);
        Assert.Equal([null, 80_000L, null], feed.RangeStarts);
    }

    [Fact]
    public async Task A_line_that_breaks_off_but_keeps_moving_is_not_given_up_on()
    {
        // Every request brings 20 kB and drops: 15 attempts for 300 kB, far more than the three fruitless ones allowed.
        var feed = new Feed { Behaviour = (_, from) => Respond.Drop(from, after: (from ?? 0) + 20_000) };
        var reports = new List<UpdateDownloadProgress>();
        await Download(feed, reports.Add);
        Assert.True(feed.PackageRequests >= 15);
    }

    [Fact]
    public async Task A_real_server_that_closes_the_connection_mid_body_is_resumed()
    {
        // Over a real socket: the server promises the whole package and closes after 70 kB - the HTTP stack's own
        // "response ended prematurely" (the error the real instance hit), then it answers the range.
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var ranges = new List<long?>();
        var server = Task.Run(async () =>
        {
            for (var call = 1; call <= 2; call++)
            {
                using var socket = await listener.AcceptTcpClientAsync();
                var stream = socket.GetStream();
                var head = new System.Text.StringBuilder();
                var one = new byte[1];
                while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && await stream.ReadAsync(one) == 1)
                {
                    head.Append((char)one[0]);
                }

                var rangeLine = head.ToString().Split("\r\n").FirstOrDefault(line => line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase));
                long? from = rangeLine is null ? null : long.Parse(rangeLine.Split('=')[1].TrimEnd('-'));
                ranges.Add(from);
                var offset = (int)(from ?? 0);
                var header = from is null
                    ? $"HTTP/1.1 200 OK\r\nContent-Length: {Package.Length}\r\nAccept-Ranges: bytes\r\nConnection: close\r\n\r\n"
                    : $"HTTP/1.1 206 Partial Content\r\nContent-Length: {Package.Length - offset}\r\nContent-Range: bytes {offset}-{Package.Length - 1}/{Package.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(header));
                var end = call == 1 ? 70_000 : Package.Length;
                await stream.WriteAsync(Package.AsMemory(offset, end - offset));
                await stream.FlushAsync();
            }
        });

        var reports = new List<UpdateDownloadProgress>();
        var target = Path.Combine(Path.GetTempPath(), $"feed-test-{Guid.NewGuid():N}.zip");
        try
        {
            using var client = new HttpClient();
            await WindowsUpdateFeed.DownloadAsync(client, $"http://127.0.0.1:{port}/feed", Manifest, target, false, reports.Add, default, Quick);
            Assert.Equal(Package, await File.ReadAllBytesAsync(target));
            await server;
            Assert.Equal([null, 70_000L], ranges);
            Assert.Contains(reports, report => report.Note?.StartsWith("The connection ended early - resuming at 0.1 MB (attempt 2)", StringComparison.Ordinal) == true);
        }
        finally
        {
            File.Delete(target);
        }
    }

    [Fact]
    public async Task A_download_that_brings_nothing_gives_up_after_the_attempts_and_says_what_happened()
    {
        var feed = new Feed { Behaviour = (call, from) => call == 1 ? Respond.Drop(from, after: 120_000) : Respond.Drop(from, after: from ?? 0) };
        var target = Path.Combine(Path.GetTempPath(), $"feed-test-{Guid.NewGuid():N}.zip");
        using var client = new HttpClient(feed);

        var error = await Assert.ThrowsAsync<IOException>(() =>
            WindowsUpdateFeed.DownloadAsync(client, FeedUrl, Manifest, target, false, null, default, Quick));

        Assert.Contains("kept breaking off", error.Message);
        Assert.Contains("after 4 attempts", error.Message); // the one that got somewhere + three that did not
        Assert.Equal(4, feed.PackageRequests);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task A_missing_package_is_not_retried()
    {
        var feed = new Feed { Behaviour = (_, _) => new HttpResponseMessage(HttpStatusCode.NotFound) };
        using var client = new HttpClient(feed);
        var target = Path.Combine(Path.GetTempPath(), $"feed-test-{Guid.NewGuid():N}.zip");

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            WindowsUpdateFeed.DownloadAsync(client, FeedUrl, Manifest, target, false, null, default, Quick));
        Assert.Equal(1, feed.PackageRequests);
    }

    [Fact]
    public void Progress_works_out_the_share_and_the_time_left()
    {
        var progress = new UpdateDownloadProgress(80 * 1048576L, 115 * 1048576L, 2.5 * 1048576, 2, "Connection dropped");
        Assert.Equal(80.0 / 115, progress.Share, 6);
        Assert.Equal(14, progress.SecondsLeft!.Value, 0);
        Assert.Null((progress with { BytesPerSecond = 0 }).SecondsLeft);
        Assert.Equal(0, new UpdateDownloadProgress(5, 0, 0, 1, null).Share);
    }

    [Fact]
    public async Task A_package_that_does_not_match_is_deleted()
    {
        var target = Path.Combine(Path.GetTempPath(), $"feed-test-{Guid.NewGuid():N}.zip");
        using var client = new HttpClient(new Feed());
        var manifest = Manifest with { Sha256 = new string('0', 64) };

        var error = await Assert.ThrowsAsync<PackageMismatchException>(() =>
            WindowsUpdateFeed.DownloadAsync(client, FeedUrl, manifest, target, false, null, default));
        Assert.Contains("does not match", error.Message);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task A_signed_build_refuses_an_unsigned_package_before_downloading_it()
    {
        var feed = new Feed();
        using var client = new HttpClient(feed);
        var manifest = Manifest;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WindowsUpdateFeed.DownloadAsync(client, FeedUrl, manifest, Path.GetTempFileName(), requireSignature: true, null, default));
        Assert.Equal(0, feed.PackageRequests);

        // A signature that is there but not the CI key's is refused after the download, and the file goes.
        var forged = manifest with { Signature = Convert.ToBase64String(new byte[64]) };
        var target = Path.Combine(Path.GetTempPath(), $"feed-test-{Guid.NewGuid():N}.zip");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WindowsUpdateFeed.DownloadAsync(client, FeedUrl, forged, target, false, null, default));
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

    private static async Task Download(Feed feed, Action<UpdateDownloadProgress>? progress)
    {
        var target = Path.Combine(Path.GetTempPath(), $"feed-test-{Guid.NewGuid():N}.zip");
        try
        {
            using var client = new HttpClient(feed);
            await WindowsUpdateFeed.DownloadAsync(client, FeedUrl, Manifest, target, requireSignature: false, progress, default, Quick);
            Assert.Equal(Package, await File.ReadAllBytesAsync(target)); // the hash check passed too, or it would have thrown
        }
        finally
        {
            File.Delete(target);
        }
    }

    /// <summary>The stand-in package responses: whole, from a range, and broken in the ways a real line breaks.</summary>
    private static class Respond
    {
        /// <summary>The package from <paramref name="from"/> (206 with Content-Range), or whole (200) for null.</summary>
        public static HttpResponseMessage Normal(long? from) => Build(from, offset => new MemoryStream(Package, (int)offset, Package.Length - (int)offset));

        /// <summary>Sends up to byte <paramref name="after"/> of the package, then the connection resets.</summary>
        public static HttpResponseMessage Drop(long? from, long after) => Build(from, offset => new BrokenStream(offset, after, BrokenStream.Mode.Reset));

        /// <summary>Sends up to byte <paramref name="after"/>, then the body simply ends (a proxy that gave up).</summary>
        public static HttpResponseMessage EndEarly(long? from, long after) => Build(from, offset => new BrokenStream(offset, after, BrokenStream.Mode.End));

        /// <summary>Sends up to byte <paramref name="after"/>, then nothing ever arrives again.</summary>
        public static HttpResponseMessage Stall(long? from, long after) => Build(from, offset => new BrokenStream(offset, after, BrokenStream.Mode.Stall));

        private static HttpResponseMessage Build(long? from, Func<long, Stream> body)
        {
            var offset = from ?? 0;
            var response = new HttpResponseMessage(from is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
            {
                Content = new StreamContent(body(offset))
            };
            response.Content.Headers.ContentLength = Package.Length - offset;
            if (from is not null)
            {
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, Package.Length - 1, Package.Length);
            }

            return response;
        }
    }

    private sealed class BrokenStream(long offset, long breakAt, BrokenStream.Mode mode) : Stream
    {
        public enum Mode { Reset, End, Stall }

        private long _position = offset;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= breakAt || _position >= Package.Length)
            {
                switch (mode)
                {
                    case Mode.Reset:
                        throw new IOException("Unable to read data from the transport connection: connection reset.");
                    case Mode.Stall:
                        await Task.Delay(Timeout.Infinite, cancellationToken);
                        return 0;
                    default:
                        return 0;
                }
            }

            var count = (int)Math.Min(Math.Min(buffer.Length, breakAt - _position), Package.Length - _position);
            Package.AsMemory((int)_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override int Read(byte[] buffer, int offsetInBuffer, int count) =>
            ReadAsync(buffer.AsMemory(offsetInBuffer, count)).AsTask().GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long seekOffset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offsetInBuffer, int count) => throw new NotSupportedException();
    }

    private sealed class Feed : HttpMessageHandler
    {
        public int PackageRequests { get; private set; }

        /// <summary>Where each package request asked to start (null = from the beginning, no Range header).</summary>
        public List<long?> RangeStarts { get; } = [];

        /// <summary>The answer to the n-th (1-based) package request, given where it asked to start.</summary>
        public Func<int, long?, HttpResponseMessage> Behaviour { get; init; } = (_, from) => Respond.Normal(from);

        public int ManifestRequests { get; private set; }

        /// <summary>The version and hash the n-th (1-based) manifest request names - by default the real package.</summary>
        public Func<int, (string Version, string Sha)> Manifests { get; init; } = _ => ("0.1.42", Sha);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith(WindowsUpdateFeed.ManifestFile, StringComparison.Ordinal))
            {
                var (version, sha) = Manifests(++ManifestRequests);
                var json = $$"""{"version":"{{version}}","file":"{{WindowsUpdateFeed.PackageFile}}","size":{{Package.Length}},"sha256":"{{sha}}","signature":null}""";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") });
            }

            if (path.EndsWith(WindowsUpdateFeed.PackageFile, StringComparison.Ordinal))
            {
                PackageRequests++;
                var from = request.Headers.Range?.Ranges.FirstOrDefault()?.From;
                RangeStarts.Add(from);
                return Task.FromResult(Behaviour(PackageRequests, from));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
