using Matmon.Core.Domain;
using Matmon.Core.Telemetry;
using Matmon.Host.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

/// <summary>The push sensor: a job calls in, silence is the alarm. What a call may say, when the sensor turns Critical, and
/// that the last push survives a restart (it rides the observations) - plus the token every push sensor gets.</summary>
public sealed class PushSensorTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 23, 3, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "matmon-push-" + Guid.NewGuid().ToString("N"));

    public PushSensorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    private static MonitoringSettings Every(int minutes, int grace)
    {
        var settings = new MonitoringSettings();
        settings.Parameters[PushSensor.IntervalParameter] = minutes.ToString();
        settings.Parameters[PushSensor.GraceParameter] = grace.ToString();
        return settings;
    }

    [Theory]
    [InlineData(null, SensorState.Healthy)]
    [InlineData("ok", SensorState.Healthy)]
    [InlineData("SUCCESS", SensorState.Healthy)]
    [InlineData("0", SensorState.Healthy)]
    [InlineData("warn", SensorState.Warning)]
    [InlineData("1", SensorState.Warning)]
    [InlineData("failed", SensorState.Critical)]
    [InlineData("down", SensorState.Critical)]
    [InlineData("2", SensorState.Critical)]
    public void A_push_says_how_things_went_in_plain_words_or_numbers(string? status, SensorState expected) =>
        Assert.Equal(expected, PushSensor.ParseStatus(status));

    [Fact]
    public void A_word_that_means_nothing_is_refused_and_a_message_becomes_one_short_line()
    {
        Assert.Null(PushSensor.ParseStatus("maybe"));
        Assert.Equal("backup done 12 GB", PushSensor.CleanMessage("backup done\r\n12 GB\t"));
        Assert.Equal(PushSensor.MaxMessageLength, PushSensor.CleanMessage(new string('x', 2000))!.Length);
        Assert.Null(PushSensor.CleanMessage("  "));
    }

    [Fact]
    public void Silence_longer_than_expected_plus_grace_is_critical()
    {
        var push = new PushSensor.Push(T0, SensorState.Healthy, 12.5, "backup done");
        var settings = Every(60, 15);

        var fresh = PushSensor.Evaluate(push, settings, T0.AddMinutes(5));
        Assert.Equal(SensorState.Healthy, fresh.State);
        Assert.Equal("Last push 5 min ago: backup done", fresh.Message);
        Assert.Equal(300, fresh.Channels.Single(channel => channel.Key == "sinceLastPush").Value);
        Assert.Equal(12.5, fresh.Channels.Single(channel => channel.Key == "value").Value);

        Assert.Equal(SensorState.Healthy, PushSensor.Evaluate(push, settings, T0.AddMinutes(75)).State);
        var late = PushSensor.Evaluate(push, settings, T0.AddMinutes(76));
        Assert.Equal(SensorState.Critical, late.State);
        Assert.Equal("No push for 76 min - expected every 60 min.", late.Message);
    }

    [Fact]
    public void A_push_that_reported_a_failure_is_critical_until_the_next_one_and_none_yet_is_unknown()
    {
        Assert.Equal(SensorState.Critical, PushSensor.Evaluate(new PushSensor.Push(T0, SensorState.Critical, null, "disk full"), Every(60, 15), T0.AddMinutes(1)).State);
        Assert.Equal(SensorState.Warning, PushSensor.Evaluate(new PushSensor.Push(T0, SensorState.Warning, null, null), Every(60, 15), T0.AddMinutes(1)).State);
        Assert.Equal(SensorState.Unknown, PushSensor.Evaluate(null, Every(60, 15), T0).State);
    }

    [Fact]
    public void The_last_push_rides_the_observation_and_comes_back_from_it()
    {
        var push = new PushSensor.Push(T0, SensorState.Warning, 3, "slow");
        var result = PushSensor.FromPush(push);
        var observation = new SensorObservation { TimestampUtc = T0, State = result.State, Channels = result.Channels.ToList(), Message = result.Message };

        Assert.Equal(push, PushSensor.FromObservation(observation));
        Assert.True(result.Channels.Single(channel => channel.Key == PushSensor.PushedAtChannel).IsVirtual);
        Assert.Null(PushSensor.FromObservation(new SensorObservation()));
    }

    [Fact]
    public void Only_a_push_sensor_without_a_token_gets_one()
    {
        var empty = new MonitoringSettings();
        Assert.True(PushSensor.EnsureToken("push", empty));
        Assert.True(empty.Parameters[PushSensor.TokenParameter].Length >= 30);

        var kept = empty.Parameters[PushSensor.TokenParameter];
        Assert.False(PushSensor.EnsureToken("push", empty));
        Assert.Equal(kept, empty.Parameters[PushSensor.TokenParameter]);
        Assert.False(PushSensor.EnsureToken("ping", new MonitoringSettings()));
    }

    [Fact]
    public void A_new_push_sensor_has_an_address_and_after_a_restart_the_last_push_is_still_known()
    {
        var path = Path.Combine(_dir, "ws.json");
        File.WriteAllText(path, $$"""
            { "schemaVersion": {{InMemoryMonitoringWorkspaceStore.CurrentSchemaVersion}},
              "rootProbe": { "$kind": "probe", "probeId": "primary", "id": "11111111-1111-1111-1111-111111111111", "name": "Primary Probe", "children": [] } }
            """);
        using var telemetry = new SqliteTelemetryRepository(Path.Combine(_dir, "t.db"));
        using var store = new InMemoryMonitoringWorkspaceStore(
            new PushTestHostEnvironment(_dir), new MatmonRuntimeOptions { WorkspacePath = path }, new MatmonAuthOptions(),
            new EphemeralDataProtectionProvider(), telemetry, NullLogger<InMemoryMonitoringWorkspaceStore>.Instance);

        var sensor = store.CreateSensor(new Guid("11111111-1111-1111-1111-111111111111"), "Nightly backup", "push", string.Empty, null);
        var token = sensor.Settings.Parameters[PushSensor.TokenParameter];
        Assert.Equal(sensor.Id, store.FindPushSensor(token)!.SensorId);
        Assert.Null(store.FindPushSensor(token + "x"));
        Assert.Null(store.FindPushSensor(""));

        var push = new PushSensor.Push(T0, SensorState.Healthy, null, "done");
        store.RecordSensorObservation(sensor.Id, PushSensor.FromPush(push), T0);

        // A fresh registry - what a restart leaves - finds the push in the latest observation.
        Assert.Equal(push, new PushRegistry(store).Last(sensor.Id));
    }
}

file sealed class PushTestHostEnvironment(string contentRoot) : IHostEnvironment
{
    public string ApplicationName { get; set; } = "Matmon.Tests";
    public string EnvironmentName { get; set; } = "Development";
    public string ContentRootPath { get; set; } = contentRoot;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
