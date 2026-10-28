using System.Text;
using System.Text.Json.Nodes;
using Matmon.Core.Domain;
using Matmon.Core.Telemetry;
using Matmon.Host.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

/// <summary>
/// A restore must bring back everything its sections hold - and a backup made by an OLDER Matmon must come back in
/// today's shape. Before this, the schema migrations ran on the live document after the restore, whose version is
/// always current, so an old backup's data was never migrated; and the alert mutes and the scheduled report were
/// packed but never put back.
/// </summary>
public sealed class BackupRestoreCompletenessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "matmon-backupcomplete-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _owned = [];
    private readonly EphemeralDataProtectionProvider _protection = new();

    public BackupRestoreCompletenessTests() => Directory.CreateDirectory(_dir);

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

    private InMemoryMonitoringWorkspaceStore NewStore(string name)
    {
        var folder = Path.Combine(_dir, name);
        Directory.CreateDirectory(folder);
        var telemetry = new SqliteTelemetryRepository(Path.Combine(folder, "t.db"));
        var store = new InMemoryMonitoringWorkspaceStore(
            new CompletenessHostEnvironment(folder),
            new MatmonRuntimeOptions { WorkspacePath = Path.Combine(folder, "ws.json") },
            new MatmonAuthOptions(),
            _protection,
            telemetry,
            NullLogger<InMemoryMonitoringWorkspaceStore>.Instance);
        _owned.Add(telemetry);
        _owned.Add(store);
        return store;
    }

    private static Guid RootId(InMemoryMonitoringWorkspaceStore store) => store.GetAllElements().OfType<ProbeElement>().First().Id;

    /// <summary>The package as JSON with its document's schema version set (or removed: a backup from before versions).</summary>
    private static byte[] WithSchemaVersion(byte[] package, int? version)
    {
        var json = JsonNode.Parse(package)!.AsObject();
        var document = (json["document"] ?? json["Document"])!.AsObject();
        var key = document.ContainsKey("schemaVersion") ? "schemaVersion" : "SchemaVersion";
        if (version is null)
        {
            document.Remove(key);
        }
        else
        {
            document[key] = version.Value;
        }

        return Encoding.UTF8.GetBytes(json.ToJsonString());
    }

    [Fact]
    public void A_backup_of_an_older_version_is_migrated_on_restore()
    {
        var old = NewStore("old");
        var ups = old.CreateSensor(RootId(old), "UPS", UpsSnmpSensorExecutor.Definition.Key, "10.0.0.5", null);
        old.UpdateElement(ups.Id, element => ((SensorElement)element).Settings.Parameters["snmp.oids"] = UpsSnmpSensorExecutor.LegacyDefaultOids);
        // As if made before migration 7 (the UPS power-cut channels) existed.
        var package = WithSchemaVersion(old.CreateBackupBytes(WorkspaceBackupSection.Topology), 6);

        var fresh = NewStore("fresh");
        var result = fresh.RestoreBackupBytes(package, WorkspaceBackupSection.Topology);

        var restored = fresh.GetAllElements().OfType<SensorElement>().Single(sensor => sensor.Name == "UPS");
        Assert.Equal(UpsSnmpSensorExecutor.DefaultOids, restored.Settings.Parameters["snmp.oids"]);
        Assert.Contains("older Matmon (data version 6)", result.Message);
    }

    [Fact]
    public void A_backup_from_before_data_versions_existed_restores_and_is_migrated()
    {
        var old = NewStore("old");
        var ups = old.CreateSensor(RootId(old), "UPS", UpsSnmpSensorExecutor.Definition.Key, "10.0.0.5", null);
        old.UpdateElement(ups.Id, element => ((SensorElement)element).Settings.Parameters["snmp.oids"] = UpsSnmpSensorExecutor.LegacyDefaultOids);
        var package = WithSchemaVersion(old.CreateBackupBytes(WorkspaceBackupSection.Topology), null);

        var fresh = NewStore("fresh");
        fresh.RestoreBackupBytes(package, WorkspaceBackupSection.Topology);

        var restored = fresh.GetAllElements().OfType<SensorElement>().Single(sensor => sensor.Name == "UPS");
        Assert.Equal(UpsSnmpSensorExecutor.DefaultOids, restored.Settings.Parameters["snmp.oids"]);
    }

    [Fact]
    public void A_backup_of_a_newer_version_is_refused_before_anything_changes()
    {
        var source = NewStore("source");
        source.CreateSensor(RootId(source), "From the future", "ping", "10.0.0.9", null);
        var package = WithSchemaVersion(source.CreateBackupBytes(WorkspaceBackupSection.Topology), InMemoryMonitoringWorkspaceStore.CurrentSchemaVersion + 5);

        var target = NewStore("target");
        var before = target.GetAllElements().Count;

        var problem = Assert.Throws<InvalidOperationException>(() => target.RestoreBackupBytes(package, WorkspaceBackupSection.Topology));
        Assert.Contains("newer Matmon", problem.Message);
        Assert.Equal(before, target.GetAllElements().Count);
    }

    [Fact]
    public void Alert_mutes_and_the_scheduled_report_come_back_with_their_sections()
    {
        var source = NewStore("source");
        var sensor = source.CreateSensor(RootId(source), "Muted ping", "ping", "10.0.0.7", null);
        source.MuteElementAlerts(sensor.Id, null, "admin");
        var report = source.GetSummaryReportSettings();
        report.Enabled = true;
        report.HourOfDay = 6;
        source.UpdateSummaryReportSettings(report);
        var package = source.CreateBackupBytes(WorkspaceBackupSection.Topology | WorkspaceBackupSection.Alerts | WorkspaceBackupSection.Notifications);

        var target = NewStore("target");
        target.RestoreBackupBytes(package, WorkspaceBackupSection.Topology | WorkspaceBackupSection.Alerts | WorkspaceBackupSection.Notifications);

        Assert.Contains(target.GetActiveAlertMutes(), mute => mute.ElementId == sensor.Id);
        Assert.True(target.GetSummaryReportSettings().Enabled);
        Assert.Equal(6, target.GetSummaryReportSettings().HourOfDay);
    }
}

file sealed class CompletenessHostEnvironment(string root) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Development";
    public string ApplicationName { get; set; } = "Matmon.Tests";
    public string ContentRootPath { get; set; } = root;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
