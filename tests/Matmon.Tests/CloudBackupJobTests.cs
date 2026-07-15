using Matmon.Core.Domain;
using Matmon.Core.Telemetry;
using Matmon.Host.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

/// <summary>Cloud-destination backup jobs: the domain flag round-trips, the shared config-only mask is correct,
/// and recording a cloud run advances the schedule + stamps status without writing a local snapshot.</summary>
public sealed class CloudBackupJobTests : IDisposable
{
    private readonly string _dir;

    public CloudBackupJobTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "matmon-cloudjob-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public void Clone_copies_the_destination()
    {
        var job = new WorkspaceBackupJob { Name = "Nightly", Destination = BackupDestination.Cloud };
        Assert.Equal(BackupDestination.Cloud, job.Clone().Destination);
    }

    [Fact]
    public void CloudConfig_mask_excludes_users_and_telemetry()
    {
        var mask = WorkspaceBackupSections.CloudConfig;

        // Config travels; the bulky telemetry + local accounts must not (Users out so a DR restore can't lock out
        // the admin, telemetry out so the cloud blob stays small).
        Assert.True(mask.HasFlag(WorkspaceBackupSection.Topology));
        Assert.True(mask.HasFlag(WorkspaceBackupSection.Notifications));
        Assert.False(mask.HasFlag(WorkspaceBackupSection.Users));
        Assert.False(mask.HasFlag(WorkspaceBackupSection.SensorHistory));
        Assert.False(mask.HasFlag(WorkspaceBackupSection.Events));
        Assert.False(mask.HasFlag(WorkspaceBackupSection.Statistics));
    }

    [Fact]
    public void RecordCloudBackupJobRun_success_advances_schedule_and_stamps_ok()
    {
        Run(store =>
        {
            var job = store.CreateBackupJob(new WorkspaceBackupJob
            {
                Name = "Cloud nightly",
                Destination = BackupDestination.Cloud,
                Schedule = new MonitoringSchedule { Mode = MonitoringScheduleMode.Daily, TimeOfDay = TimeSpan.FromHours(2) }
            });

            store.RecordCloudBackupJobRun(job.Id, success: true, "Backed up to Matmon.Cloud.", 4096);

            var saved = store.FindBackupJob(job.Id)!;
            Assert.Equal("ok", saved.LastStatus);
            Assert.NotNull(saved.LastRunUtc);
            Assert.NotNull(saved.NextRunUtc);
            Assert.True(saved.NextRunUtc > saved.LastRunUtc);
            Assert.Equal(4096, saved.LastSnapshotBytes);
            Assert.Null(saved.LastSnapshotFileName); // no local artifact for a cloud push
        });
    }

    [Fact]
    public void RecordCloudBackupJobRun_failure_stamps_error_and_keeps_no_bytes()
    {
        Run(store =>
        {
            var job = store.CreateBackupJob(new WorkspaceBackupJob
            {
                Name = "Cloud nightly",
                Destination = BackupDestination.Cloud,
                Schedule = new MonitoringSchedule { Mode = MonitoringScheduleMode.Daily, TimeOfDay = TimeSpan.FromHours(2) }
            });

            store.RecordCloudBackupJobRun(job.Id, success: false, "Not connected to Matmon.Cloud.", null);

            var saved = store.FindBackupJob(job.Id)!;
            Assert.Equal("error", saved.LastStatus);
            Assert.Equal("Not connected to Matmon.Cloud.", saved.LastMessage);
            Assert.NotNull(saved.NextRunUtc); // still rescheduled so it retries next slot
            Assert.Null(saved.LastSnapshotBytes);
        });
    }

    private void Run(Action<InMemoryMonitoringWorkspaceStore> body)
    {
        using var telemetry = new SqliteTelemetryRepository(Path.Combine(_dir, $"t-{Guid.NewGuid():N}.db"));
        using var store = new InMemoryMonitoringWorkspaceStore(
            new CloudJobTestHostEnvironment(_dir),
            new MatmonRuntimeOptions { WorkspacePath = Path.Combine(_dir, $"ws-{Guid.NewGuid():N}.json") },
            new MatmonAuthOptions(),
            new EphemeralDataProtectionProvider(),
            telemetry,
            NullLogger<InMemoryMonitoringWorkspaceStore>.Instance);
        body(store);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}

file sealed class CloudJobTestHostEnvironment : IHostEnvironment
{
    public CloudJobTestHostEnvironment(string contentRoot)
    {
        ContentRootPath = contentRoot;
        ContentRootFileProvider = new NullFileProvider();
    }

    public string ApplicationName { get; set; } = "Matmon.Tests";
    public string EnvironmentName { get; set; } = "Development";
    public string ContentRootPath { get; set; }
    public IFileProvider ContentRootFileProvider { get; set; }
}
