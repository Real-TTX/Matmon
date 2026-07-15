using Matmon.Core.Domain;
using Matmon.Core.Telemetry;
using Matmon.Host.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

/// <summary>
/// A custom (admin-authored) script sensor type is stored as a SensorDefinition in the workspace: it must show
/// up in the catalog (so the type dropdown + CreateSensor gate see it), enforce a unique key, and - critically -
/// survive a reload with its script fields intact (CloneSensorDefinition is rebuilt on every load).
/// </summary>
public sealed class CustomSensorTypeTests : IDisposable
{
    private readonly string _dir;
    private readonly string _workspacePath;
    private readonly string _dbPath;

    public CustomSensorTypeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "matmon-ct-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _workspacePath = Path.Combine(_dir, "workspace.json");
        _dbPath = Path.Combine(_dir, "telemetry.db");
    }

    private InMemoryMonitoringWorkspaceStore NewStore(ITelemetryRepository telemetry) =>
        new(
            new CtTestHostEnvironment(_dir),
            new MatmonRuntimeOptions { WorkspacePath = _workspacePath },
            new MatmonAuthOptions(),
            new EphemeralDataProtectionProvider(),
            telemetry,
            NullLogger<InMemoryMonitoringWorkspaceStore>.Instance);

    [Fact]
    public void Create_lists_and_exposes_the_custom_type_in_the_catalog()
    {
        using var telemetry = new SqliteTelemetryRepository(_dbPath);
        using var store = NewStore(telemetry);

        var created = store.CreateCustomSensorType("Widget Health", "Checks the widget", "pwsh", "json", "Write-Output 42", null);

        Assert.StartsWith("custom:", created.Key);
        Assert.True(created.IsCustomScript);
        Assert.Equal("Write-Output 42", created.ScriptBody);

        Assert.Single(store.GetCustomSensorTypes());
        // Must appear in the full catalog so the type dropdown + CreateSensor validation accept it.
        Assert.Contains(store.GetSensorDefinitions(), d => d.Key == created.Key && d.IsCustomScript);
    }

    [Fact]
    public void Duplicate_name_is_rejected()
    {
        using var telemetry = new SqliteTelemetryRepository(_dbPath);
        using var store = NewStore(telemetry);

        store.CreateCustomSensorType("Widget Health", null, "pwsh", "auto", "echo x", null);
        Assert.Throws<InvalidOperationException>(() =>
            store.CreateCustomSensorType("widget health", null, "bash", "auto", "echo y", null));
    }

    [Fact]
    public void Update_changes_the_stored_script_and_keeps_the_key()
    {
        using var telemetry = new SqliteTelemetryRepository(_dbPath);
        using var store = NewStore(telemetry);

        var created = store.CreateCustomSensorType("Widget Health", null, "pwsh", "auto", "echo x", null);
        var updated = store.UpdateCustomSensorType(created.Key, "Widget Health", "now with bash", "bash", "text", "echo changed", null);

        Assert.NotNull(updated);
        Assert.Equal(created.Key, updated!.Key);
        Assert.Equal("bash", updated.ScriptLanguage);
        Assert.Equal("echo changed", updated.ScriptBody);
    }

    [Fact]
    public void Custom_type_survives_a_reload_with_its_script_fields_intact()
    {
        string key;
        using (var telemetry = new SqliteTelemetryRepository(_dbPath))
        using (var store = NewStore(telemetry))
        {
            key = store.CreateCustomSensorType("Widget Health", "desc", "bash", "regex", "echo v=1", "(?<v>\\d+)").Key;
        } // Dispose flushes to disk

        using var telemetry2 = new SqliteTelemetryRepository(_dbPath);
        using var reloaded = NewStore(telemetry2);

        // CloneSensorDefinition (rebuilt on load) must copy the script fields, else the type silently breaks.
        var def = Assert.Single(reloaded.GetCustomSensorTypes());
        Assert.Equal(key, def.Key);
        Assert.True(def.IsCustomScript);
        Assert.Equal("bash", def.ScriptLanguage);
        Assert.Equal("regex", def.ScriptOutputFormat);
        Assert.Equal("echo v=1", def.ScriptBody);
        Assert.Equal("(?<v>\\d+)", def.ScriptRegexPattern);
    }

    [Fact]
    public void Delete_removes_an_unused_custom_type()
    {
        using var telemetry = new SqliteTelemetryRepository(_dbPath);
        using var store = NewStore(telemetry);

        var created = store.CreateCustomSensorType("Widget Health", null, "pwsh", "auto", "echo x", null);
        Assert.True(store.DeleteCustomSensorType(created.Key));
        Assert.Empty(store.GetCustomSensorTypes());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception)
        {
            // best-effort temp cleanup
        }
    }
}

file sealed class CtTestHostEnvironment : IHostEnvironment
{
    public CtTestHostEnvironment(string contentRoot)
    {
        ContentRootPath = contentRoot;
        ContentRootFileProvider = new NullFileProvider();
    }

    public string ApplicationName { get; set; } = "Matmon.Tests";
    public string EnvironmentName { get; set; } = "Development";
    public string ContentRootPath { get; set; }
    public IFileProvider ContentRootFileProvider { get; set; }
}
