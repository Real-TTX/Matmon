namespace Matmon.Host.Services.SelfUpdate;

/// <summary>What System > Updates shows.</summary>
public sealed record SelfUpdateStatus(
    SelfUpdateMode Mode,
    string? UnavailableReason,
    string CurrentVersion,
    string Channel,
    DateTimeOffset? LastCheckUtc,
    SelfUpdateOffer? Offer,
    string? LastError,
    SelfUpdateOutcome? LastOutcome,
    bool Installing,
    string? AutomaticState,
    SelfUpdateProgress? Progress = null);

/// <summary>
/// Keeps an eye on updates for this installation (Primary only): hourly it reads how the last update ended, asks the
/// installation method whether there is a newer build, and - when the admin switched automatic updates on - installs it
/// inside the install window. Before every install the configuration is saved as a local backup snapshot, so even a
/// rollback across a schema migration has something to go back to.
/// </summary>
public sealed class SelfUpdateService : BackgroundService
{
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(1);

    // Telemetry stays out of the snapshot: it is large, lives in its own database and is not what an update changes.
    private const Matmon.Core.Domain.WorkspaceBackupSection SnapshotSections =
        Matmon.Core.Domain.WorkspaceBackupSection.All & ~Matmon.Core.Domain.WorkspaceBackupSections.History;

    private readonly IReadOnlyList<ISelfUpdateTarget> _targets;
    private readonly IMonitoringWorkspaceStore _store;
    private readonly CloudBackupClient? _cloudBackups;
    private readonly ILogger<SelfUpdateService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile SelfUpdateStatus _status;
    private DateTimeOffset? _handedOverUtc;
    private volatile SelfUpdateProgress? _progress;
    private CancellationToken _stopping;

    // A hand-over normally ends this process within seconds. Still running this long after it, the updater gave up
    // before the swap (its outcome says why) - another attempt may go ahead.
    private static readonly TimeSpan HandOverTimeout = TimeSpan.FromMinutes(10);

    public SelfUpdateService(IEnumerable<ISelfUpdateTarget> targets, IMonitoringWorkspaceStore store, ILogger<SelfUpdateService> logger, CloudBackupClient? cloudBackups = null)
    {
        _targets = targets.ToList();
        _store = store;
        _cloudBackups = cloudBackups;
        _logger = logger;
        var (target, reason) = Resolve();
        _status = new SelfUpdateStatus(target?.Mode ?? SelfUpdateMode.Unavailable, reason, MatmonVersion.Current, MatmonVersion.Channel,
            null, null, null, store.GetSelfUpdateOutcome(), false, null);
    }

