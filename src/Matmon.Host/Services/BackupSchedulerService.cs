using Matmon.Core;
using Matmon.Core.Domain;

namespace Matmon.Host.Services;

public sealed class BackupSchedulerService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly IMonitoringWorkspaceStore _workspaceStore;
    private readonly MatmonRuntimeOptions _runtimeOptions;
    private readonly ILogger<BackupSchedulerService> _logger;

    public BackupSchedulerService(
        IMonitoringWorkspaceStore workspaceStore,
        MatmonRuntimeOptions runtimeOptions,
        ILogger<BackupSchedulerService> logger)
    {
        _workspaceStore = workspaceStore;
        _runtimeOptions = runtimeOptions;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_runtimeOptions.Mode != AppMode.Master)
        {
            return;
        }

        await RunOnceAsync(stoppingToken);

        using var timer = new PeriodicTimer(PollInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunOnceAsync(stoppingToken);
        }
    }

    private Task RunOnceAsync(CancellationToken stoppingToken)
    {
        var now = DateTimeOffset.UtcNow;
        var jobs = _workspaceStore.GetBackupJobs()
            .Where(job => job.Enabled && (job.NextRunUtc is null || job.NextRunUtc <= now))
            .OrderBy(job => job.NextRunUtc ?? DateTimeOffset.MinValue)
            .ToArray();

        foreach (var job in jobs)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                var snapshot = _workspaceStore.RunBackupJob(job.Id, "Scheduled backup.");
                _logger.LogInformation(
                    "Backup job {BackupJobName} created snapshot {SnapshotFile}",
                    job.Name,
                    snapshot.FileName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Scheduled backup job {BackupJobName} failed", job.Name);
            }
        }

        return Task.CompletedTask;
    }
}
