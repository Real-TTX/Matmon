using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace Matmon.Host.Services.SelfUpdate;

/// <summary>
/// The Windows way: the instance runs as a Windows service out of a program folder (<c>install-service</c>), checks the
/// feed, downloads and verifies the package (hash, CI signature, <c>--version</c> of the new build) while it keeps
/// running, and hands the swap to <see cref="WindowsUpdateApplier"/> - started from a COPY of the running program
/// folder, so the build doing a rollback is the build known to work.
/// </summary>
public sealed class WindowsSelfUpdateTarget(string? configuredFeedUrl, string workspaceDirectory, IHttpClientFactory httpClientFactory) : ISelfUpdateTarget
{
    private WindowsUpdateManifest? _manifest;
    private string? _feed;

    public SelfUpdateMode Mode => SelfUpdateMode.WindowsService;

    public string StateDirectory => Path.Combine(workspaceDirectory, "update");

    public static string InstallDirectory => Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

    public bool IsAvailable(out string reason)
    {
        if (!OperatingSystem.IsWindows() || !WindowsServiceHelpers.IsWindowsService())
        {
            reason = "not running as a Windows service";
            return false;
        }

        if (ServiceName() is null)
        {
            reason = "cannot find the Windows service this program runs as";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public async Task<SelfUpdateOffer> CheckAsync(CancellationToken cancellationToken)
    {
        _feed = WindowsUpdateFeed.FeedUrl(configuredFeedUrl, MatmonVersion.Channel);
        if (_feed is null)
        {
            return SelfUpdateOffer.None($"this is a local build ({MatmonVersion.Current}) - there is no release channel to follow");
        }

        using var client = Client();
        _manifest = await WindowsUpdateFeed.ReadManifestAsync(client, _feed, cancellationToken);
        if (string.Equals(_manifest.Version, MatmonVersion.Current, StringComparison.OrdinalIgnoreCase))
        {
            return SelfUpdateOffer.None($"up to date ({MatmonVersion.Channel.ToLowerInvariant()} channel)");
        }

        return new SelfUpdateOffer(true, _manifest.Version, $"{MatmonVersion.Channel.ToLowerInvariant()} channel", _manifest.Version);
    }

    public IReadOnlyList<SelfUpdateStep> InstallSteps { get; } =
    [
        new("download", "Downloading the package"),
        new("verify", "Checking checksum and signature"),
        new("unpack", "Unpacking and test-starting the new build"),
        new("handover", "Handing over to the updater")
    ];

    public async Task ApplyAsync(SelfUpdateOffer offer, ISelfUpdateProgress? progress, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        if (_feed is null || _manifest is null)
        {
            throw new InvalidOperationException("check for updates again first - the offer is no longer current");
        }

        var service = ServiceName() ?? throw new InvalidOperationException("cannot find the Windows service this program runs as");
        var install = InstallDirectory;
        var updater = $"{install}.updater";
        Directory.CreateDirectory(StateDirectory);
        var package = Path.Combine(StateDirectory, WindowsUpdateFeed.PackageFile);

        // The manifest is read again here: the feed is a rolling release, and the one read at check time may describe
        // a build CI has replaced since - that is how an install failed with "does not match its manifest".
        progress?.Step("download");
        WindowsUpdateManifest manifest;
        using (var client = Client())
        {
            var verifying = false;
            manifest = await WindowsUpdateFeed.DownloadLatestAsync(client, _feed, package, AgentPackageSignature.RequiredByThisBuild, state =>
            {
                progress?.Download(state);
                if (state.Complete && !verifying)
                {
                    verifying = true; // every byte is there: the hash and the signature are checked next
                    progress?.Step("verify");
                }
            }, cancellationToken);
        }

        progress?.Step("unpack");
        var staged = $"{install}.new-{Sanitize(manifest.Version)}";
        TryDeleteDirectory(staged);
        ZipFile.ExtractToDirectory(package, staged, overwriteFiles: true);
        File.Delete(package);
        await VerifyRunsAsync(Path.Combine(staged, "Matmon.Host.exe"), manifest.Version, cancellationToken);

        // The updater is a copy of the RUNNING build, outside the folder it is about to replace.
        progress?.Step("handover");
        TryDeleteDirectory(updater);
        CopyDirectory(install, updater);

        ServiceControl.LaunchDetached(Path.Combine(updater, "Matmon.Host.exe"),
        [
            WindowsUpdateApplier.Command,
            "--install", install,
            "--staged", staged,
            "--to", manifest.Version,
            "--from", MatmonVersion.Current,
            "--state", StateDirectory,
            "--service", service,
            "--health", HealthUrl()
        ]);
    }

    public Task<SelfUpdateOutcome?> ReadLastOutcomeAsync(CancellationToken cancellationToken)
    {
        // After a self-update Windows' "Installed apps" should name the version that runs now.
        if (OperatingSystem.IsWindows())
        {
            WindowsInstallation.RefreshRegisteredVersion(MatmonVersion.Current);
            // ...and the tray icon and uninstaller should be the ones of this build too.
            WindowsInstallation.RefreshSetupProgram(InstallDirectory);
        }

        var files = new WindowsUpdateFiles(StateDirectory);
        if (files.IsPending)
        {
            return Task.FromResult<SelfUpdateOutcome?>(null); // an update is under way
        }

        var outcome = files.ReadOutcome();
        if (outcome is not null)
        {
            // What an update leaves beside the program folder once it is over; locked (still running) = next time.
            var install = InstallDirectory;
            TryDeleteDirectory($"{install}.updater");
            foreach (var leftover in Directory.EnumerateDirectories(Path.GetDirectoryName(install)!, Path.GetFileName(install) + ".*")
                         .Where(path => path.Contains(".bak-", StringComparison.Ordinal) || path.Contains(".new-", StringComparison.Ordinal) || path.Contains(".failed-", StringComparison.Ordinal)))
            {
                TryDeleteDirectory(leftover);
            }
        }

        return Task.FromResult(outcome);
    }

    // Before anything is stopped: a truncated package or a build that does not start fails HERE.
    private static async Task VerifyRunsAsync(string executable, string expectedVersion, CancellationToken cancellationToken)
    {
        using var process = Process.Start(new ProcessStartInfo(executable, "--version")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("the new build did not start");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var output = (await process.StandardOutput.ReadToEndAsync(timeout.Token)).Trim();
        await process.WaitForExitAsync(timeout.Token);
        if (!string.Equals(output, expectedVersion, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"the new build reports version '{output}', expected '{expectedVersion}'");
        }
    }

    /// <summary>Where this instance answers /healthz locally - from the address it was installed with.</summary>
    internal static string HealthUrl(string? urls = null)
    {
        urls ??= Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://+:8099";
        var first = urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "http://+:8099";
        var uri = new Uri(first.Replace("://+", "://localhost").Replace("://*", "://localhost").Replace("://0.0.0.0", "://localhost").Replace("://[::]", "://localhost"));
        var host = uri.Host is "localhost" ? "127.0.0.1" : uri.Host;
        return $"{uri.Scheme}://{host}:{uri.Port}/healthz";
    }

    private static string? ServiceName() =>
        OperatingSystem.IsWindows() ? ServiceControl.ResolveWindowsServiceName(Environment.ProcessPath) : null;

    private HttpClient Client()
    {
        var client = httpClientFactory.CreateClient("matmon-update");
        client.Timeout = TimeSpan.FromMinutes(15);
        return client;
    }

    private static string Sanitize(string version) =>
        string.Concat(version.Select(character => char.IsLetterOrDigit(character) || character is '.' or '-' ? character : '_'));

    internal static void CopyDirectory(string source, string target)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        }

        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)), overwrite: true);
        }
    }

    internal static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still in use - the next look tidies it.
        }
    }
}