    /// <summary>The status; "installing" ends by itself once a hand-over is older than <see cref="HandOverTimeout"/> - this
    /// process should have been replaced by then, and the page must not stay locked behind an update that never happened.</summary>
    public SelfUpdateStatus Status
    {
        get
        {
            var status = _status;
            if (status.Installing && _handedOverUtc is { } since && DateTimeOffset.UtcNow - since >= HandOverTimeout)
            {
                status = status with { Installing = false };
            }

            return status with { Progress = status.Installing ? _progress : null };
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        try
        {
            await Task.Delay(FirstCheckDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await RunAsync(automatic: true, stoppingToken);
                await Task.Delay(CheckInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>"Check now" - reads the last outcome and asks for an update; never installs.</summary>
    public Task<SelfUpdateStatus> CheckNowAsync(CancellationToken cancellationToken) => RunAsync(automatic: false, cancellationToken);

    /// <summary>Reads how the last update ended right away: the updates page asks for it when it comes back after an update,
    /// which is well before the first hourly look (2 minutes after start).</summary>
    public async Task<SelfUpdateStatus> RefreshOutcomeAsync(CancellationToken cancellationToken)
    {
        // The page never waits behind a download that is running: it shows the status as it is.
        if (!await _gate.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            return Status;
        }

        try
        {
            var (target, _) = Resolve();
            if (target is null)
            {
                return _status;
            }

            var outcome = await ReadOutcomeAsync(target, cancellationToken);
            return _status = _status with { LastOutcome = outcome };
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Whether this installation can update itself at all (a container with the Docker socket, the Windows service).</summary>
    public bool CanSelfUpdate => Resolve().Target is not null;

    /// <summary>
    /// An install asked for in Matmon Cloud (<paramref name="requestedBy"/> says who and in which role). The same path as
    /// "Install now" - backup first, rollback when the new build does not come up - after a fresh check when none ran yet,
    /// and only while this instance's admin lets the cloud do it. Null = handed over, else why not.
    /// </summary>
    public async Task<string?> InstallFromCloudAsync(string requestedBy, CancellationToken cancellationToken)
    {
        if (!_store.GetSelfUpdateSettings().AllowCloudInstall)
        {
            _logger.LogWarning("Update requested from Matmon Cloud by {RequestedBy} - refused: updates from the cloud are switched off", requestedBy);
            return "updates from Matmon Cloud are switched off on this instance (System > Updates)";
        }

        _logger.LogWarning("Update requested from Matmon Cloud by {RequestedBy}", requestedBy);
        if (_status.Offer is not { Available: true })
        {
            await CheckNowAsync(cancellationToken);
        }

        return await InstallNowAsync(cancellationToken);
    }

    /// <summary>"Install now" - null when the hand-over happened (the instance restarts shortly), else why not.</summary>
    public async Task<string?> InstallNowAsync(CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            return "an update is already being installed";
        }

        try
        {
            var (target, reason) = Resolve();
            if (target is null)
            {
                return reason;
            }

            if (_status.Offer is not { Available: true } offer)
            {
                return "there is no update to install - check for updates first";
            }

            // Not the request's token: a browser that gives up (or a connection that drops) during a long download used to
            // cancel the install half-way and leave it marked "installing" for good. The download has its own stall limit.
            return await InstallLockedAsync(target, offer, _stopping);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SelfUpdateStatus> RunAsync(bool automatic, CancellationToken cancellationToken)
    {
        if (!automatic && !await _gate.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            return Status; // "Check now" while an update downloads
        }

        if (automatic)
        {
            await _gate.WaitAsync(cancellationToken);
        }

        try
        {
            var (target, reason) = Resolve();
            if (target is null)
            {
                return _status = _status with { Mode = SelfUpdateMode.Unavailable, UnavailableReason = reason };
            }

            var lastOutcome = await ReadOutcomeAsync(target, cancellationToken);
            SelfUpdateOffer offer;
            try
            {
                offer = await target.CheckAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Update check failed: {Reason}", ex.Message);
                return _status = _status with
                {
                    Mode = target.Mode, UnavailableReason = null, LastCheckUtc = DateTimeOffset.UtcNow, LastError = ex.Message, LastOutcome = lastOutcome
                };
            }

            var settings = _store.GetSelfUpdateSettings();
            var whyNot = SelfUpdatePolicy.WhyNotAutomatic(settings, offer, lastOutcome, DateTimeOffset.UtcNow, DisplayTimeZone.SystemDefault);
            _status = _status with
            {
                Mode = target.Mode,
                UnavailableReason = null,
                LastCheckUtc = DateTimeOffset.UtcNow,
                Offer = offer,
                LastError = null,
                LastOutcome = lastOutcome,
                AutomaticState = offer.Available ? whyNot ?? "installing now" : null
            };

            if (automatic && whyNot is null)
            {
                var problem = await InstallLockedAsync(target, offer, cancellationToken);
                if (problem is not null)
                {
                    _logger.LogWarning("Automatic update to {Version} did not start: {Reason}", offer.Version, problem);
                }
            }

            return _status;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The backup before an install, as System > Backup > Settings says: on this machine (the default), also in Matmon.Cloud,
    /// or none. Sealed with the backup passphrase when one is set. A failed cloud push does not stop the update - the
    /// snapshot on this machine is there - but it is logged.
    /// </summary>
    private async Task BackUpBeforeUpdateAsync(SelfUpdateOffer offer, CancellationToken cancellationToken)
    {
        var mode = _store.GetBackupSettings().BeforeUpdate;
        if (mode == Matmon.Core.Domain.PreUpdateBackupMode.Off)
        {
            _logger.LogInformation("No backup before the update to {Version} (switched off)", offer.Version);
            return;
        }

        var snapshot = _store.CreateBackupBytes(SnapshotSections, $"before the update to {offer.Version}");
        using (var stream = new MemoryStream(snapshot))
        {
            _store.ImportBackupSnapshot(stream, $"pre-update-{offer.Version}.json");
        }

        if (mode == Matmon.Core.Domain.PreUpdateBackupMode.LocalAndCloud)
        {
            if (_cloudBackups is not { IsConnected: true })
            {
                _logger.LogWarning("Backup before the update to {Version}: not connected to Matmon.Cloud - only the snapshot on this machine was made", offer.Version);
                return;
            }
            try
            {
                var encrypted = _store.GetBackupSettings().HasPassphrase;
                var bytes = _store.CreateBackupBytes(Matmon.Core.Domain.WorkspaceBackupSections.CloudConfig, $"before the update to {offer.Version}");
                await _cloudBackups.PushAsync(bytes, $"Before update to {offer.Version}{(encrypted ? " (encrypted)" : "")}", cancellationToken, scheduled: true, encrypted: encrypted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("Backup before the update to {Version}: the cloud push failed ({Reason}) - the snapshot on this machine was made", offer.Version, ex.Message);
            }
        }
    }

    private async Task<string?> InstallLockedAsync(ISelfUpdateTarget target, SelfUpdateOffer offer, CancellationToken cancellationToken)
    {
        if (Status.Installing)
        {
            return "an update is already being installed";
        }

        // The steps the update screen lists: found, backup, what the installation method does, and the restart its
        // updater does once this process has handed over.
        var progress = new ProgressReporter(this,
        [
            new SelfUpdateStep("check", $"Found {offer.Version}"),
            new SelfUpdateStep("backup", "Backing up the configuration"),
            .. target.InstallSteps,
            new SelfUpdateStep("restart", "Restarting with the new version")
        ]);

        try
        {
            _handedOverUtc = null; // downloading: "installing" for as long as that takes
            progress.Step("backup");
            _status = _status with { Installing = true, LastError = null };
            await BackUpBeforeUpdateAsync(offer, cancellationToken);

            _logger.LogInformation("Installing update {Current} -> {Version} ({Mode})", MatmonVersion.Current, offer.Version, target.Mode);
            await target.ApplyAsync(offer, progress, cancellationToken);
            progress.Step("restart");
            _handedOverUtc = DateTimeOffset.UtcNow;
            return null;
        }
        catch (Exception ex)
        {
            // Every failure ends "installing" - a cancelled one too, or the page stays locked until a restart.
            var reason = ex is OperationCanceledException ? "the update was interrupted before it was handed over" : ex.Message;
            _logger.LogWarning("Update to {Version} could not be started: {Reason}", offer.Version, reason);
            var failed = new SelfUpdateOutcome(SelfUpdateOutcomeKind.Failed, MatmonVersion.Current, offer.Version, DateTimeOffset.UtcNow, reason, offer.Identity);
            _store.RecordSelfUpdateOutcome(failed);
            // Not LastError: that is the CHECK's (the page shows it as "the check failed"); the outcome says what went wrong.
            _progress = null;
            _status = _status with { Installing = false, LastOutcome = failed };
            return reason;
        }
    }

    private async Task<SelfUpdateOutcome?> ReadOutcomeAsync(ISelfUpdateTarget target, CancellationToken cancellationToken)
    {
        var stored = _store.GetSelfUpdateOutcome();
        try
        {
            if (await target.ReadLastOutcomeAsync(cancellationToken) is { } fresh && (stored is null || fresh.AtUtc > stored.AtUtc))
            {
                _store.RecordSelfUpdateOutcome(fresh);
                _status = _status with { Installing = false };
                _logger.LogInformation("Last update: {Kind} {From} -> {To}{Detail}", fresh.Kind, fresh.FromVersion, fresh.ToVersion,
                    fresh.Detail is null ? string.Empty : " - " + fresh.Detail);
                return fresh;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Could not read the last update's outcome");
        }

        return stored;
    }

    private (ISelfUpdateTarget? Target, string Reason) Resolve()
    {
        var reasons = new List<string>();
        foreach (var target in _targets)
        {
            if (target.IsAvailable(out var reason))
            {
                return (target, string.Empty);
            }

            reasons.Add(reason);
        }

        return (null, string.Join("; ", reasons));
    }

    /// <summary>Keeps <see cref="Status"/>'s progress current while an install runs; the update screen polls it.</summary>
    private sealed class ProgressReporter(SelfUpdateService service, IReadOnlyList<SelfUpdateStep> steps) : ISelfUpdateProgress
    {
        public void Step(string key)
        {
            // The download's figures stay while its checks run (they say what was downloaded); any later step drops them.
            var keep = key == "verify" ? service._progress?.Download : null;
            service._progress = new SelfUpdateProgress(steps, key, keep);
        }

        public void Download(UpdateDownloadProgress progress)
        {
            var current = service._progress?.Current ?? "download";
            service._progress = new SelfUpdateProgress(steps, current, progress);
        }
    }
}
