using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Matmon.Probe;

namespace Matmon.Agent;

/// <summary>
/// Keeps the agent on its instance's build. Two jobs:
///
/// 1. After an update, CONFIRM: this is the new build, started by the updater, which is waiting to hear that
///    it works. It does once the first heartbeat reaches the primary - then the updater keeps it; silence and
///    the updater puts the previous build back.
/// 2. Periodically CHECK the instance's agent manifest; on a different version, download the package for this
///    platform, verify its SHA-256 and that it runs at all (<c>--version</c> must print the offered version),
///    then hand over to the updater - a copy of THIS build, so the thing doing the rollback is the thing that
///    is known to work.
///
/// Only under a service manager (nothing would start the new build otherwise) and switchable off with
/// <c>Matmon__AgentAutoUpdate=false</c>.
/// </summary>
public sealed class AgentUpdateService : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ConfirmationWindow = TimeSpan.FromMinutes(5);

    private readonly ProbeRuntimeOptions _options;
    private readonly SlaveProbeRuntimeState _runtimeState;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AgentUpdateSettings _settings;
    private readonly ILogger<AgentUpdateService> _logger;
    private readonly AgentUpdateFiles _files;

    public AgentUpdateService(
        ProbeRuntimeOptions options,
        SlaveProbeRuntimeState runtimeState,
        IHttpClientFactory httpClientFactory,
        AgentUpdateSettings settings,
        ILogger<AgentUpdateService> logger)
    {
        _options = options;
        _runtimeState = runtimeState;
        _httpClientFactory = httpClientFactory;
        _settings = settings;
        _logger = logger;
        _files = new AgentUpdateFiles(settings.StateDirectory);
    }

    // Next to the executable, so the final swap is a rename on one volume. On Windows the staged file keeps
    // an .exe extension - it is started once (--version) before the swap.
    private static string StagedPath(string target) =>
        OperatingSystem.IsWindows()
            ? Path.Combine(Path.GetDirectoryName(target)!, "matmon-agent.new.exe")
            : target + ".new";

    private static string UpdaterPath(string target) =>
        Path.Combine(Path.GetDirectoryName(target)!, OperatingSystem.IsWindows() ? "matmon-agent-updater.exe" : "matmon-agent-updater");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _runtimeState.AgentUpdateStatus = AgentUpdatePolicy.Describe(_files.ReadLastUpdate());
        if (Environment.ProcessPath is { } self && Path.GetFileNameWithoutExtension(self) == "matmon-agent")
        {
            // Leftovers of a finished update. A still-running updater keeps its file locked on Windows, and the
            // delete fails quietly - the next start tidies up.
            AgentUpdateFiles.TryDelete(UpdaterPath(self));
        }

        await ConfirmPendingUpdateAsync(stoppingToken);

        if (!_settings.Enabled)
        {
            _logger.LogInformation("Agent auto-update is switched off (Matmon__AgentAutoUpdate=false)");
            return;
        }

        if (!AgentServiceControl.IsRunningAsService())
        {
            _logger.LogInformation("Agent auto-update is off: not running as a Windows service or systemd unit");
            return;
        }

        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CheckAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Agent update check failed");
                }

                // Jitter, so a fleet enrolled on the same afternoon does not download in lockstep.
                var jitter = TimeSpan.FromSeconds(Random.Shared.Next(0, 300));
                await Task.Delay(_settings.CheckInterval + jitter, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ConfirmPendingUpdateAsync(CancellationToken stoppingToken)
    {
        var pending = _files.ReadPending();
        if (pending is null || !string.Equals(pending.Version, MatmonVersion.Current, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var deadline = DateTimeOffset.UtcNow + ConfirmationWindow;
        while (DateTimeOffset.UtcNow < deadline && !stoppingToken.IsCancellationRequested)
        {
            if (_runtimeState.Snapshot().IsConnected)
            {
                _files.Confirm(MatmonVersion.Current);
                _logger.LogInformation("Agent update {From} -> {Version} confirmed: the primary answered", pending.FromVersion, pending.Version);
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }

    private async Task CheckAsync(CancellationToken stoppingToken)
    {
        var client = _httpClientFactory.CreateClient("AgentUpdate");
        client.BaseAddress = new Uri(_options.PrimaryUrl!.TrimEnd('/') + "/");
        client.Timeout = TimeSpan.FromMinutes(10);
        client.DefaultRequestHeaders.Add("X-Matmon-Probe-Id", _options.ProbeId);
        client.DefaultRequestHeaders.Add("X-Matmon-Probe-Token", _options.ProbeToken);

        using var response = await client.GetAsync("api/agent/packages", stoppingToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogDebug("The primary has no agent manifest (older than this agent)");
            return;
        }

        response.EnsureSuccessStatusCode();
        var manifest = await response.Content.ReadFromJsonAsync<AgentManifest>(stoppingToken);
        var runtimeId = RuntimeInformation.RuntimeIdentifier;
        var package = manifest?.Packages?.FirstOrDefault(candidate =>
            string.Equals(candidate.RuntimeId, runtimeId, StringComparison.OrdinalIgnoreCase));

        var decision = AgentUpdatePolicy.Decide(
            MatmonVersion.Current, manifest?.Version, package is not null, _files.ReadLastUpdate(), DateTimeOffset.UtcNow);
        if (!decision.ShouldUpdate)
        {
            _logger.LogDebug("No agent update: {Reason}", decision.Reason);
            return;
        }

        var target = Environment.ProcessPath ?? throw new InvalidOperationException("the agent does not know its own executable");
        var service = AgentServiceControl.ResolveServiceName()
            ?? throw new InvalidOperationException("could not find the service this agent runs as");
        var staged = StagedPath(target);
        var updater = UpdaterPath(target);

        _logger.LogInformation("Agent update available: {Current} -> {Version}; downloading {RuntimeId}", MatmonVersion.Current, decision.Version, runtimeId);
        await DownloadAsync(client, package!, staged, stoppingToken);
        await VerifyRunsAsync(staged, decision.Version!, stoppingToken);

        // The updater is a copy of THIS build. A copy still running from an earlier update locks the file -
        // then an update is already under way and this one waits for the next check.
        File.Copy(target, updater, overwrite: true);

        _files.Log($"handing over to the updater for {MatmonVersion.Current} -> {decision.Version}");
        _logger.LogInformation("Handing over to the updater; this service will be restarted as {Version}", decision.Version);
        AgentServiceControl.LaunchDetached(updater,
        [
            "apply-update",
            "--target", target,
            "--staged", staged,
            "--version", decision.Version!,
            "--from", MatmonVersion.Current,
            "--state", _settings.StateDirectory,
            "--service", service
        ]);
    }

    private static async Task DownloadAsync(HttpClient client, AgentManifestPackage package, string staged, CancellationToken stoppingToken)
    {
        AgentUpdateFiles.TryDelete(staged);
        using var response = await client.GetAsync(package.Url.TrimStart('/'), HttpCompletionOption.ResponseHeadersRead, stoppingToken);
        response.EnsureSuccessStatusCode();

        using var sha = SHA256.Create();
        await using (var body = await response.Content.ReadAsStreamAsync(stoppingToken))
        await using (var file = File.Create(staged))
        await using (var hashing = new CryptoStream(file, sha, CryptoStreamMode.Write))
        {
            await body.CopyToAsync(hashing, stoppingToken);
        }

        var actual = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
        if (!string.Equals(actual, package.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            AgentUpdateFiles.TryDelete(staged);
            throw new InvalidOperationException($"the downloaded package does not match its manifest (sha256 {actual}, expected {package.Sha256})");
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(staged, (UnixFileMode)0b111_101_101);
        }
    }

    // Cheap insurance before the service is stopped: a binary for the wrong platform, a truncated file or a
    // build that crashes on start fails HERE, while the working agent is still running.
    private static async Task VerifyRunsAsync(string staged, string expectedVersion, CancellationToken stoppingToken)
    {
        using var process = Process.Start(new ProcessStartInfo(staged, "--version")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("the downloaded agent did not start");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var output = (await process.StandardOutput.ReadToEndAsync(timeout.Token)).Trim();
        await process.WaitForExitAsync(timeout.Token);

        if (!string.Equals(output, expectedVersion, StringComparison.OrdinalIgnoreCase))
        {
            AgentUpdateFiles.TryDelete(staged);
            throw new InvalidOperationException($"the downloaded agent reports version '{output}', expected '{expectedVersion}'");
        }
    }

    private sealed record AgentManifest(string? Version, IReadOnlyList<AgentManifestPackage>? Packages);

    private sealed record AgentManifestPackage(string RuntimeId, string Url, string Sha256, long Size);
}

/// <summary>The agent's update settings, read once at startup.</summary>
public sealed record AgentUpdateSettings(bool Enabled, TimeSpan CheckInterval, string StateDirectory);
