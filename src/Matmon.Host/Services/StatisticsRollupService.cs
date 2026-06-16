using Matmon.Core;

namespace Matmon.Host.Services;

/// <summary>
/// Periodically downsamples raw observations into accurate statistics buckets
/// (avg/min/max/percentiles + uptime) and applies telemetry retention. Running
/// this out-of-band keeps the polling hot path light and lets percentiles be
/// computed from the real samples. Primary-only.
/// </summary>
public sealed class StatisticsRollupService : BackgroundService
{
    private static readonly TimeSpan RollupInterval = TimeSpan.FromMinutes(5);

    private readonly IMonitoringWorkspaceStore _workspaceStore;
    private readonly MatmonRuntimeOptions _runtimeOptions;
    private readonly ILogger<StatisticsRollupService> _logger;

    public StatisticsRollupService(
        IMonitoringWorkspaceStore workspaceStore,
        MatmonRuntimeOptions runtimeOptions,
        ILogger<StatisticsRollupService> logger)
    {
        _workspaceStore = workspaceStore;
        _runtimeOptions = runtimeOptions;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_runtimeOptions.Mode != AppMode.Primary)
        {
            return;
        }

        RunOnce();

        using var timer = new PeriodicTimer(RollupInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            RunOnce();
        }
    }

    private void RunOnce()
    {
        try
        {
            var result = _workspaceStore.RunTelemetryMaintenance(DateTimeOffset.UtcNow);
            if (result.BucketsWritten > 0 || result.ObservationsPruned > 0 ||
                result.StatisticsPruned > 0 || result.EventsPruned > 0)
            {
                _logger.LogDebug(
                    "Telemetry rollup: {Sensors} sensors, {Buckets} buckets, pruned {Observations} obs / {Statistics} stats / {Events} events",
                    result.Sensors,
                    result.BucketsWritten,
                    result.ObservationsPruned,
                    result.StatisticsPruned,
                    result.EventsPruned);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Telemetry rollup pass failed");
        }
    }
}
