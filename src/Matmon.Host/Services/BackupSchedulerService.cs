using Matmon.Core;
using Matmon.Core.Domain;

namespace Matmon.Host.Services;

public sealed class BackupSchedulerService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly IMonitoringWorkspaceStore _workspaceStore;
    private readonly MatmonRuntimeOptions _runtimeOptions;
    private readonly CloudBackupClient _cloudBackups;
    private readonly ILogger<BackupSchedulerService> _logger;

    public BackupSchedulerService(
        IMonitoringWorkspaceStore workspaceStore,
        MatmonRuntimeOptions runtimeOptions,
        CloudBackupClient cloudBackups,
        ILogger<BackupSchedulerService> logger)
    {
        _workspaceStore = workspaceStore;
        _runtimeOptions = runtimeOptions;
        _cloudBackups = cloudBackups;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_runtimeOptions.Mode != AppMode.Primary)
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

    private async Task RunOnceAsync(CancellationToken stoppingToken)
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

            if (job.Destination == BackupDestination.Cloud)
            {
                await RunCloudJobAsync(job, stoppingToken);
                continue;
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
    }

    // A Cloud-destination job pushes the config-only snapshot to Matmon.Cloud (the store does no networking, so
    // the HTTP lives here) and records the run + advances the schedule via the store. Cloud enforces its own
    // newest-N retention, so RetentionCount is not applied here.
    private async Task RunCloudJobAsync(WorkspaceBackupJob job, CancellationToken stoppingToken)
    {
        if (!_cloudBackups.IsConnected)
        {
            _workspaceStore.RecordCloudBackupJobRun(job.Id, success: false, "Not connected to Matmon.Cloud.", null);
            _logger.LogWarning("Cloud backup job {BackupJobName} skipped: not connected to Matmon.Cloud", job.Name);
            return;
        }

        try
        {
            var bytes = _workspaceStore.CreateBackupBytes(WorkspaceBackupSections.CloudConfig, "Scheduled cloud backup.");
            var label = $"{job.Name} {DateTimeOffset.Now:yyyy-MM-dd HH:mm}";
            await _cloudBackups.PushAsync(bytes, label, stoppingToken);
            _workspaceStore.RecordCloudBackupJobRun(job.Id, success: true, "Backed up to Matmon.Cloud.", bytes.LongLength);
            _logger.LogInformation("Cloud backup job {BackupJobName} pushed {Bytes} bytes to Matmon.Cloud", job.Name, bytes.LongLength);
        }
        catch (Exception ex)
        {
            _workspaceStore.RecordCloudBackupJobRun(job.Id, success: false, ex.Message, null);
            _logger.LogWarning(ex, "Cloud backup job {BackupJobName} failed", job.Name);
        }
    }
}
