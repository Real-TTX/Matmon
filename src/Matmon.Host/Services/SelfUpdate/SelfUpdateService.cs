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
    string? AutomaticState);

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
        Matmon.Core.Domain.WorkspaceBackupSection.All
        & ~Matmon.Core.Domain.WorkspaceBackupSection.SensorHistory
        & ~Matmon.Core.Domain.WorkspaceBackupSection.Events
        & ~Matmon.Core.Domain.WorkspaceBackupSection.Statistics;

    private readonly IReadOnlyList<ISelfUpdateTarget> _targets;
    private readonly IMonitoringWorkspaceStore _store;
    private readonly ILogger<SelfUpdateService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile SelfUpdateStatus _status;
    private DateTimeOffset? _handedOverUtc;

    // A hand-over normally ends this process within seconds. Still running this long after it, the updater gave up
    // before the swap (its outcome says why) - another attempt may go ahead.
    private static readonly TimeSpan HandOverTimeout = TimeSpan.FromMinutes(10);

    public SelfUpdateService(IEnumerable<ISelfUpdateTarget> targets, IMonitoringWorkspaceStore store, ILogger<SelfUpdateService> logger)
    {
        _targets = targets.ToList();
        _store = store;
        _logger = logger;
        var (target, reason) = Resolve();
        _status = new SelfUpdateStatus(target?.Mode ?? SelfUpdateMode.Unavailable, reason, MatmonVersion.Current, MatmonVersion.Channel,
            null, null, null, store.GetSelfUpdateOutcome(), false, null);
    }

    public SelfUpdateStatus Status => _status;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
        await _gate.WaitAsync(cancellationToken);
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

    /// <summary>"Install now" - null when the hand-over happened (the instance restarts shortly), else why not.</summary>
    public async Task<string?> InstallNowAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
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

            return await InstallLockedAsync(target, offer, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SelfUpdateStatus> RunAsync(bool automatic, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
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

    private async Task<string?> InstallLockedAsync(ISelfUpdateTarget target, SelfUpdateOffer offer, CancellationToken cancellationToken)
    {
        if (_status.Installing && _handedOverUtc is { } since && DateTimeOffset.UtcNow - since < HandOverTimeout)
        {
            return "an update is already being installed";
        }

        try
        {
            var snapshot = _store.CreateBackupBytes(SnapshotSections, $"before the update to {offer.Version}");
            using (var stream = new MemoryStream(snapshot))
            {
                _store.ImportBackupSnapshot(stream, $"pre-update-{offer.Version}.json");
            }

            _logger.LogInformation("Installing update {Current} -> {Version} ({Mode})", MatmonVersion.Current, offer.Version, target.Mode);
            _status = _status with { Installing = true, LastError = null };
            _handedOverUtc = DateTimeOffset.UtcNow;
            await target.ApplyAsync(offer, cancellationToken);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Update to {Version} could not be started: {Reason}", offer.Version, ex.Message);
            var failed = new SelfUpdateOutcome(SelfUpdateOutcomeKind.Failed, MatmonVersion.Current, offer.Version, DateTimeOffset.UtcNow, ex.Message, offer.Identity);
            _store.RecordSelfUpdateOutcome(failed);
            // Not LastError: that is the CHECK's (the page shows it as "the check failed"); the outcome says what went wrong.
            _status = _status with { Installing = false, LastOutcome = failed };
            return ex.Message;
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
}
