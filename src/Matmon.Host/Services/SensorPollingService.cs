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

        var dueCount = 0;

        using var scope = _scopeFactory.CreateScope();
        var executionService = scope.ServiceProvider.GetRequiredService<ISensorExecutionService>();
        var now = DateTimeOffset.UtcNow;

        foreach (var sensor in sensors)
        {
            stoppingToken.ThrowIfCancellationRequested();

            if (!IsLocalSensor(sensor, elementsById))
            {
                continue;
            }

            if (sensor.IsPaused)
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
            if (!MonitoringScheduleCalculator.IsDue(effectiveSettings, latestRunUtc, now, TimeSpan.FromSeconds(15)))
            {
                continue;
            }

            dueCount++;

            try
            {
                var result = await executionService.ExecuteNowAsync(sensor.Id, cancellationToken: stoppingToken);
                _logger.LogInformation(
                    "Polled sensor {SensorName} -> {State} ({Message})",
                    sensor.Name,
                    result.State,
                    result.Message ?? "ok");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Scheduled execution for sensor {SensorName} failed", sensor.Name);
            }
        }

        if (dueCount > 0)
        {
            _logger.LogInformation("Polled {Count} sensors", dueCount);
        }
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
