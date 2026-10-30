using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Matmon.Probe;

namespace Matmon.Host.Services.SelfUpdate;

/// <summary>What the feed says about the newest package.</summary>
public sealed record WindowsUpdateManifest(string Version, string File, long Size, string Sha256, string? Signature);

/// <summary>
/// How far a package download is: bytes so far, of how many, how fast (averaged over the last few seconds), which
/// attempt this is, and - after the connection broke off - a note saying so ("connection dropped - resuming at 80 MB
/// (attempt 2)"). <see cref="Complete"/> = every byte is there and the checks (hash, signature) are about to run.
/// </summary>
public sealed record UpdateDownloadProgress(long Done, long Total, double BytesPerSecond, int Attempt, string? Note, bool Complete = false)
{
    /// <summary>The share downloaded, 0..1 (0 while the size is unknown).</summary>
    public double Share => Total > 0 ? Math.Clamp((double)Done / Total, 0, 1) : 0;

    /// <summary>Seconds still to go at the current speed, or null when that cannot be said yet.</summary>
    public double? SecondsLeft => BytesPerSecond > 1 && Total > Done ? (Total - Done) / BytesPerSecond : null;
}

/// <summary>
/// How patient a download is. <see cref="StallTimeout"/>: how long a read may bring nothing before the connection counts
/// as dead. <see cref="Backoff"/>: the waits before the next attempt, one per attempt in a row that brought NOTHING -
/// when they run out the download gives up (so up to <c>Backoff.Count + 1</c> fruitless attempts). An attempt that got
/// further resets that count: a line that keeps breaking off but keeps moving is not given up on.
/// <see cref="MaxAttempts"/> is the hard ceiling over everything.
/// </summary>
public sealed record UpdateDownloadPolicy(TimeSpan StallTimeout, IReadOnlyList<TimeSpan> Backoff, int MaxAttempts = 60)
{
    public static readonly UpdateDownloadPolicy Default = new(
        TimeSpan.FromSeconds(60),
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30)]);

    public int FruitlessLimit => Backoff.Count + 1;
}

/// <summary>
/// Where the Windows instance finds its packages: a release feed per channel - by default the rolling GitHub releases
/// <c>windows-latest</c> (release builds) and <c>windows-nightly</c> (dev builds) of the Matmon repository, each with a
/// manifest (<see cref="ManifestFile"/>) and the package it describes. <c>Matmon__UpdateFeedUrl</c> replaces the
/// address (a mirror, or a local test feed). ONE implementation of reading and verifying it, used by the self-update
/// of a running instance AND by the online setup (Matmon.Setup links this file) - so both trust a package for the same
/// reasons: its SHA-256 from the manifest and the CI signature over platform <see cref="SignaturePlatform"/>.
/// </summary>
public static class WindowsUpdateFeed
{
    public const string ManifestFile = "matmon-windows-x64.json";
    public const string PackageFile = "matmon-windows-x64.zip";

    /// <summary>The "platform" the package's signature covers - kept apart from the agent's platforms, so an agent
    /// package can never pass as an instance package or the other way round (<see cref="AgentPackageSignature"/>).</summary>
    public const string SignaturePlatform = "instance-win-x64";

    public const string DefaultBaseUrl = "https://github.com/Real-TTX/Matmon/releases/download";

    private static readonly JsonSerializerOptions ManifestJson = new(JsonSerializerDefaults.Web);

