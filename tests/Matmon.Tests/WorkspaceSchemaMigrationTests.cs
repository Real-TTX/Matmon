using System.Text.Json;
using Matmon.Core.Telemetry;
using Matmon.Host.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;

namespace Matmon.Tests;

/// <summary>
/// The workspace's one-time migrations run from a single ordered, numbered registry rather than from a
/// hand-placed list of calls in the constructor. Two things are worth pinning: the registry's SHAPE (which a
/// reviewer cannot check by reading it) and that a workspace written before the pipeline existed still gets
/// migrated and then records how far it came.
/// </summary>
public class WorkspaceSchemaMigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "matmon-schema-" + Guid.NewGuid().ToString("N"));

    public WorkspaceSchemaMigrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void The_registry_is_unique_ascending_and_gapless()
    {
        var versions = InMemoryMonitoringWorkspaceStore.SchemaMigrationCatalog.Select(entry => entry.Version).ToArray();

        Assert.NotEmpty(versions);
        Assert.Equal(versions.Distinct().Count(), versions.Length);
        Assert.Equal(versions.OrderBy(version => version), versions);
        // Gapless from 1: a hole would mean a version number was burned, and a document stamped inside the
        // hole would silently skip whatever is added there later.
        Assert.Equal(Enumerable.Range(1, versions.Length), versions);
    }

    [Fact]
    public void The_current_version_is_the_last_migration()
    {
        // Adding a migration without bumping the constant would stamp documents as fully migrated while the
        // new step had never run on them - the one mistake this design has to make impossible to miss.
        Assert.Equal(
            InMemoryMonitoringWorkspaceStore.CurrentSchemaVersion,
            InMemoryMonitoringWorkspaceStore.SchemaMigrationCatalog[^1].Version);
    }

    [Fact]
    public void Every_migration_is_named()
    {
        Assert.All(InMemoryMonitoringWorkspaceStore.SchemaMigrationCatalog,
            entry => Assert.False(string.IsNullOrWhiteSpace(entry.Name)));
    }

    [Fact]
    public void A_workspace_from_before_the_pipeline_is_migrated_and_then_stamped()
    {
        var path = Path.Combine(_dir, "ws.json");
        // No schemaVersion at all - exactly what every workspace in the field looks like - plus a sensor on a
        // sensor type that was retired, which migration 2 has to rewrite.
        File.WriteAllText(path, """
        {
          "rootProbe": {
            "$kind": "probe",
            "probeId": "primary",
            "id": "11111111-1111-1111-1111-111111111111",
            "name": "Primary Probe",
            "children": [
              {
                "$kind": "sensor",
                "id": "22222222-2222-2222-2222-222222222222",
                "name": "PVE",
                "sensorTypeKey": "proxmox",
                "target": "pve.local",
                "settings": { "parameters": { "pve.scope": "cluster" } }
              }
            ]
          }
        }
        """);

        string typeAfterBoot;
        using (var telemetry = new SqliteTelemetryRepository(Path.Combine(_dir, "t.db")))
        using (var store = NewStore(path, telemetry))
        {
            typeAfterBoot = store.GetAllElements()
                .OfType<Matmon.Core.Domain.SensorElement>()
                .Single(sensor => sensor.Name == "PVE")
                .SensorTypeKey;
        }

        Assert.Equal("proxmox-health", typeAfterBoot);

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(
            InMemoryMonitoringWorkspaceStore.CurrentSchemaVersion,
            document.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public void A_current_workspace_keeps_its_version_across_a_restart()
    {
        var path = Path.Combine(_dir, "ws2.json");

        using (var telemetry = new SqliteTelemetryRepository(Path.Combine(_dir, "t2.db")))
        using (var store = NewStore(path, telemetry))
        {
            store.GetAllElements();
        }

        using (var telemetry = new SqliteTelemetryRepository(Path.Combine(_dir, "t2.db")))
        using (var store = NewStore(path, telemetry))
        {
            store.GetAllElements();
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(
            InMemoryMonitoringWorkspaceStore.CurrentSchemaVersion,
            document.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    private InMemoryMonitoringWorkspaceStore NewStore(string workspacePath, ITelemetryRepository telemetry) =>
        new(new SchemaHostEnvironment(_dir),
            new MatmonRuntimeOptions { WorkspacePath = workspacePath },
            new MatmonAuthOptions(),
            new EphemeralDataProtectionProvider(),
            telemetry,
            NullLogger<InMemoryMonitoringWorkspaceStore>.Instance);

    private sealed class SchemaHostEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Matmon.Tests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class NullFileProvider : IFileProvider
    {
        public IDirectoryContents GetDirectoryContents(string subpath) => NotFoundDirectoryContents.Singleton;
        public IFileInfo GetFileInfo(string subpath) => new NotFoundFileInfo(subpath);
        public IChangeToken Watch(string filter) => NullChangeToken.Singleton;
    }
}
