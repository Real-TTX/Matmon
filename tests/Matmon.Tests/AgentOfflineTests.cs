using Matmon.Core.Domain;

namespace Matmon.Tests;

/// <summary>
/// An agent host that may go offline (a notebook): its silence is "offline" (Paused, no alert) instead of an error, it warns
/// only once it has been away longer than its limit, and the agent's own "going offline" notice counts at once.
/// </summary>
public sealed class AgentOfflineTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Offline_is_a_notice_since_the_last_beat_three_missed_beats_or_no_beat_since_the_start()
    {
        Assert.Null(AgentOffline.Since(Now.AddSeconds(-30), null, Now));
        Assert.Equal(Now.AddMinutes(-5), AgentOffline.Since(Now.AddMinutes(-5), null, Now));
        Assert.Equal(Now.AddSeconds(-10), AgentOffline.Since(Now.AddSeconds(-20), Now.AddSeconds(-10), Now));
        // A notice older than the last heartbeat is from before it came back.
        Assert.Null(AgentOffline.Since(Now.AddSeconds(-10), Now.AddMinutes(-3), Now));
        Assert.Equal(DateTimeOffset.MinValue, AgentOffline.Since(null, null, Now));
    }

    [Fact]
    public void Offline_is_paused_and_warns_only_past_the_limit()
    {
        var away = AgentOffline.Result(Now.AddHours(-3), 14, "NOTEBOOK-07", 10_800, Now);
        Assert.Equal(SensorState.Paused, away.State);
        Assert.Equal("NOTEBOOK-07 is offline for 3 h 0 min (allowed for this host)", away.Message);

        var gone = AgentOffline.Result(Now.AddDays(-15), 14, "NOTEBOOK-07", null, Now);
        Assert.Equal(SensorState.Warning, gone.State);
        Assert.Contains("longer than the 14 days", gone.Message);

        Assert.Equal(SensorState.Paused, AgentOffline.Result(Now.AddDays(-400), 0, "NB", null, Now).State);
        Assert.Contains("since this instance started", AgentOffline.Result(DateTimeOffset.MinValue, 14, "NB", null, Now).Message);
    }

    [Fact]
    public async Task The_connection_sensor_of_an_offline_notebook_is_paused_and_of_any_other_agent_critical()
    {
        var lookup = new Lookup { LastSeen = DateTimeOffset.UtcNow.AddMinutes(-10) };
        var notebook = new ProbeHeartbeatSensorExecutor(lookup, new Policy("nb-07"));
        var server = new ProbeHeartbeatSensorExecutor(lookup, new Policy("nb-07"));
        var settings = new MonitoringSettings();
        settings.Thresholds["channel:ageSeconds:critical"] = "> 130";

        var offline = await notebook.ExecuteAsync(new SensorExecutionContext("probe-heartbeat", "nb-07", settings));
        var silent = await server.ExecuteAsync(new SensorExecutionContext("probe-heartbeat", "srv-01", settings));

        Assert.Equal(SensorState.Paused, offline.State);
        Assert.Contains("is offline for 10 min", offline.Message);
        Assert.Equal(SensorState.Critical, silent.State);

        // Back: a fresh heartbeat is judged as before.
        lookup.LastSeen = DateTimeOffset.UtcNow.AddSeconds(-5);
        Assert.Equal(SensorState.Healthy, (await notebook.ExecuteAsync(new SensorExecutionContext("probe-heartbeat", "nb-07", settings))).State);

        // It said it is going to sleep: offline at once, before any heartbeat is missed.
        lookup.GoingOffline = DateTimeOffset.UtcNow;
        Assert.Equal(SensorState.Paused, (await notebook.ExecuteAsync(new SensorExecutionContext("probe-heartbeat", "nb-07", settings))).State);
    }

    private sealed class Lookup : IProbeHeartbeatLookup
    {
        public DateTimeOffset LastSeen { get; set; }

        public DateTimeOffset? GoingOffline { get; set; }

        public bool TryGetLastHeartbeat(string probeId, out DateTimeOffset lastSeenUtc, out string probeName)
        {
            lastSeenUtc = LastSeen;
            probeName = probeId.ToUpperInvariant();
            return true;
        }

        public bool TryGetGoingOffline(string probeId, out DateTimeOffset atUtc)
        {
            atUtc = GoingOffline ?? default;
            return GoingOffline is not null;
        }
    }

    private sealed class Policy(string probeId) : IAgentOfflinePolicy
    {
        public bool TryGet(string id, out int warnDays)
        {
            warnDays = 14;
            return string.Equals(id, probeId, StringComparison.OrdinalIgnoreCase);
        }
    }
}
