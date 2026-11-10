using Matmon.Core.Domain;
using Matmon.Core.Telemetry;
using Matmon.Host.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

/// <summary>Restore reports what it did: item counts and any credential that could not be decrypted (dropped), and
/// the create path refuses a too-short portable passphrase.</summary>
public sealed class BackupRestoreResultTests : IDisposable
{
    private const string Secret = "s3cret-across-instances";
    private readonly string _dir;

    public BackupRestoreResultTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "matmon-restore-result-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    private T Run<T>(string tag, Func<InMemoryMonitoringWorkspaceStore, T> body)
    {
        using var telemetry = new SqliteTelemetryRepository(Path.Combine(_dir, $"telemetry-{tag}.db"));
        using var store = new InMemoryMonitoringWorkspaceStore(
            new RestoreResultTestHostEnvironment(_dir),
            new MatmonRuntimeOptions { WorkspacePath = Path.Combine(_dir, $"workspace-{tag}.json") },
            new MatmonAuthOptions(),
            new EphemeralDataProtectionProvider(),
            telemetry,
            NullLogger<InMemoryMonitoringWorkspaceStore>.Instance);
        return body(store);
    }

    private static void SeedCredential(InMemoryMonitoringWorkspaceStore store)
    {
        var rootId = store.GetAllElements().OfType<ProbeElement>().First().Id;
        store.UpdateElement(rootId, element => element.Settings.Credentials.Add(new MonitoringCredentialBundle
        {
            Name = "SSH box",
            Kind = MonitoringCredentialKind.Generic,
            Values = { ["generic.password"] = Secret },
        }));
    }

    [Fact]
    public void Short_passphrase_is_rejected_on_create()
    {
        Run("a", store =>
        {
            var ex = Assert.Throws<ArgumentException>(
                () => store.CreateBackupBytes(WorkspaceBackupSection.Topology, "test", "short"));
            Assert.Contains("at least", ex.Message);
            return 0;
        });
    }

    [Fact]
    public void Restore_result_reports_item_counts()
    {
        // Create + restore in the SAME store (one DP ring) = a genuine same-instance restore.
        Run("a", store =>
        {
            SeedCredential(store);
            var blob = store.CreateBackupBytes(WorkspaceBackupSection.Topology, "test");
            var result = store.RestoreBackupBytes(blob, WorkspaceBackupSection.Topology);
            Assert.True(result.Success);
            Assert.True(result.Probes >= 1, $"expected >=1 probe, got {result.Probes}");
            Assert.Empty(result.DroppedSecretsOrEmpty); // same instance -> credential decrypts fine
            Assert.Contains("probe", result.Message);
            return 0;
        });
    }

    [Fact]
    public void Cross_instance_non_portable_restore_reports_the_dropped_credential()
    {
        // Non-portable backup on instance A (instance-bound DP ciphertext).
        var blob = Run("a", store =>
        {
            SeedCredential(store);
            return store.CreateBackupBytes(WorkspaceBackupSection.Topology, "test");
        });

        // Restoring on instance B (different DP ring) can't decrypt the credential -> it is dropped and reported.
        Run("b", store =>
        {
            var result = store.RestoreBackupBytes(blob, WorkspaceBackupSection.Topology);
            Assert.NotEmpty(result.DroppedSecretsOrEmpty);
            Assert.Contains(result.DroppedSecretsOrEmpty, name => name.Contains("SSH box"));
            Assert.Contains("could not be decrypted", result.Message);
            return 0;
        });
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}

file sealed class RestoreResultTestHostEnvironment : IHostEnvironment
{
    public RestoreResultTestHostEnvironment(string contentRoot)
    {
        ContentRootPath = contentRoot;
        ContentRootFileProvider = new NullFileProvider();
    }

    public string ApplicationName { get; set; } = "Matmon.Tests";
    public string EnvironmentName { get; set; } = "Development";
    public string ContentRootPath { get; set; }
    public IFileProvider ContentRootFileProvider { get; set; }
}

/// <summary>A backup may carry the history (sensor results, events, statistics) - also to Matmon.Cloud, compressed - and a
/// restore puts back only what a package carries: restoring "everything" from a configuration-only backup used to apply
/// the missing history sections as EMPTY and wipe the sensor history.</summary>
public sealed class BackupHistoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "matmon-backup-history-" + Guid.NewGuid().ToString("N"));

    public BackupHistoryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private T Run<T>(string tag, Func<InMemoryMonitoringWorkspaceStore, T> body)
    {
        using var telemetry = new SqliteTelemetryRepository(Path.Combine(_dir, $"telemetry-{tag}.db"));
        using var store = new InMemoryMonitoringWorkspaceStore(
            new RestoreResultTestHostEnvironment(_dir),
            new MatmonRuntimeOptions { WorkspacePath = Path.Combine(_dir, $"workspace-{tag}.json") },
            new MatmonAuthOptions(),
            new EphemeralDataProtectionProvider(),
            telemetry,
            NullLogger<InMemoryMonitoringWorkspaceStore>.Instance);
        return body(store);
    }

    [Fact]
    public void The_history_travels_compressed_and_a_configuration_backup_leaves_it_alone()
    {
        var sensorId = Guid.NewGuid();
        var (withHistory, configOnly) = Run("source", store =>
        {
            var start = DateTimeOffset.UtcNow.AddHours(-1);
            for (var i = 0; i < 50; i++)
            {
                store.RecordSensorObservation(sensorId, SensorExecutionResult.Healthy(TimeSpan.FromMilliseconds(12), "fine"), start.AddMinutes(i));
            }

            var sections = WorkspaceBackupSections.CloudConfig | WorkspaceBackupSections.History;
            var compressed = store.CreateBackupBytes(sections, "test", compress: true);
            var plain = store.CreateBackupBytes(sections, "test");
            Assert.True(BackupPayload.IsCompressed(compressed));
            Assert.True(compressed.Length * 3 < plain.Length);
            return (compressed, store.CreateBackupBytes(WorkspaceBackupSections.CloudConfig, "test"));
        });

        Run("target", store =>
        {
            store.RestoreBackupBytes(withHistory, WorkspaceBackupSections.CloudRestore);
            Assert.Equal(50, store.GetSensorHistory(sensorId).Count);

            // A configuration-only backup restored with "everything" keeps the history it does not carry.
            store.RestoreBackupBytes(configOnly, WorkspaceBackupSections.CloudRestore);
            Assert.Equal(50, store.GetSensorHistory(sensorId).Count);
            return 0;
        });
    }

    [Fact]
    public void A_cloud_job_never_carries_the_local_users()
    {
        Assert.Equal(WorkspaceBackupSections.CloudConfig, WorkspaceBackupSections.ForCloud(WorkspaceBackupSection.None));
        Assert.False(WorkspaceBackupSections.ForCloud(WorkspaceBackupSection.All).HasFlag(WorkspaceBackupSection.Users));
        Assert.True(WorkspaceBackupSections.ForCloud(WorkspaceBackupSection.All).HasFlag(WorkspaceBackupSection.SensorHistory));
    }
}
