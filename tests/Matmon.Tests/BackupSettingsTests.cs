using System.Text;
using Matmon.Core.Domain;
using Matmon.Core.Telemetry;
using Matmon.Host.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

/// <summary>System > Backup > Settings: the instance's backup passphrase seals every package by default (and opens this
/// instance's own packages without being typed), it never travels in a package, and a restore keeps the probes connected.</summary>
public sealed class BackupSettingsTests : IDisposable
{
    private const string Passphrase = "correct horse battery staple";
    private const string Secret = "s3cret-in-every-backup";

    private readonly string _dir;

    public BackupSettingsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "matmon-backupsettings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    // A fresh store == a distinct instance: its own workspace, telemetry and DataProtection key ring.
    private T Run<T>(string tag, Func<InMemoryMonitoringWorkspaceStore, T> body)
    {
        using var telemetry = new SqliteTelemetryRepository(Path.Combine(_dir, $"telemetry-{tag}.db"));
        using var store = new InMemoryMonitoringWorkspaceStore(
            new BackupSettingsTestHostEnvironment(_dir),
            new MatmonRuntimeOptions { WorkspacePath = Path.Combine(_dir, tag, "workspace.json") },
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

    private static string? RestoredSecret(InMemoryMonitoringWorkspaceStore store) =>
        store.GetAllElements().OfType<ProbeElement>().First(probe => probe.ParentId is null)
            .Settings.Credentials.FirstOrDefault(c => c.Name == "SSH box")?.Values.GetValueOrDefault("generic.password");

    [Fact]
    public void The_backup_passphrase_seals_a_package_by_default_and_never_travels_in_it()
    {
        var blob = Run("a", store =>
        {
            SeedCredential(store);
            store.SetBackupPassphrase(Passphrase);
            Assert.True(store.GetBackupSettings().HasPassphrase);
            return store.CreateBackupBytes(WorkspaceBackupSection.All, "test");
        });

        var json = Encoding.UTF8.GetString(blob);
        Assert.Contains("\"secretsPortable\":true", json);
        Assert.DoesNotContain(Passphrase, json);
        Assert.DoesNotContain(Secret, json);

        // Another instance: the credential comes back with the passphrase, and the passphrase setting does not.
        var (secret, hasPassphrase) = Run("b", store =>
        {
            store.RestoreBackupBytes(blob, WorkspaceBackupSection.Topology, Passphrase);
            return (RestoredSecret(store), store.GetBackupSettings().HasPassphrase);
        });
        Assert.Equal(Secret, secret);
        Assert.False(hasPassphrase);
    }

    [Fact]
    public void An_instance_restores_its_own_sealed_backup_without_the_passphrase_being_typed()
    {
        var secret = Run("a", store =>
        {
            SeedCredential(store);
            store.SetBackupPassphrase(Passphrase);
            var blob = store.CreateBackupBytes(WorkspaceBackupSection.Topology, "test");
            store.RestoreBackupBytes(blob, WorkspaceBackupSection.Topology);
            return RestoredSecret(store);
        });
        Assert.Equal(Secret, secret);
    }

    [Fact]
    public void A_local_job_snapshot_is_sealed_too_and_restores_on_the_same_instance()
    {
        var (portable, secret) = Run("a", store =>
        {
            SeedCredential(store);
            store.SetBackupPassphrase(Passphrase);
            var job = store.CreateBackupJob(new WorkspaceBackupJob { Name = "Nightly", Sections = WorkspaceBackupSection.Topology });
            var snapshot = store.RunBackupJob(job.Id);
            var text = File.ReadAllText(Directory.GetFiles(_dir, snapshot.FileName, SearchOption.AllDirectories).Single());
            store.RestoreBackupSnapshot(snapshot.FileName, WorkspaceBackupSection.Topology);
            return (System.Text.RegularExpressions.Regex.IsMatch(text, @"""secretsPortable"":\s*true"), RestoredSecret(store));
        });
        Assert.True(portable);
        Assert.Equal(Secret, secret);
    }

    [Fact]
    public void A_wrong_typed_passphrase_is_still_refused()
    {
        Run("a", store =>
        {
            store.SetBackupPassphrase(Passphrase);
            var blob = store.CreateBackupBytes(WorkspaceBackupSection.Topology, "test");
            Assert.Throws<InvalidOperationException>(() => store.RestoreBackupBytes(blob, WorkspaceBackupSection.Topology, "not the right one at all"));
            return 0;
        });
    }

    [Fact]
    public void The_passphrase_needs_twelve_characters_and_can_be_removed()
    {
        Run("a", store =>
        {
            Assert.Throws<ArgumentException>(() => store.SetBackupPassphrase("short"));
            store.SetBackupPassphrase(Passphrase);
            store.SetBackupPassphrase(null);
            Assert.False(store.GetBackupSettings().HasPassphrase);
            Assert.Null(store.GetBackupPassphrase());
            Assert.DoesNotContain("\"secretsPortable\":true", Encoding.UTF8.GetString(store.CreateBackupBytes(WorkspaceBackupSection.Topology)));
            return 0;
        });
    }

    [Fact]
    public void The_backup_before_an_update_defaults_to_this_machine_and_can_be_changed()
    {
        Run("a", store =>
        {
            Assert.Equal(PreUpdateBackupMode.Local, store.GetBackupSettings().BeforeUpdate);
            store.SetPreUpdateBackup(PreUpdateBackupMode.LocalAndCloud);
            Assert.Equal(PreUpdateBackupMode.LocalAndCloud, store.GetBackupSettings().BeforeUpdate);
            store.SetPreUpdateBackup(PreUpdateBackupMode.Off);
            Assert.Equal(PreUpdateBackupMode.Off, store.GetBackupSettings().BeforeUpdate);
            return 0;
        });
    }

    [Fact]
    public void A_restore_keeps_the_token_a_remote_probe_uses_today()
    {
        Run("a", store =>
        {
            var root = store.GetAllElements().OfType<ProbeElement>().First(probe => probe.ParentId is null);
            var probe = store.CreateProbe(root.Id, "Branch office", null);
            var oldToken = store.RotateProbeToken(probe.Id);
            var blob = store.CreateBackupBytes(WorkspaceBackupSection.Topology, "before the rotation");

            // The token is rotated after the backup was made - the probe out there now uses the new one.
            var newToken = store.RotateProbeToken(probe.Id);
            Assert.NotEqual(oldToken, newToken);

            store.RestoreBackupBytes(blob, WorkspaceBackupSection.Topology);

            Assert.True(store.TryValidateProbe(probe.ProbeId, newToken));
            Assert.False(store.TryValidateProbe(probe.ProbeId, oldToken));
            return 0;
        });
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}

file sealed class BackupSettingsTestHostEnvironment : IHostEnvironment
{
    public BackupSettingsTestHostEnvironment(string contentRoot)
    {
        ContentRootPath = contentRoot;
        ContentRootFileProvider = new NullFileProvider();
    }

    public string ApplicationName { get; set; } = "Matmon.Tests";
    public string EnvironmentName { get; set; } = "Development";
    public string ContentRootPath { get; set; }
    public IFileProvider ContentRootFileProvider { get; set; }
}

/// <summary>Every instance backs itself up without anyone thinking of it: a nightly job here, and a nightly cloud job once linked.</summary>
public sealed class DefaultBackupJobTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "matmon-defaultjobs-" + Guid.NewGuid().ToString("N"));

    private InMemoryMonitoringWorkspaceStore Open(SqliteTelemetryRepository telemetry) => new(
        new BackupSettingsTestHostEnvironment(_dir),
        new MatmonRuntimeOptions { WorkspacePath = Path.Combine(_dir, "workspace.json") },
        new MatmonAuthOptions(),
        new EphemeralDataProtectionProvider(),
        telemetry,
        NullLogger<InMemoryMonitoringWorkspaceStore>.Instance);

    private static void Heartbeat(InMemoryMonitoringWorkspaceStore store, string status = "ok") =>
        store.UpdateCloudConnection(new CloudConnectionState { InstanceId = Guid.NewGuid(), CloudUrl = "https://cloud.example.test", LastStatus = status });

    [Fact]
    public void A_new_instance_has_a_nightly_local_backup_and_gets_a_cloud_one_on_its_first_good_heartbeat()
    {
        Directory.CreateDirectory(_dir);
        using var telemetry = new SqliteTelemetryRepository(Path.Combine(_dir, "t.db"));
        using var store = Open(telemetry);

        var local = Assert.Single(store.GetBackupJobs());
        Assert.Equal(BackupDestination.Local, local.Destination);
        Assert.Equal(MonitoringScheduleMode.Daily, local.Schedule.Mode);
        Assert.NotNull(local.NextRunUtc);

        Heartbeat(store, "unauthorized");
        Assert.DoesNotContain(store.GetBackupJobs(), job => job.Destination == BackupDestination.Cloud);

        Heartbeat(store);
        var cloud = Assert.Single(store.GetBackupJobs(), job => job.Destination == BackupDestination.Cloud);
        Assert.True(cloud.Enabled);
        Assert.True(store.HasNightlyCloudBackup());
    }

    [Fact]
    public void A_removed_default_job_does_not_come_back()
    {
        Directory.CreateDirectory(_dir);
        using var telemetry = new SqliteTelemetryRepository(Path.Combine(_dir, "t.db"));
        using (var store = Open(telemetry))
        {
            Heartbeat(store);
            foreach (var job in store.GetBackupJobs())
            {
                store.DeleteBackupJob(job.Id);
            }
            Heartbeat(store);
            Assert.Empty(store.GetBackupJobs());
        }
        using (var reopened = Open(telemetry))
        {
            Assert.Empty(reopened.GetBackupJobs());
        }
    }

    [Fact]
    public void The_assistant_switches_the_nightly_cloud_backup_off_and_on()
    {
        Directory.CreateDirectory(_dir);
        using var telemetry = new SqliteTelemetryRepository(Path.Combine(_dir, "t.db"));
        using var store = Open(telemetry);
        Heartbeat(store);
        store.SetNightlyCloudBackup(false);
        Assert.False(store.HasNightlyCloudBackup());
        store.SetNightlyCloudBackup(true);
        Assert.True(store.HasNightlyCloudBackup());
        Assert.Single(store.GetBackupJobs(), job => job.Destination == BackupDestination.Cloud);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
