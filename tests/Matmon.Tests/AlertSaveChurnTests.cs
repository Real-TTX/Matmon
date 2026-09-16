using Matmon.Core.Domain;
using Matmon.Core.Telemetry;
using Matmon.Host.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

/// <summary>The poll hot path (RecordSensorObservation) must only re-save workspace.json when it actually changed
/// _document.Alerts. Observations + events live in SQLite, so a healthy poll, or an active alert whose only change
/// is its LastSeenUtc, must NOT rewrite the file every poll (that was per-poll disk churn).</summary>
public sealed class AlertSaveChurnTests : IDisposable
{
    private readonly string _dir;

    public AlertSaveChurnTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "matmon-churn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    private InMemoryMonitoringWorkspaceStore NewStore(IDataProtectionProvider dp, string wsPath, ITelemetryRepository telemetry) =>
        new(new ChurnHostEnvironment(_dir),
            new MatmonRuntimeOptions { WorkspacePath = wsPath },
            new MatmonAuthOptions(),
            dp,
            telemetry,
            NullLogger<InMemoryMonitoringWorkspaceStore>.Instance);

    private Guid CreateSensor(InMemoryMonitoringWorkspaceStore store, string name)
    {
        var rootId = store.GetAllElements().OfType<ProbeElement>().First().Id;
        return store.CreateSensor(rootId, name, "ping", "host", null).Id;
    }

    [Fact]
    public void Re_recording_an_identical_active_alert_does_not_rewrite_the_file()
    {
        var dp = new EphemeralDataProtectionProvider();
        var wsPath = Path.Combine(_dir, "ws.json");
        Guid sensorId;

        // Run 1: raise an alert (this DOES persist), then close the store to flush.
        using (var tel = new SqliteTelemetryRepository(Path.Combine(_dir, "t1.db")))
        {
            var store = NewStore(dp, wsPath, tel);
            sensorId = CreateSensor(store, "Ping A");
            store.RecordSensorObservation(sensorId, SensorExecutionResult.Critical(TimeSpan.FromMilliseconds(1), "host down"), DateTimeOffset.UtcNow);
            store.Dispose();
        }

        // Run 2: the ctor re-serialises the loaded doc (F0). Re-recording the SAME active alert (only LastSeenUtc
        // would change) must not queue a save, so Dispose writes nothing and the file is byte-identical.
        var tel2 = new SqliteTelemetryRepository(Path.Combine(_dir, "t2.db"));
        var store2 = NewStore(dp, wsPath, tel2);
        var f0 = File.ReadAllBytes(wsPath);
        store2.RecordSensorObservation(sensorId, SensorExecutionResult.Critical(TimeSpan.FromMilliseconds(1), "host down"), DateTimeOffset.UtcNow.AddMinutes(5));
        store2.Dispose();
        tel2.Dispose();
        var f1 = File.ReadAllBytes(wsPath);

        Assert.Equal(f0, f1); // no churn from a LastSeenUtc-only re-record
    }

    [Fact]
    public void Healthy_poll_with_no_active_alert_does_not_rewrite_the_file()
    {
        var dp = new EphemeralDataProtectionProvider();
        var wsPath = Path.Combine(_dir, "ws2.json");

        var tel = new SqliteTelemetryRepository(Path.Combine(_dir, "t.db"));
        var store = NewStore(dp, wsPath, tel);
        var sensorId = CreateSensor(store, "Ping B");
        store.Dispose();
        tel.Dispose();

        var tel2 = new SqliteTelemetryRepository(Path.Combine(_dir, "t2b.db"));
        var store2 = NewStore(dp, wsPath, tel2);
        var f0 = File.ReadAllBytes(wsPath);
        store2.RecordSensorObservation(sensorId, SensorExecutionResult.Healthy(TimeSpan.FromMilliseconds(1), "ok"), DateTimeOffset.UtcNow);
        store2.Dispose();
        tel2.Dispose();
        var f1 = File.ReadAllBytes(wsPath);

        Assert.Equal(f0, f1); // a healthy poll changes no alerts -> no save
    }

    [Fact]
    public void A_material_change_still_rewrites_the_file()
    {
        var dp = new EphemeralDataProtectionProvider();
        var wsPath = Path.Combine(_dir, "ws3.json");
        Guid sensorId;

        using (var tel = new SqliteTelemetryRepository(Path.Combine(_dir, "t3.db")))
        {
            var store = NewStore(dp, wsPath, tel);
            sensorId = CreateSensor(store, "Ping C");
            store.RecordSensorObservation(sensorId, SensorExecutionResult.Warning(TimeSpan.FromMilliseconds(1), "slow"), DateTimeOffset.UtcNow);
            store.Dispose();
        }

        var tel2 = new SqliteTelemetryRepository(Path.Combine(_dir, "t3b.db"));
        var store2 = NewStore(dp, wsPath, tel2);
        var f0 = File.ReadAllBytes(wsPath);
        // Escalate Warning -> Critical: a material change, so it MUST persist.
        store2.RecordSensorObservation(sensorId, SensorExecutionResult.Critical(TimeSpan.FromMilliseconds(1), "down"), DateTimeOffset.UtcNow.AddMinutes(1));
        store2.Dispose();
        tel2.Dispose();
        var f1 = File.ReadAllBytes(wsPath);

        Assert.NotEqual(f0, f1); // state/message change is persisted
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}

file sealed class ChurnHostEnvironment : IHostEnvironment
{
    public ChurnHostEnvironment(string contentRoot)
    {
        ContentRootPath = contentRoot;
        ContentRootFileProvider = new NullFileProvider();
    }

    public string ApplicationName { get; set; } = "Matmon.Tests";
    public string EnvironmentName { get; set; } = "Development";
    public string ContentRootPath { get; set; }
    public IFileProvider ContentRootFileProvider { get; set; }
}
