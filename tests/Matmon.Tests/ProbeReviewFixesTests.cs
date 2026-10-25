using Matmon.Core.Domain;
using Matmon.Core.Telemetry;
using Matmon.Host;
using Matmon.Host.Services;
using Matmon.Probe;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

/// <summary>
/// Remote probes, found in the review of 2026-10-25: a nested probe's sensors went to the outer probe as well, the root
/// could be given a token that opened every local sensor's credentials, and one NaN reading jammed a probe's offline
/// buffer for good.
/// </summary>
public sealed class ProbeReviewFixesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("matmon-probefix-").FullName;
    private readonly SqliteTelemetryRepository _telemetry;
    private readonly InMemoryMonitoringWorkspaceStore _workspace;

    public ProbeReviewFixesTests()
    {
        _telemetry = new SqliteTelemetryRepository(Path.Combine(_dir, "telemetry.db"));
        _workspace = new InMemoryMonitoringWorkspaceStore(
            new ProbeFixHostEnvironment(_dir),
            new MatmonRuntimeOptions { WorkspacePath = Path.Combine(_dir, "workspace.json") },
            new MatmonAuthOptions(),
            new EphemeralDataProtectionProvider(),
            _telemetry,
            NullLogger<InMemoryMonitoringWorkspaceStore>.Instance);
    }

    private Guid RootId => _workspace.GetAllElements().OfType<ProbeElement>().First(probe => probe.ParentId is null).Id;

    [Fact]
    public void A_probe_runs_its_own_sensors_not_those_of_a_probe_nested_under_it()
    {
        var outer = _workspace.CreateProbe(RootId, "Head office", null);
        var outerHost = _workspace.CreateHost(outer.Id, "core-switch", "10.0.0.1", null);
        var outerPing = _workspace.CreateSensor(outerHost.Id, "Core ping", "ping", string.Empty, null);
        var inner = _workspace.CreateProbe(outer.Id, "Branch", null);
        var innerHost = _workspace.CreateHost(inner.Id, "branch-nas", "10.1.0.5", null);
        var innerPing = _workspace.CreateSensor(innerHost.Id, "NAS ping", "ping", string.Empty, null);
        var provider = new ProbeSensorAssignmentProvider(_workspace);

        var outerSensors = provider.BuildAssignments(outer.ProbeId).Sensors.Select(sensor => sensor.SensorId).ToArray();
        var innerSensors = provider.BuildAssignments(inner.ProbeId).Sensors.Select(sensor => sensor.SensorId).ToArray();

        Assert.Contains(outerPing.Id, outerSensors);
        Assert.DoesNotContain(innerPing.Id, outerSensors);
        Assert.Contains(innerPing.Id, innerSensors);
        Assert.False(provider.TryBuildRecordingContext(outer.ProbeId, innerPing.Id, out _, out _, out _));
        Assert.True(provider.TryBuildRecordingContext(inner.ProbeId, innerPing.Id, out _, out _, out _));
    }

    [Fact]
    public void The_root_probe_never_gets_a_token_and_never_passes_the_probe_check()
    {
        var root = _workspace.GetAllElements().OfType<ProbeElement>().First(probe => probe.ParentId is null);

        Assert.Throws<InvalidOperationException>(() => _workspace.RotateProbeToken(root.Id));
        Assert.False(_workspace.TryValidateProbe(root.ProbeId, "anything"));

        var remote = _workspace.CreateProbe(root.Id, "Branch", null);
        var token = _workspace.RotateProbeToken(remote.Id);
        Assert.True(_workspace.TryValidateProbe(remote.ProbeId, token));
    }

    [Fact]
    public void A_reading_that_is_not_a_number_is_dropped_before_it_travels()
    {
        var result = new SensorExecutionResult(SensorState.Healthy, TimeSpan.Zero, double.NaN, "value NaN")
        {
            Channels =
            [
                new SensorChannelValue { Key = "load", Label = "Load", Value = double.PositiveInfinity, IsDefault = true },
                new SensorChannelValue { Key = "temp", Label = "Temp", Value = 41 }
            ]
        };

        var cleaned = SensorExecutionResultHelper.ApplyDefaultChannelSelection(new MonitoringSettings(), result);

        Assert.Null(cleaned.Value);
        Assert.Null(cleaned.Channels.Single(channel => channel.Key == "load").Value);
        Assert.Equal(41, cleaned.Channels.Single(channel => channel.Key == "temp").Value);
    }

    [Fact]
    public void The_disk_queue_drops_an_item_it_cannot_write_instead_of_jamming()
    {
        var warnings = new List<string>();
        var queue = new JsonLinesQueue<NumberBox>(Path.Combine(_dir, "queue.jsonl"), warnings.Add);

        queue.AddRange([new NumberBox(1), new NumberBox(double.NaN), new NumberBox(3)]);

        Assert.Equal(2, queue.Count);
        Assert.Single(warnings);
        Assert.Equal(2, new JsonLinesQueue<NumberBox>(Path.Combine(_dir, "queue.jsonl")).Count); // what is on disk loads again
    }

    public void Dispose()
    {
        _workspace.Dispose();
        _telemetry.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    public sealed record NumberBox(double Value);
}

file sealed class ProbeFixHostEnvironment(string root) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Test";
    public string ApplicationName { get; set; } = "Matmon.Tests";
    public string ContentRootPath { get; set; } = root;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
