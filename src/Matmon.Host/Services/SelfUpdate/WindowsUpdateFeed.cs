using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Matmon.Probe;

namespace Matmon.Host.Services.SelfUpdate;

/// <summary>What the feed says about the newest package.</summary>
public sealed record WindowsUpdateManifest(string Version, string File, long Size, string Sha256, string? Signature);

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

    /// <summary>
    /// Downloads the package the manifest describes to <paramref name="target"/> and verifies it - hash, then signature.
    /// <paramref name="requireSignature"/> = this build is signed itself and never runs an unsigned package (known from
    /// the manifest, so nothing is downloaded for one). A package that fails a check is deleted.
    /// <paramref name="progress"/> gets the share downloaded, 0..1, roughly every percent.
    /// </summary>
    public static async Task DownloadAsync(HttpClient client, string feed, WindowsUpdateManifest manifest, string target, bool requireSignature,
        Action<double>? progress, CancellationToken cancellationToken)
    {
        if (requireSignature && string.IsNullOrWhiteSpace(manifest.Signature))
        {
            throw new InvalidOperationException("the feed offers an unsigned package and this build only installs signed ones");
        }

        using var response = await client.GetAsync($"{feed}/{manifest.File}", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? manifest.Size;

        using var sha = SHA256.Create();
        await using (var body = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var file = File.Create(target))
        await using (var hashing = new CryptoStream(file, sha, CryptoStreamMode.Write))
        {
            var buffer = new byte[81920];
            long done = 0;
            var reported = -1;
            int read;
            while ((read = await body.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await hashing.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                done += read;
                var percent = total > 0 ? (int)(done * 100 / total) : 0;
                if (progress is not null && percent != reported)
                {
                    reported = percent;
                    progress(Math.Min(1, (double)done / Math.Max(1, total)));
                }
            }
        }

        var actual = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
        if (!string.Equals(actual, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(target);
            throw new InvalidOperationException($"the downloaded package does not match its manifest (sha256 {actual}, expected {manifest.Sha256})");
        }

        if (!string.IsNullOrWhiteSpace(manifest.Signature) &&
            !AgentPackageSignature.Verify(manifest.Signature, SignaturePlatform, manifest.Version, actual))
        {
            File.Delete(target);
            throw new InvalidOperationException("the package's signature is not valid");
        }
    }
}
