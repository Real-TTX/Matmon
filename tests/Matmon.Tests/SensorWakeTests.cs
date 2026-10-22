using System.Collections.Concurrent;
using System.Net;
using Matmon.Core.Domain;
using Matmon.Core.Telemetry;
using Matmon.Host.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

/// <summary>
/// "Poll on trap": a trap from 10.0.0.5 makes the sensors OF 10.0.0.5 poll now - the ones on its host (by the host's
/// address) and the ones that name it as their own target, locally or as a run job on their remote probe - and nobody
/// else's. A burst of traps polls each sensor once per 10 s, and a paused sensor stays paused.
/// </summary>
public sealed class SensorWakeTests : IDisposable
{
    private static readonly Guid UpsSensor = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TargetedSensor = new("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid OtherSensor = new("aaaaaaaa-0000-0000-0000-000000000003");
    private static readonly Guid PausedSensor = new("aaaaaaaa-0000-0000-0000-000000000004");
    private static readonly Guid BranchSensor = new("aaaaaaaa-0000-0000-0000-000000000005");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "matmon-wake-" + Guid.NewGuid().ToString("N"));

    public SensorWakeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    [Fact]
    public async Task A_trap_polls_the_sensors_of_its_device_locally_and_on_the_remote_probe()
    {
        var path = Path.Combine(_dir, "workspace.json");
        File.WriteAllText(path, $$"""
            {
              "schemaVersion": {{InMemoryMonitoringWorkspaceStore.CurrentSchemaVersion}},
              "rootProbe": { "$kind": "probe", "probeId": "primary", "id": "11111111-1111-1111-1111-111111111111", "name": "Primary Probe", "children": [
                { "$kind": "host", "id": "22222222-0000-0000-0000-000000000001", "parentId": "11111111-1111-1111-1111-111111111111", "name": "UPS", "address": "10.0.0.5", "children": [
                  { "$kind": "sensor", "id": "{{UpsSensor}}", "parentId": "22222222-0000-0000-0000-000000000001", "name": "UPS", "sensorTypeKey": "nut-ups", "target": "" },
                  { "$kind": "sensor", "id": "{{PausedSensor}}", "parentId": "22222222-0000-0000-0000-000000000001", "name": "Paused", "sensorTypeKey": "ping", "target": "", "isPaused": true }
                ] },
                { "$kind": "host", "id": "22222222-0000-0000-0000-000000000002", "parentId": "11111111-1111-1111-1111-111111111111", "name": "Monitor box", "address": "10.0.0.7", "children": [
                  { "$kind": "sensor", "id": "{{TargetedSensor}}", "parentId": "22222222-0000-0000-0000-000000000002", "name": "UPS web page", "sensorTypeKey": "http", "target": "http://10.0.0.5/status" },
                  { "$kind": "sensor", "id": "{{OtherSensor}}", "parentId": "22222222-0000-0000-0000-000000000002", "name": "Ping", "sensorTypeKey": "ping", "target": "" }
                ] },
                { "$kind": "probe", "probeId": "branch", "id": "33333333-0000-0000-0000-000000000001", "parentId": "11111111-1111-1111-1111-111111111111", "name": "Branch", "children": [
                  { "$kind": "host", "id": "22222222-0000-0000-0000-000000000003", "parentId": "33333333-0000-0000-0000-000000000001", "name": "Branch UPS", "address": "10.0.0.5", "children": [
                    { "$kind": "sensor", "id": "{{BranchSensor}}", "parentId": "22222222-0000-0000-0000-000000000003", "name": "UPS", "sensorTypeKey": "nut-ups", "target": "" }
                  ] }
                ] }
              ] }
            }
            """);

        using var telemetry = new SqliteTelemetryRepository(Path.Combine(_dir, "telemetry.db"));
        using var store = new InMemoryMonitoringWorkspaceStore(
            new WakeTestHostEnvironment(_dir),
            new MatmonRuntimeOptions { WorkspacePath = path },
            new MatmonAuthOptions(),
            new EphemeralDataProtectionProvider(),
            telemetry,
            NullLogger<InMemoryMonitoringWorkspaceStore>.Instance);
        var executed = new ConcurrentBag<Guid>();
        var services = new ServiceCollection()
            .AddSingleton<ISensorExecutionService>(new RecordingExecution(executed))
            .BuildServiceProvider();
        var runs = new OnDemandRunStore();
        var wake = new SensorWakeService(store, new ProbeSensorAssignmentProvider(store), runs, services.GetRequiredService<IServiceScopeFactory>(), NullLogger<SensorWakeService>.Instance);

        var woken = wake.WakeSensorsOf(IPAddress.Parse("10.0.0.5"));

        Assert.Equal(3, woken); // the UPS host's sensor, the one that names 10.0.0.5 as its target, the branch's
        var job = Assert.Single(runs.TakePending("branch"));
        Assert.Equal(BranchSensor, job.SensorId);
        for (var attempt = 0; attempt < 100 && executed.Count < 2; attempt++)
        {
            await Task.Delay(20);
        }

        Assert.Equal(new[] { UpsSensor, TargetedSensor }.OrderBy(id => id), executed.OrderBy(id => id));
        Assert.DoesNotContain(OtherSensor, executed);
        Assert.DoesNotContain(PausedSensor, executed);

        // A burst: the same device again at once polls nothing more.
        Assert.Equal(0, wake.WakeSensorsOf(IPAddress.Parse("10.0.0.5")));
        // Another device is its own.
        Assert.Equal(1, wake.WakeSensorsOf(IPAddress.Parse("10.0.0.7")));
    }

    private sealed class RecordingExecution(ConcurrentBag<Guid> executed) : ISensorExecutionService
    {
        public ValueTask<SensorExecutionResult> ExecuteNowAsync(Guid sensorId, MonitoringSettings? overrideSettings = null, CancellationToken cancellationToken = default)
        {
            executed.Add(sensorId);
            return ValueTask.FromResult(SensorExecutionResult.Healthy(TimeSpan.Zero));
        }

        public ValueTask<SensorExecutionResult> ExecuteTransientAsync(string sensorTypeKey, string target, MonitoringSettings settings, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(SensorExecutionResult.Healthy(TimeSpan.Zero));
    }
}

file sealed class WakeTestHostEnvironment(string contentRoot) : IHostEnvironment
{
    public string ApplicationName { get; set; } = "Matmon.Tests";
    public string EnvironmentName { get; set; } = "Development";
    public string ContentRootPath { get; set; } = contentRoot;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
