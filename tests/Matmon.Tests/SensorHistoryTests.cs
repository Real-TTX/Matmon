using Matmon.Core.Domain;
using Matmon.Core.Telemetry;
using Matmon.Host.Services;
using Matmon.Host.Ui;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

/// <summary>The sensor page's two history lists: the status history (SensorStatusHistory - pure) and the checks paged by
/// state straight from the telemetry database, plus the events the store now records for them (who paused / acknowledged,
/// recovered and re-opened alerts).</summary>
public sealed class SensorHistoryTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid Sensor = Guid.NewGuid();
    private readonly string _dir;

    public SensorHistoryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "matmon-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* SQLite may still hold the file a moment */ }
    }

    private static MonitoringEvent Event(int minute, MonitoringEventKind kind, SensorState? state = null, string message = "", string? actor = null)
        => new()
        {
            TimestampUtc = T0.AddMinutes(minute),
            Kind = kind,
            ElementId = Sensor,
            State = state,
            Message = message,
            Actor = actor
        };

    // --- SensorStatusHistory ------------------------------------------------------------------------------------

    [Fact]
    public void State_changes_carry_from_to_how_long_they_held_and_how_long_it_was_down()
    {
        var events = new[]
        {
            Event(0, MonitoringEventKind.StateChanged, SensorState.Healthy, "state OK"),
            Event(10, MonitoringEventKind.StateChanged, SensorState.Critical, "OK -> Error: host down via probe-1"),
            Event(15, MonitoringEventKind.StateChanged, SensorState.Warning, "Error -> Warning: slow"),
            Event(22, MonitoringEventKind.StateChanged, SensorState.Healthy, "Warning -> OK"),
        };

        var history = SensorStatusHistory.Build(events, [], T0.AddMinutes(30));

        Assert.Equal(4, history.Count);
        var recovery = history[0]; // newest first
        Assert.Equal(SensorState.Warning, recovery.FromState);
        Assert.Equal(SensorState.Healthy, recovery.ToState);
        Assert.Equal(TimeSpan.FromMinutes(12), recovery.ProblemDuration); // Error at :10 until OK at :22
        Assert.True(recovery.Ongoing);
        Assert.Equal(TimeSpan.FromMinutes(8), recovery.Duration);
        Assert.Equal(string.Empty, recovery.Message);

        var failure = history[2];
        Assert.Equal(SensorState.Healthy, failure.FromState);
        Assert.Equal(SensorState.Critical, failure.ToState);
        Assert.Equal("host down via probe-1", failure.Message); // the transition text is shown as pills, not repeated
        Assert.Equal(TimeSpan.FromMinutes(5), failure.Duration);
        Assert.False(failure.Ongoing);
        Assert.Null(history[3].FromState);
    }

    [Fact]
    public void Consecutive_state_changes_to_the_same_state_are_one()
    {
        var events = new[]
        {
            Event(0, MonitoringEventKind.StateChanged, SensorState.Critical, "state Error: down"),
            Event(5, MonitoringEventKind.StateChanged, SensorState.Critical, "state Error: down"), // after a restart
            Event(9, MonitoringEventKind.StateChanged, SensorState.Healthy, "Error -> OK"),
        };

        var history = SensorStatusHistory.Build(events, [], T0.AddMinutes(10));

        Assert.Equal(2, history.Count);
        Assert.Equal(TimeSpan.FromMinutes(9), history[1].Duration);
        Assert.Equal(TimeSpan.FromMinutes(9), history[0].ProblemDuration);
    }

    [Fact]
    public void Pause_and_resume_name_who_and_how_long_old_events_by_their_message()
    {
        var events = new[]
        {
            Event(0, MonitoringEventKind.StateChanged, SensorState.Warning, "state Warning"),
            Event(3, MonitoringEventKind.Paused, SensorState.Paused, "Sensor paused by anna@example.com", "anna@example.com"),
            Event(48, MonitoringEventKind.Resumed, SensorState.Healthy, "Sensor resumed by bob"), // before the Actor field
        };

        var history = SensorStatusHistory.Build(events, [], T0.AddHours(1));

        var resumed = history[0];
        Assert.Equal(SensorStatusKind.Resumed, resumed.Kind);
        Assert.Equal("bob", resumed.Actor);
        Assert.Equal(SensorState.Paused, resumed.FromState);
        Assert.Null(resumed.ToState); // the "Healthy" a resume event stores is no state the sensor went to
        Assert.Equal(TimeSpan.FromMinutes(45), resumed.Duration);
        Assert.Equal(string.Empty, resumed.Message);

        var paused = history[1];
        Assert.Equal("anna@example.com", paused.Actor);
        Assert.Equal(SensorState.Warning, paused.FromState);
        Assert.Equal(SensorState.Paused, paused.ToState);
        Assert.False(paused.Ongoing);
        Assert.Equal(SensorStatusCategory.Pause, paused.Category);

        // The warning held until the pause, not until now.
        Assert.Equal(TimeSpan.FromMinutes(3), history[2].Duration);
    }

    [Fact]
    public void A_pause_comes_before_the_alert_it_resolved_in_the_same_moment()
    {
        var history = SensorStatusHistory.Build(
            [
                Event(0, MonitoringEventKind.AlertRaised, SensorState.Critical, "down"),
                Event(5, MonitoringEventKind.AlertResolved, SensorState.Critical, "sensor paused"),
                Event(5, MonitoringEventKind.Paused, SensorState.Paused, "Sensor paused", "anna"),
            ],
            [],
            T0.AddMinutes(6));

        Assert.Equal([SensorStatusKind.AlertResolved, SensorStatusKind.Paused, SensorStatusKind.AlertRaised], history.Select(e => e.Kind));
    }

    [Fact]
    public void A_pause_still_in_effect_counts_on()
    {
        var history = SensorStatusHistory.Build(
            [Event(0, MonitoringEventKind.Paused, SensorState.Paused, "Sensor paused")], [], T0.AddMinutes(20));

        Assert.True(history[0].Ongoing);
        Assert.Equal(TimeSpan.FromMinutes(20), history[0].Duration);
    }

    [Fact]
    public void The_alert_record_fills_in_what_no_event_says_and_never_doubles_what_one_does()
    {
        var alertId = Guid.NewGuid();
        var alert = new MonitoringAlert
        {
            Id = alertId,
            ElementId = Sensor,
            State = SensorState.Critical,
            Message = "host down",
            FirstSeenUtc = T0.AddMinutes(1),
            LastSeenUtc = T0.AddMinutes(9),
            RecoveredUtc = T0.AddMinutes(10),  // older build: no AlertRecovered event
            AcknowledgedUtc = T0.AddMinutes(12),
            AcknowledgedBy = "anna",
            ResolvedUtc = T0.AddMinutes(12),
            Escalations = [new AlertEscalationMark { RuleId = Guid.NewGuid(), Step = 0, SentUtc = T0.AddMinutes(6) }]
        };
        var events = new[]
        {
            Event(1, MonitoringEventKind.AlertRaised, SensorState.Critical, "host down"),
            Event(12, MonitoringEventKind.AlertAcknowledged, SensorState.Critical, "Alert acknowledged by anna"),
            Event(12, MonitoringEventKind.AlertResolved, SensorState.Critical, "host down"),
        };

        var history = SensorStatusHistory.Build(events, [alert], T0.AddMinutes(20));
        var kinds = history.Select(entry => entry.Kind).Reverse().ToArray();

        Assert.Equal(
            [SensorStatusKind.AlertRaised, SensorStatusKind.AlertEscalated, SensorStatusKind.AlertRecovered,
             SensorStatusKind.AlertAcknowledged, SensorStatusKind.AlertResolved],
            kinds);
        Assert.Equal("anna", history.Single(e => e.Kind == SensorStatusKind.AlertAcknowledged).Actor);
        Assert.Equal(TimeSpan.FromMinutes(9), history.Single(e => e.Kind == SensorStatusKind.AlertRecovered).Duration);
        Assert.Equal(TimeSpan.FromMinutes(11), history.Single(e => e.Kind == SensorStatusKind.AlertResolved).Duration);
        Assert.Equal("Escalation step 1 sent", history.Single(e => e.Kind == SensorStatusKind.AlertEscalated).Message);
    }

    [Fact]
    public void The_same_alert_step_recorded_twice_within_moments_is_one()
    {
        var events = new[]
        {
            Event(0, MonitoringEventKind.AlertRaised, SensorState.Critical, "down"),
            Event(5, MonitoringEventKind.AlertResolved, SensorState.Critical, "down"),
            new MonitoringEvent { TimestampUtc = T0.AddMinutes(5).AddSeconds(2), Kind = MonitoringEventKind.AlertResolved, ElementId = Sensor, Message = "down" },
            Event(7, MonitoringEventKind.AlertRaised, SensorState.Critical, "down again"),
        };

        var history = SensorStatusHistory.Build(events, [], T0.AddMinutes(10));

        Assert.Equal(3, history.Count);
    }

    [Fact]
    public void Mutes_keep_their_scope_and_actor()
    {
        var history = SensorStatusHistory.Build(
            [Event(0, MonitoringEventKind.AlertMuted, null, "Alerts muted until 30.10.2026 10:00 by anna", "anna")], [], T0);

        Assert.Equal("until 30.10.2026 10:00", history[0].Message);
        Assert.Equal("anna", history[0].Actor);
        Assert.Equal(SensorStatusCategory.Acknowledgements, history[0].Category);
    }

    [Theory]
    [InlineData("state OK", "")]
    [InlineData("No data -> Error: timeout", "timeout")]
    [InlineData("OK -> Warning via probe-2", "via probe-2")]
    [InlineData("a message that is not a transition", "a message that is not a transition")]
    public void Strips_the_transition_from_a_state_change_message(string message, string expected)
        => Assert.Equal(expected, SensorStatusHistory.StripTransition(message));

    [Theory]
    [InlineData(40, "40 s")]
    [InlineData(12 * 60, "12 min")]
    [InlineData(3 * 3600 + 5 * 60, "3 h 5 min")]
    [InlineData(2 * 86400 + 4 * 3600 + 59, "2 d 4 h")]
    [InlineData(-5, "0 s")]
    public void Formats_spans(int seconds, string expected)
        => Assert.Equal(expected, MonitoringDisplay.FormatSpan(TimeSpan.FromSeconds(seconds)));

    // --- Checks paged by state ----------------------------------------------------------------------------------

    [Fact]
    public void Checks_page_newest_first_by_state_with_counts()
    {
        using var telemetry = new SqliteTelemetryRepository(Path.Combine(_dir, "t.db"));
        for (var i = 0; i < 120; i++)
        {
            telemetry.AppendObservation(new SensorObservation
            {
                SensorId = Sensor,
                TimestampUtc = T0.AddMinutes(i),
                State = i % 10 == 0 ? SensorState.Critical : i % 10 == 1 ? SensorState.Warning : SensorState.Healthy,
                Message = $"check {i}"
            });
        }

        var counts = telemetry.GetObservationStateCounts(Sensor);
        Assert.Equal(12, counts[SensorState.Critical]);
        Assert.Equal(12, counts[SensorState.Warning]);
        Assert.Equal(96, counts[SensorState.Healthy]);

        var errors = telemetry.GetObservationPage(Sensor, [SensorState.Critical], 0, 50);
        Assert.Equal(12, errors.Count);
        Assert.All(errors, observation => Assert.Equal(SensorState.Critical, observation.State));
        Assert.Equal("check 110", errors[0].Message); // newest first

        var problemsPage2 = telemetry.GetObservationPage(Sensor, [SensorState.Warning, SensorState.Critical], 20, 50);
        Assert.Equal(4, problemsPage2.Count);
        Assert.Equal("check 0", problemsPage2[^1].Message); // the oldest check is reachable by paging back

        var everythingPage3 = telemetry.GetObservationPage(Sensor, null, 100, 50);
        Assert.Equal(20, everythingPage3.Count);
        Assert.Empty(telemetry.GetObservationPage(Sensor, [], 0, 50));
    }

    // --- What the store records ---------------------------------------------------------------------------------

    [Fact]
    public void The_store_records_who_paused_and_acknowledged_and_when_an_alert_recovered_and_reopened()
    {
        using var telemetry = new SqliteTelemetryRepository(Path.Combine(_dir, "store.db"));
        var store = new InMemoryMonitoringWorkspaceStore(
            new HistoryHostEnvironment(_dir),
            new MatmonRuntimeOptions { WorkspacePath = Path.Combine(_dir, "ws.json") },
            new MatmonAuthOptions(),
            new EphemeralDataProtectionProvider(),
            telemetry,
            NullLogger<InMemoryMonitoringWorkspaceStore>.Instance);
        try
        {
            var rootId = store.GetAllElements().OfType<ProbeElement>().First().Id;
            var sensorId = store.CreateSensor(rootId, "Ping", "ping", "host", null).Id;
            var start = DateTimeOffset.UtcNow.AddMinutes(-10);

            store.RecordSensorObservation(sensorId, SensorExecutionResult.Critical(TimeSpan.FromMilliseconds(1), "down"), start);
            store.RecordSensorObservation(sensorId, SensorExecutionResult.Healthy(TimeSpan.FromMilliseconds(1)), start.AddMinutes(1));
            store.RecordSensorObservation(sensorId, SensorExecutionResult.Critical(TimeSpan.FromMilliseconds(1), "down again"), start.AddMinutes(2));
            var alert = store.GetElementAlerts(sensorId).Single(a => a.IsActive);
            Assert.True(store.AcknowledgeAlert(alert.Id, "anna"));
            Assert.True(store.SetSensorPaused(sensorId, true, "bob"));
            Assert.True(store.SetSensorPaused(sensorId, false, "bob"));

            var events = store.GetElementEvents(sensorId);
            Assert.Contains(events, e => e.Kind == MonitoringEventKind.AlertRecovered);
            Assert.Contains(events, e => e.Kind == MonitoringEventKind.AlertReopened && e.Message == "down again");
            Assert.Equal("anna", events.Single(e => e.Kind == MonitoringEventKind.AlertAcknowledged).Actor);
            Assert.Equal("bob", events.Single(e => e.Kind == MonitoringEventKind.Paused).Actor);
            Assert.Equal("bob", events.Single(e => e.Kind == MonitoringEventKind.Resumed).Actor);

            var history = SensorStatusHistory.Build(events, store.GetElementAlerts(sensorId), DateTimeOffset.UtcNow);
            Assert.Equal(1, history.Count(e => e.Kind == SensorStatusKind.AlertRaised));
            Assert.Equal(1, history.Count(e => e.Kind == SensorStatusKind.AlertRecovered));
            Assert.Contains(history, e => e.Kind == SensorStatusKind.AlertReopened);
        }
        finally
        {
            store.Dispose();
        }
    }

    [Fact]
    public void An_older_event_table_gets_the_actor_column()
    {
        var path = Path.Combine(_dir, "old.db");
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                CREATE TABLE event (id TEXT PRIMARY KEY, ts INTEGER NOT NULL, kind INTEGER NOT NULL, element_id TEXT NULL,
                    element_kind INTEGER NULL, element_name TEXT NOT NULL, element_path TEXT NOT NULL, state INTEGER NULL,
                    message TEXT NOT NULL, details TEXT NULL);
                INSERT INTO event VALUES ('00000000-0000-0000-0000-000000000001', 1, 9, NULL, NULL, 'x', 'x', NULL, 'Sensor paused', NULL);
                """;
            cmd.ExecuteNonQuery();
        }

        using var telemetry = new SqliteTelemetryRepository(path);
        telemetry.AppendEvent(Event(0, MonitoringEventKind.Paused, SensorState.Paused, "Sensor paused by anna", "anna"));

        Assert.Equal("anna", telemetry.GetElementEvents(Sensor, 10).Single().Actor);
        Assert.Contains(telemetry.GetAllEvents(), e => e.Actor is null && e.ElementId is null);
    }
}

file sealed class HistoryHostEnvironment : IHostEnvironment
{
    public HistoryHostEnvironment(string contentRoot)
    {
        ContentRootPath = contentRoot;
        ContentRootFileProvider = new NullFileProvider();
    }

    public string ApplicationName { get; set; } = "Matmon.Tests";
    public string EnvironmentName { get; set; } = "Development";
    public string ContentRootPath { get; set; }
    public IFileProvider ContentRootFileProvider { get; set; }
}