/// <summary>The updater's hand-shake files in the state directory: pending.json while an update runs, last-update.json after, update.log always.</summary>
public sealed class WindowsUpdateFiles(string stateDirectory)
{
    private string PendingPath => Path.Combine(stateDirectory, "pending.json");
    private string OutcomePath => Path.Combine(stateDirectory, "last-update.json");
    private string LogPath => Path.Combine(stateDirectory, "update.log");

    public bool IsPending => File.Exists(PendingPath);

    public void MarkPending(string to, string from)
    {
        Directory.CreateDirectory(stateDirectory);
        File.WriteAllText(PendingPath, JsonSerializer.Serialize(new { to, from, startedUtc = DateTimeOffset.UtcNow }, OutcomeJson.Options));
    }

    public void ClearPending()
    {
        try
        {
            File.Delete(PendingPath);
        }
        catch (IOException)
        {
        }
    }

    public void WriteOutcome(SelfUpdateOutcome outcome)
    {
        Directory.CreateDirectory(stateDirectory);
        File.WriteAllText(OutcomePath, JsonSerializer.Serialize(outcome, OutcomeJson.Options));
    }

    public SelfUpdateOutcome? ReadOutcome()
    {
        try
        {
            return File.Exists(OutcomePath) ? JsonSerializer.Deserialize<SelfUpdateOutcome>(File.ReadAllText(OutcomePath), OutcomeJson.Options) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    public void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(stateDirectory);
            File.AppendAllText(LogPath, $"{DateTimeOffset.UtcNow:u} {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }
}