    /// <summary>The feed folder for this build's channel, or null for a local build (nothing to follow).</summary>
    public static string? FeedUrl(string? configured, string channel)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim().TrimEnd('/');
        }

        return channel switch
        {
            "Release" => $"{DefaultBaseUrl}/windows-latest",
            "Nightly" => $"{DefaultBaseUrl}/windows-nightly",
            _ => null
        };
    }

    public static async Task<WindowsUpdateManifest> ReadManifestAsync(HttpClient client, string feed, CancellationToken cancellationToken) =>
        await client.GetFromJsonAsync<WindowsUpdateManifest>($"{feed}/{ManifestFile}", ManifestJson, cancellationToken)
            ?? throw new InvalidOperationException("the update feed returned no manifest");

    /// <summary>How long a download may go without a single byte before the connection counts as broken (and is resumed).</summary>
    public static TimeSpan StallTimeout => UpdateDownloadPolicy.Default.StallTimeout;

    private static readonly TimeSpan ReportEvery = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan SpeedWindow = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Downloads the package the manifest describes to <paramref name="target"/> and verifies it - hash, then signature.
    /// <paramref name="requireSignature"/> = this build is signed itself and never runs an unsigned package (known from
    /// the manifest, so nothing is downloaded for one). A package that fails a check is deleted.
    /// A connection that drops, stalls or ends early is RESUMED where it broke off (an HTTP range request; a server that
    /// answers a range with the whole file starts over), with a growing wait between attempts - see
    /// <see cref="UpdateDownloadPolicy"/>. A 115 MB package over a flaky line used to fail outright at the first drop.
    /// <paramref name="progress"/> gets an <see cref="UpdateDownloadProgress"/> a few times a second and at every event.
    /// </summary>
    public static async Task DownloadAsync(HttpClient client, string feed, WindowsUpdateManifest manifest, string target, bool requireSignature,
        Action<UpdateDownloadProgress>? progress, CancellationToken cancellationToken, UpdateDownloadPolicy? policy = null)
    {
        if (requireSignature && string.IsNullOrWhiteSpace(manifest.Signature))
        {
            throw new InvalidOperationException("the feed offers an unsigned package and this build only installs signed ones");
        }

        policy ??= UpdateDownloadPolicy.Default;
        var download = new Download(client, $"{feed}/{manifest.File}", manifest.Size, policy, progress);
        string actual;
        try
        {
            actual = await download.RunAsync(target, cancellationToken);
        }
        catch
        {
            TryDelete(target);
            throw;
        }

        if (!string.Equals(actual, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(target);
            throw new InvalidOperationException($"the downloaded package does not match its manifest (sha256 {actual}, expected {manifest.Sha256})");
        }

        if (!string.IsNullOrWhiteSpace(manifest.Signature) &&
            !AgentPackageSignature.Verify(manifest.Signature, SignaturePlatform, manifest.Version, actual))
        {
            TryDelete(target);
            throw new InvalidOperationException("the package's signature is not valid");
        }
    }

    internal static string Megabytes(long bytes) =>
        (bytes / 1048576.0).ToString(bytes < 10 * 1048576L ? "0.0" : "0", System.Globalization.CultureInfo.InvariantCulture);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>One download with its resume state: the bytes written so far, the hash over exactly those bytes, the size.</summary>
    private sealed class Download(HttpClient client, string url, long expectedSize, UpdateDownloadPolicy policy, Action<UpdateDownloadProgress>? progress)
    {
        private IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long _done;
        private long _total = expectedSize;
        private EntityTagHeaderValue? _etag;
        private int _attempt;
        private string? _note;
        private long _lastReport;
        private readonly Queue<(long Ticks, long Done)> _samples = new();

        public async Task<string> RunAsync(string target, CancellationToken cancellationToken)
        {
            await using var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            var fruitless = 0;
            while (true)
            {
                _attempt++;
                var before = _done;
                try
                {
                    await AttemptAsync(file, cancellationToken);
                    return await FinishAsync(file, cancellationToken);
                }
                catch (Exception ex) when (IsTransient(ex) && !cancellationToken.IsCancellationRequested)
                {
                    if (_total > 0 && _done == _total)
                    {
                        // Every byte arrived and only the connection's ending failed: nothing to resume - the hash decides.
                        return await FinishAsync(file, cancellationToken);
                    }

                    fruitless = _done > before ? 0 : fruitless + 1;
                    var reason = Describe(ex);
                    if (fruitless >= policy.FruitlessLimit || _attempt >= policy.MaxAttempts)
                    {
                        throw new IOException(
                            $"the download kept breaking off - {Megabytes(_done)} of {Megabytes(_total)} MB after {_attempt} attempt{(_attempt == 1 ? "" : "s")} " +
                            $"(last: {reason}); try again later, or check the connection and any proxy between this machine and the feed", ex);
                    }

                    var wait = policy.Backoff.Count == 0 ? TimeSpan.Zero : policy.Backoff[Math.Clamp(fruitless - 1, 0, policy.Backoff.Count - 1)];
                    _note = _done > 0
                        ? $"{Capitalise(reason)} - resuming at {Megabytes(_done)} MB (attempt {_attempt + 1})"
                        : $"{Capitalise(reason)} - trying again (attempt {_attempt + 1})";
                    _samples.Clear();
                    Report(force: true);
                    await Task.Delay(wait, cancellationToken);
                }
            }
        }

        private async Task<string> FinishAsync(FileStream file, CancellationToken cancellationToken)
        {
            await file.FlushAsync(cancellationToken);
            Report(force: true, complete: true);
            using var hash = _hash;
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }

        private async Task AttemptAsync(FileStream file, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (_done > 0)
            {
                request.Headers.Range = new RangeHeaderValue(_done, null);
                // Only the same file may be continued: with a strong ETag a changed file comes back whole (200).
                if (_etag is { IsWeak: false })
                {
                    request.Headers.IfRange = new RangeConditionHeaderValue(_etag);
                }
            }

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (_done > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                // Asked for bytes past the end: the size is not what this download thinks - start over.
                Restart(file);
                throw new TransientDownloadException("the server could not continue the download");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw Retryable(response.StatusCode)
                    ? new TransientDownloadException($"the server answered {(int)response.StatusCode} {response.ReasonPhrase}")
                    : new HttpRequestException($"the server answered {(int)response.StatusCode} {response.ReasonPhrase}", null, response.StatusCode);
            }

            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                var range = response.Content.Headers.ContentRange;
                if (range is not { HasRange: true } || range.From != _done || (range.HasLength && _total > 0 && range.Length != _total))
                {
                    // Not the bytes that were asked for, or a file of another size - do not stitch it on.
                    Restart(file);
                    throw new TransientDownloadException("the server sent a part that does not fit");
                }

                if (range.HasLength)
                {
                    _total = range.Length!.Value;
                }
            }
            else
            {
                if (_done > 0)
                {
                    // A server (or proxy) that ignores the range sends the whole file again.
                    Restart(file);
                    _note = "The server does not resume downloads - starting over";
                    Report(force: true);
                }

                if (response.Content.Headers.ContentLength is { } length)
                {
                    _total = length;
                }
            }

            _etag ??= response.Headers.ETag;

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var buffer = new byte[81920];
            while (true)
            {
                stall.CancelAfter(policy.StallTimeout);
                int read;
                try
                {
                    read = await body.ReadAsync(buffer, stall.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TransientDownloadException(
                        $"nothing arrived for {policy.StallTimeout.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} seconds");
                }

                if (read <= 0)
                {
                    break;
                }

                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                _hash.AppendData(buffer, 0, read);
                _done += read;
                Report(force: false);
            }

            if (_total > 0 && _done < _total)
            {
                throw new TransientDownloadException("the connection ended early");
            }

            if (_total > 0 && _done > _total)
            {
                Restart(file);
                throw new TransientDownloadException("the server sent more than the package's size");
            }
        }

        private void Restart(FileStream file)
        {
            file.SetLength(0);
            file.Position = 0;
            _hash.Dispose();
            _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            _done = 0;
            _etag = null;
            _samples.Clear();
        }

        private void Report(bool force, bool complete = false)
        {
            if (progress is null)
            {
                return;
            }

            var now = Stopwatch.GetTimestamp();
            if (!force && Stopwatch.GetElapsedTime(_lastReport, now) < ReportEvery && _lastReport != 0)
            {
                return;
            }

            _lastReport = now;
            _samples.Enqueue((now, _done));
            while (_samples.Count > 2 && Stopwatch.GetElapsedTime(_samples.Peek().Ticks, now) > SpeedWindow)
            {
                _samples.Dequeue();
            }

            var (firstTicks, firstDone) = _samples.Peek();
            var seconds = Stopwatch.GetElapsedTime(firstTicks, now).TotalSeconds;
            var speed = seconds > 0.2 ? Math.Max(0, (_done - firstDone) / seconds) : 0;
            progress(new UpdateDownloadProgress(_done, _total, speed, _attempt, _note, complete));
        }

        private static bool Retryable(HttpStatusCode status) =>
            (int)status >= 500 || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;

        // What may go right on the next attempt: a dropped or stalled connection, a premature end, a 5xx. Not a 404, not
        // a refused certificate's "forbidden" - those would fail the same way six times.
        private static bool IsTransient(Exception ex) => ex switch
        {
            TransientDownloadException => true,
            HttpRequestException { StatusCode: { } status } => Retryable(status),
            HttpRequestException => true, // no status = the connection itself failed (reset, DNS, premature end)
            IOException => true,
            _ => false
        };

        private static string Describe(Exception ex) => ex switch
        {
            TransientDownloadException => ex.Message,
            HttpIOException { HttpRequestError: HttpRequestError.ResponseEnded } => "the connection ended early",
            HttpRequestException { StatusCode: null } or IOException => "the connection dropped",
            _ => ex.Message
        };

        private static string Capitalise(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
    }

    private sealed class TransientDownloadException(string message) : Exception(message);
}
