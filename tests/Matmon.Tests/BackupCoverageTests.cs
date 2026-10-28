using System.Reflection;
using Matmon.Core.Domain;
using Matmon.Core.SnmpTraps;
using Matmon.Core.Syslog;
using Matmon.Core.Telemetry;
using Matmon.Host.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

/// <summary>
/// Everything in the workspace is either carried by a backup section or deliberately left on the machine - and what a
/// section carries comes back on restore, on another instance too (collectors, trap users, time zone, the syslog log).
/// </summary>
public sealed class BackupCoverageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "matmon-backupcoverage-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _owned = [];
    private readonly EphemeralDataProtectionProvider _protection = new();

    public BackupCoverageTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var owned in Enumerable.Reverse(_owned))
        {
            owned.Dispose();
        }

        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Every_part_of_the_workspace_has_a_section_or_a_reason_to_stay()
    {
        var document = typeof(InMemoryMonitoringWorkspaceStore).GetNestedType("WorkspaceDocument", BindingFlags.NonPublic)!;
        var properties = document.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(property => property.Name).ToHashSet();
        var coverage = InMemoryMonitoringWorkspaceStore.BackupCoverage;

        var undecided = properties.Except(coverage.Keys).ToArray();
        Assert.True(undecided.Length == 0, $"Not in BackupCoverage (say which section carries it, or why it stays): {string.Join(", ", undecided)}");
        Assert.Empty(coverage.Keys.Except(properties));
        Assert.All(coverage.Where(entry => entry.Value.Section is null), entry => Assert.False(string.IsNullOrWhiteSpace(entry.Value.Why), entry.Key));
        // Every section a property is filed under exists in "All" (so a full backup carries it).
        Assert.All(coverage.Values.Where(value => value.Section is not null), value => Assert.True(WorkspaceBackupSection.All.HasFlag(value.Section!.Value)));
    }

    private (InMemoryMonitoringWorkspaceStore Store, SyslogStore Syslog) NewStore(string name)
    {
        var folder = Path.Combine(_dir, name);
        Directory.CreateDirectory(folder);
        var telemetry = new SqliteTelemetryRepository(Path.Combine(folder, "t.db"));
        var syslog = new SyslogStore(Path.Combine(folder, "syslog.db"), NullLogger<SyslogStore>.Instance);
        var store = new InMemoryMonitoringWorkspaceStore(
            new CoverageHostEnvironment(folder),
            new MatmonRuntimeOptions { WorkspacePath = Path.Combine(folder, "ws.json") },
            new MatmonAuthOptions(),
            _protection,
            telemetry,
            NullLogger<InMemoryMonitoringWorkspaceStore>.Instance,
            syslog: syslog);
        _owned.Add(telemetry);
        _owned.Add(syslog);
        _owned.Add(store);
        return (store, syslog);
    }

    [Fact]
    public async Task Collectors_trap_users_the_time_zone_and_the_syslog_log_come_back_on_another_instance()
    {
        var (source, sourceLog) = NewStore("source");
        var rootId = source.GetAllElements().OfType<ProbeElement>().First().Id;
        source.SavePluginAssignment(new PluginAssignment
        {
            PluginId = "syslog-collector",
            ProbeElementId = rootId,
            Settings = new(StringComparer.OrdinalIgnoreCase) { ["udpPort"] = "5514", ["allowedSources"] = "10.0.0.0/8" }
        });
        var syslogSettings = source.GetSyslogSettings();
        syslogSettings.RetentionDays = 30;
        source.UpdateSyslogSettings(syslogSettings);
        var traps = source.GetSnmpTrapSettings();
        traps.Communities = "public-ro";
        traps.V3Users = [new SnmpTrapV3User { Username = "trapper", AuthProtocol = "sha256", AuthPassword = "auth-secret-123" }];
        source.UpdateSnmpTrapSettings(traps);
        source.SetDisplayTimeZoneId("Europe/Berlin");
        sourceLog.TryEnqueue(new SyslogMessage { ReceivedUtc = DateTimeOffset.UtcNow.AddMinutes(-2), SourceAddress = "10.0.0.1", Message = "link up" });
        sourceLog.TryEnqueue(new SyslogMessage { ReceivedUtc = DateTimeOffset.UtcNow.AddMinutes(-1), SourceAddress = "10.0.0.1", Message = "link down", Severity = SyslogSeverity.Error });
        await sourceLog.FlushAsync();

        // Portable (passphrase), so the trap user's password survives the move to another instance.
        const string passphrase = "a-long-enough-passphrase";
        var package = source.CreateBackupBytes(WorkspaceBackupSection.All, passphrase: passphrase);

        var (target, targetLog) = NewStore("target");
        target.RestoreBackupBytes(package, WorkspaceBackupSection.All, passphrase);

        var collector = Assert.Single(target.GetPluginAssignments());
        Assert.Equal("5514", collector.Settings["udpPort"]);
        Assert.Equal(30, target.GetSyslogSettings().RetentionDays);
        var restoredTraps = target.GetSnmpTrapSettings();
        Assert.Equal("public-ro", restoredTraps.Communities);
        Assert.Equal("auth-secret-123", Assert.Single(restoredTraps.V3Users).AuthPassword);
        Assert.Equal("Europe/Berlin", target.GetDisplayTimeZoneId());
        var log = targetLog.ExportNewest(10);
        Assert.Equal(["link up", "link down"], log.Select(message => message.Message));
    }

    [Fact]
    public void A_collector_on_a_probe_the_restore_does_not_bring_is_left_out()
    {
        var (source, _) = NewStore("source");
        var rootProbe = source.GetAllElements().OfType<ProbeElement>().First().Id;
        var remote = source.CreateProbe(rootProbe, "Remote", null);
        source.SavePluginAssignment(new PluginAssignment { PluginId = "snmptrap-collector", ProbeElementId = remote.Id });

        var package = source.CreateBackupBytes(WorkspaceBackupSection.Collectors);
        var (target, _) = NewStore("target");
        target.RestoreBackupBytes(package, WorkspaceBackupSection.Collectors);

        Assert.Empty(target.GetPluginAssignments());
    }

    [Fact]
    public void The_cloud_backup_and_the_nightly_job_leave_the_syslog_log_out_but_carry_the_collectors()
    {
        Assert.False(WorkspaceBackupSections.CloudConfig.HasFlag(WorkspaceBackupSection.SyslogMessages));
        Assert.True(WorkspaceBackupSections.CloudConfig.HasFlag(WorkspaceBackupSection.Collectors));
        Assert.True(WorkspaceBackupSections.CloudConfig.HasFlag(WorkspaceBackupSection.GeneralSettings));
        Assert.True(WorkspaceBackupSections.History.HasFlag(WorkspaceBackupSection.SyslogMessages));

        var (store, _) = NewStore("nightly");
        var nightly = store.GetBackupJobs().Single(job => job.Name == InMemoryMonitoringWorkspaceStore.DefaultLocalJobName);
        Assert.True(nightly.Sections.HasFlag(WorkspaceBackupSection.Collectors));
        Assert.False(nightly.Sections.HasFlag(WorkspaceBackupSection.SyslogMessages));
    }
}

file sealed class CoverageHostEnvironment(string root) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Development";
    public string ApplicationName { get; set; } = "Matmon.Tests";
    public string ContentRootPath { get; set; } = root;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
