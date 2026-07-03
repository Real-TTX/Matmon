using Matmon.Core;
using Matmon.Core.Domain;

namespace Matmon.Host.Services;

public sealed class SensorPollingService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMonitoringWorkspaceStore _workspaceStore;
    private readonly ILogger<SensorPollingService> _logger;
    private readonly MonitoringInheritanceResolver _resolver = new();
    private readonly MatmonRuntimeOptions _runtimeOptions;

    public SensorPollingService(
        IServiceScopeFactory scopeFactory,
        IMonitoringWorkspaceStore workspaceStore,
        ILogger<SensorPollingService> logger,
        MatmonRuntimeOptions runtimeOptions)
    {
        _scopeFactory = scopeFactory;
        _workspaceStore = workspaceStore;
        _logger = logger;
        _runtimeOptions = runtimeOptions;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_runtimeOptions.Mode != AppMode.Primary)
        {
            return;
        }

        var pollInterval = TimeSpan.FromSeconds(5);
        _logger.LogInformation("Sensor polling service started with {Interval}", pollInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(pollInterval, stoppingToken);
                await PollDueSensorsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Scheduled sensor polling failed");
            }
        }
    }

    private async Task PollDueSensorsAsync(CancellationToken stoppingToken)
    {
        var snapshot = _workspaceStore.Workspace;
        var elementsById = _workspaceStore.GetAllElements().ToDictionary(element => element.Id);
        var templateMap = snapshot.Templates.ToDictionary(template => template.Id);
        var latestBySensor = _workspaceStore.GetLatestSensorObservations();

        var sensors = elementsById.Values.OfType<SensorElement>().ToArray();
        if (sensors.Length == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;

        // First pass (no I/O): work out which sensors are due and how overdue each is.
        var due = new List<(SensorElement Sensor, double OverdueSeconds)>();
        foreach (var sensor in sensors)
        {
            stoppingToken.ThrowIfCancellationRequested();

            if (!IsLocalSensor(sensor, elementsById) || sensor.IsPaused)
            {
                continue;
            }

            var lineage = BuildLineage(sensor, elementsById);
            var effectiveSettings = _resolver.Resolve(lineage, templateMap);
            if (effectiveSettings.Enabled == false)
            {
                continue;
            }

            var latestRunUtc = latestBySensor.TryGetValue(sensor.Id, out var latest)
                ? latest.TimestampUtc
                : (DateTimeOffset?)null;
            var fallbackInterval = SensorScheduleDefaults.Resolve(sensor.SensorTypeKey);
            if (!MonitoringScheduleCalculator.IsDue(effectiveSettings, latestRunUtc, now, fallbackInterval))
            {
                continue;
            }

            // Never-run sensors, then the longest-overdue ones, go first — so a big catch-up (after
            // downtime, or resuming a paused sensor/folder) fills the biggest gaps first.
            var overdueSeconds = latestRunUtc is DateTimeOffset last
                ? (now - last).TotalSeconds
                : double.MaxValue;
            due.Add((sensor, overdueSeconds));
        }

        if (due.Count == 0)
        {
            return;
        }

        due.Sort((left, right) => right.OverdueSeconds.CompareTo(left.OverdueSeconds));

        // Second pass: run the due sensors concurrently with a bounded worker pool, so one slow or
        // timing-out sensor no longer blocks the rest of the cycle. Each execution gets its own DI
        // scope; the store serializes the actual observation writes on its own gate.
        // Clamp to a sane range so a misconfigured value can't spawn thousands of concurrent scopes/sockets.
        var workers = Math.Clamp(_runtimeOptions.PollingWorkers, 1, 256);
        await Parallel.ForEachAsync(
            due,
            new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = stoppingToken },
            async (item, cancellationToken) =>
            {
                using var scope = _scopeFactory.CreateScope();
                var executionService = scope.ServiceProvider.GetRequiredService<ISensorExecutionService>();
                try
                {
                    var result = await executionService.ExecuteNowAsync(item.Sensor.Id, cancellationToken: cancellationToken);
                    _logger.LogInformation(
                        "Polled sensor {SensorName} -> {State} ({Message})",
                        item.Sensor.Name,
                        result.State,
                        result.Message ?? "ok");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // shutting down — ignore
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Scheduled execution for sensor {SensorName} failed", item.Sensor.Name);
                }
            });

        _logger.LogInformation("Polled {Count} sensors ({Workers} workers)", due.Count, workers);
    }

    private static IReadOnlyList<MonitoringElement> BuildLineage(
        MonitoringElement element,
        IReadOnlyDictionary<Guid, MonitoringElement> elementsById)
    {
        var lineage = new List<MonitoringElement>();
        var current = element;

        while (true)
        {
            lineage.Add(current);

            if (current.ParentId is not Guid parentId)
            {
                break;
            }

            if (!elementsById.TryGetValue(parentId, out var parent))
            {
                break;
            }

            current = parent;
        }

        lineage.Reverse();
        return lineage;
    }

    private static bool IsLocalSensor(
        MonitoringElement element,
        IReadOnlyDictionary<Guid, MonitoringElement> elementsById)
    {
        if (element is SensorElement sensor &&
            string.Equals(sensor.SensorTypeKey, ProbeHeartbeatSensorExecutor.Definition.Key, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var lineage = BuildLineage(element, elementsById);
        var probeAncestor = lineage.OfType<ProbeElement>().LastOrDefault();
        return probeAncestor is not null && probeAncestor.ParentId is null;
    }
}
