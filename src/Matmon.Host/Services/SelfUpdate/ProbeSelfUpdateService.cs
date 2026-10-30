using System.Text.Json;
using Matmon.Probe;

namespace Matmon.Host.Services.SelfUpdate;

/// <summary>
/// How the last update of a Docker probe ended, kept in its data folder (a probe has no workspace to keep it in - the
/// agent's last-update.json is the model). Without a data volume it lives as long as the container; the helper container
/// the update leaves behind is read again by the new one anyway.
/// </summary>
public sealed class ProbeUpdateOutcomeFile(string path)
{
    public string Path { get; } = path;

    public SelfUpdateOutcome? Read()
    {
        try
        {
            return File.Exists(Path) ? JsonSerializer.Deserialize<SelfUpdateOutcome>(File.ReadAllText(Path), OutcomeJson.Options) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Keeps <paramref name="outcome"/> when it is newer than what is there; true when it was written.</summary>
    public bool Write(SelfUpdateOutcome outcome)
    {
        if (Read() is { } stored && stored.AtUtc >= outcome.AtUtc)
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var temp = Path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(outcome, OutcomeJson.Options));
            File.Move(temp, Path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>
/// The probe side of "update this Docker probe from its primary" (Secondary mode only). The primary hands an update
/// command out with a heartbeat answer (Probes page > Update); this takes it, answers at once (the answer rides the next
/// heartbeat, which is asked for right away - the container may be replaced before the regular one), pulls the
/// primary's exact version tag (<see cref="ProbeUpdatePolicy"/>, falling back to the newest image of the probe's own
/// tag) and hands over to the same helper container the instance uses (<see cref="DockerSelfUpdateTarget"/>: swap,
/// 3-minute health wait, rollback). Nothing is installed without a command - there is no automatic window on a probe.
/// </summary>
public sealed class ProbeSelfUpdateService : BackgroundService, IProbeSelfUpdate
{
    // When to look for the helper an update left behind: it ends shortly after the new container answers, i.e. after
    // this process started - so a few looks in the first minutes, then hourly.
    private static readonly TimeSpan[] OutcomeLooks = [TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(6)];
    private static readonly TimeSpan OutcomeInterval = TimeSpan.FromHours(1);
    private const int HandledMemory = 20;

    private readonly DockerSelfUpdateTarget _target;
    private readonly ProbeUpdateOutcomeFile _file;
    private readonly SlaveProbeRuntimeState _runtimeState;
    private readonly ILogger<ProbeSelfUpdateService> _logger;
    private readonly string _currentVersion;
    private readonly object _gate = new();
    private readonly Queue<string> _handled = new();
    private SelfUpdateOutcome? _outcome;
    private string? _installingTo;
    private (string Id, bool Accepted, string Message)? _answer;
    private CancellationToken _stopping;

    public ProbeSelfUpdateService(DockerSelfUpdateTarget target, ProbeUpdateOutcomeFile file, SlaveProbeRuntimeState runtimeState,
        ILogger<ProbeSelfUpdateService> logger, string? currentVersion = null)
    {
        _target = target;
        _file = file;
        _runtimeState = runtimeState;
        _logger = logger;
        _currentVersion = currentVersion ?? MatmonVersion.Current;
        _outcome = file.Read();
    }

    /// <summary>What a probe installed by the Windows setup says instead of "no Docker socket": it is updated by running the
    /// newest setup on its machine (which keeps its connection), not from the primary.</summary>
    public const string WindowsServiceReason = "a Windows probe - run the newest Matmon setup on that machine";

    public ProbeSelfUpdateReport Report()
    {
        var canUpdate = _target.IsAvailable(out var reason);
        if (!canUpdate && OperatingSystem.IsWindows() && Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService())
        {
            reason = WindowsServiceReason;
        }

        lock (_gate)
        {
            return new ProbeSelfUpdateReport(
                canUpdate,
                canUpdate ? null : reason,
                _installingTo is not null,
                _installingTo,
                _outcome?.Kind.ToString(),
                _outcome?.FromVersion,
                _outcome?.ToVersion,
                _outcome?.AtUtc,
                _outcome?.Detail,
                _answer?.Id,
                _answer?.Accepted,
                _answer?.Message);
        }
    }

    public void Handle(ProbeUpdateCommand command)
    {
        string? whyNot;
        lock (_gate)
        {
            if (_handled.Contains(command.Id))
            {
                return;
            }

            _handled.Enqueue(command.Id);
            while (_handled.Count > HandledMemory)
            {
                _handled.Dequeue();
            }

            var canUpdate = _target.IsAvailable(out var reason);
            whyNot = ProbeUpdatePolicy.WhyNot(command, _currentVersion, canUpdate, reason, _installingTo is not null);
            _answer = (command.Id, whyNot is null, whyNot ?? $"installing {command.Version}");
            if (whyNot is null)
            {
                _installingTo = command.Version;
            }
        }

        _logger.LogWarning("Update to {Version} requested by the primary ({RequestedBy}){Refusal}", command.Version,
            command.RequestedBy ?? "an admin", whyNot is null ? string.Empty : " - refused: " + whyNot);
        _runtimeState.RequestHeartbeat();
        if (whyNot is null)
        {
            _ = Task.Run(() => InstallAsync(command.Version));
        }
    }

    /// <summary>Pull and hand over. Every failure ends "installing" and is kept as the outcome (the primary shows it).</summary>
    internal async Task InstallAsync(string version)
    {
        try
        {
            var offer = await FindAsync(version, _stopping);
            if (!offer.Available)
            {
                throw new InvalidOperationException(offer.Detail);
            }

            _logger.LogInformation("Installing update {Current} -> {Version} ({Image})", _currentVersion, offer.Version, offer.ImageReference ?? "own tag");
            await _target.ApplyAsync(offer, null, _stopping);
            // From here the helper stops this container; "installing" stays until then (or until the outcome says otherwise).
        }
        catch (Exception ex)
        {
            var reason = ex is OperationCanceledException ? "the update was interrupted before it was handed over" : ex.Message;
            _logger.LogWarning("Update to {Version} could not be started: {Reason}", version, reason);
            Keep(new SelfUpdateOutcome(SelfUpdateOutcomeKind.Failed, _currentVersion, version, DateTimeOffset.UtcNow, reason));
            lock (_gate)
            {
                _installingTo = null;
            }

            _runtimeState.RequestHeartbeat();
        }
    }

    /// <summary>The primary's exact version tag when it is a published build; else - or when the registry has no such
    /// tag - the newest image of the tag this container runs.</summary>
    private async Task<SelfUpdateOffer> FindAsync(string version, CancellationToken cancellationToken)
    {
        if (ProbeUpdatePolicy.IsPublished(version))
        {
            try
            {
                return await _target.CheckVersionAsync(version, cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogInformation("No image tagged {Version} ({Reason}) - looking for a newer image of the probe's own tag", version, ex.Message);
            }
        }

        return await _target.CheckAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        try
        {
            var looks = 0;
            while (!stoppingToken.IsCancellationRequested)
            {
                var wait = looks < OutcomeLooks.Length
                    ? OutcomeLooks[looks] - (looks == 0 ? TimeSpan.Zero : OutcomeLooks[looks - 1])
                    : OutcomeInterval;
                looks++;
                await Task.Delay(wait, stoppingToken);
                await ReadOutcomeAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task ReadOutcomeAsync(CancellationToken cancellationToken)
    {
        if (!_target.IsAvailable(out _))
        {
            return;
        }

        try
        {
            if (await _target.ReadLastOutcomeAsync(cancellationToken) is { } fresh)
            {
                _logger.LogInformation("Last update: {Kind} {From} -> {To}{Detail}", fresh.Kind, fresh.FromVersion, fresh.ToVersion,
                    fresh.Detail is null ? string.Empty : " - " + fresh.Detail);
                Keep(fresh);
                lock (_gate)
                {
                    _installingTo = null;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Could not read the last update's outcome");
        }
    }

    private void Keep(SelfUpdateOutcome outcome)
    {
        _file.Write(outcome);
        lock (_gate)
        {
            if (_outcome is null || outcome.AtUtc > _outcome.AtUtc)
            {
                _outcome = outcome;
            }
        }
    }
}
