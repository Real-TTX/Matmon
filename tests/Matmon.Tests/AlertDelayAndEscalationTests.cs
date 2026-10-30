using Matmon.Core.Domain;
using Matmon.Core.Telemetry;
using Matmon.Host.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

/// <summary>
/// An alert delay keeps a blip from alarming (a UPS on battery for two seconds); an escalation tells someone else when
/// nobody acknowledges. The delay has to hold on BOTH paths that raise alerts - the poll path, which notifies, and the
/// dashboard's sync, which does not: an alert the dashboard raised first would make the poll path find it "already
/// there" and the notification would never go out.
/// </summary>
public sealed class AlertDelayAndEscalationTests : IDisposable
{
    private static readonly Guid SensorId = new("33333333-3333-3333-3333-333333333333");
    private static readonly DateTimeOffset T0 = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "matmon-delay-" + Guid.NewGuid().ToString("N"));
    private readonly List<AlertNotificationEvent> _notified = [];

    public AlertDelayAndEscalationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    private (InMemoryMonitoringWorkspaceStore Store, SqliteTelemetryRepository Telemetry) Boot(int? alertDelaySeconds)
    {
        var path = Path.Combine(_dir, $"ws-{Guid.NewGuid():N}.json");
        var settings = alertDelaySeconds is null ? "{}" : $$"""{ "alertDelaySeconds": {{alertDelaySeconds}} }""";
        File.WriteAllText(path, $$"""
            {
              "schemaVersion": {{InMemoryMonitoringWorkspaceStore.CurrentSchemaVersion}},
              "rootProbe": { "$kind": "probe", "probeId": "primary", "id": "11111111-1111-1111-1111-111111111111", "name": "Primary Probe", "children": [
                { "$kind": "sensor", "id": "{{SensorId}}", "parentId": "11111111-1111-1111-1111-111111111111", "name": "UPS", "sensorTypeKey": "nut-ups", "target": "nas", "settings": {{settings}} }
              ] }
            }
            """);

        var telemetry = new SqliteTelemetryRepository(Path.Combine(_dir, $"t-{Guid.NewGuid():N}.db"));
        var store = new InMemoryMonitoringWorkspaceStore(
            new DelayTestHostEnvironment(_dir),
            new MatmonRuntimeOptions { WorkspacePath = path },
            new MatmonAuthOptions(),
            new EphemeralDataProtectionProvider(),
            telemetry,
            NullLogger<InMemoryMonitoringWorkspaceStore>.Instance,
            new RecordingSink(_notified));
        return (store, telemetry);
    }

    private static SensorExecutionResult Critical() => SensorExecutionResult.Critical(TimeSpan.FromMilliseconds(5), "On battery");

    private static SensorExecutionResult Healthy() => SensorExecutionResult.Healthy(TimeSpan.FromMilliseconds(5), "Online");

    private static MonitoringAlertCandidate Candidate() =>
        new(SensorId, MonitoringElementKind.Sensor, "UPS", "Primary Probe / UPS", SensorState.Critical, "On battery");

    [Fact]
    public void Without_a_delay_the_first_problem_alarms_at_once()
    {
        var (store, telemetry) = Boot(null);
        using (telemetry) using (store)
        {
            store.RecordSensorObservation(SensorId, Critical(), T0);

            Assert.Single(store.Workspace.Alerts, alert => alert.IsActive);
            Assert.Single(_notified);
        }
    }

    [Fact]
    public void With_a_delay_the_alert_comes_only_once_the_problem_has_lasted()
    {
        var (store, telemetry) = Boot(30);
        using (telemetry) using (store)
        {
            store.RecordSensorObservation(SensorId, Critical(), T0);
            store.RecordSensorObservation(SensorId, Critical(), T0.AddSeconds(15));
            Assert.Empty(store.Workspace.Alerts);
            Assert.Empty(_notified);

            store.RecordSensorObservation(SensorId, Critical(), T0.AddSeconds(30));

            var alert = Assert.Single(store.Workspace.Alerts, candidate => candidate.IsActive);
            Assert.Equal(T0.AddSeconds(30), alert.FirstSeenUtc);
            Assert.Single(_notified);
        }
    }

    [Fact]
    public void A_blip_shorter_than_the_delay_never_alarms_and_a_new_problem_starts_counting_again()
    {
        var (store, telemetry) = Boot(30);
        using (telemetry) using (store)
        {
            store.RecordSensorObservation(SensorId, Critical(), T0);
            store.RecordSensorObservation(SensorId, Healthy(), T0.AddSeconds(15));
            store.RecordSensorObservation(SensorId, Critical(), T0.AddSeconds(31)); // a NEW streak, 0 s old

            Assert.Empty(store.Workspace.Alerts);
            Assert.Empty(_notified);
        }
    }

    [Fact]
    public void The_dashboard_sync_does_not_raise_a_silent_alert_while_the_delay_runs()
    {
        var (store, telemetry) = Boot(30);
        using (telemetry) using (store)
        {
            store.RecordSensorObservation(SensorId, Critical(), T0);
            store.SyncAlerts([Candidate()], T0.AddSeconds(10));
            Assert.Empty(store.Workspace.Alerts);

            // Once the delay is over the poll path raises it - WITH its notification.
            store.RecordSensorObservation(SensorId, Critical(), T0.AddSeconds(30));
            Assert.Single(store.Workspace.Alerts, alert => alert.IsActive);
            Assert.Single(_notified);
        }
    }

    [Fact]
    public void The_dashboard_sync_keeps_first_and_last_seen_at_the_time_of_the_check()
    {
        // Opening the dashboard used to stamp "last seen" (and a new alert's "first seen") with the moment of the visit.
        var (store, telemetry) = Boot(null);
        using (telemetry) using (store)
        {
            var checkedAt = T0;
            store.SyncAlerts([Candidate() with { ObservedUtc = checkedAt }], T0.AddMinutes(40));
            var alert = store.Workspace.Alerts.Single();
            Assert.Equal(checkedAt, alert.FirstSeenUtc);
            Assert.Equal(checkedAt, alert.LastSeenUtc);

            // A later visit with no newer check moves nothing; a newer check moves "last seen" to it.
            store.SyncAlerts([Candidate() with { ObservedUtc = checkedAt }], T0.AddMinutes(90));
            Assert.Equal(checkedAt, store.Workspace.Alerts.Single().LastSeenUtc);
            store.SyncAlerts([Candidate() with { ObservedUtc = T0.AddMinutes(60) }], T0.AddMinutes(95));
            Assert.Equal(T0.AddMinutes(60), store.Workspace.Alerts.Single().LastSeenUtc);
            Assert.Equal(checkedAt, store.Workspace.Alerts.Single().FirstSeenUtc);
        }
    }

    [Fact]
    public void An_escalation_step_is_recorded_once_on_the_alert()
    {
        var (store, telemetry) = Boot(null);
        using (telemetry) using (store)
        {
            store.RecordSensorObservation(SensorId, Critical(), T0);
            var alert = store.Workspace.Alerts.Single();
            var ruleId = Guid.NewGuid();

            Assert.True(store.TryMarkAlertEscalated(alert.Id, ruleId, 0, T0.AddMinutes(10)));
            Assert.False(store.TryMarkAlertEscalated(alert.Id, ruleId, 0, T0.AddMinutes(11)));
            Assert.True(store.TryMarkAlertEscalated(alert.Id, ruleId, 1, T0.AddMinutes(30)));
            Assert.Equal(2, store.Workspace.Alerts.Single().Escalations.Count);
        }
    }

    // --- when a step is due (pure) ---------------------------------------------------------------------------

    private static NotificationRule Rule(params int[] minutes) => new()
    {
        Name = "UPS",
        TriggerStates = [SensorState.Critical],
        Escalations = minutes.Select(value => new NotificationEscalationStep { AfterMinutes = value }).ToList()
    };

    private static MonitoringAlert Alert() => new() { ElementId = SensorId, State = SensorState.Critical, FirstSeenUtc = T0, LastSeenUtc = T0 };

    [Fact]
    public void A_step_is_due_once_its_minutes_have_passed()
    {
        var rule = Rule(10, 30);

        Assert.Empty(NotificationEscalation.DueSteps(rule, Alert(), T0.AddMinutes(9)));
        Assert.Equal([0], NotificationEscalation.DueSteps(rule, Alert(), T0.AddMinutes(10)));
        Assert.Equal([0, 1], NotificationEscalation.DueSteps(rule, Alert(), T0.AddMinutes(45)));
    }

    [Fact]
    public void A_step_already_sent_is_not_due_again()
    {
        var rule = Rule(10, 30);
        var alert = Alert();
        alert.Escalations.Add(new AlertEscalationMark { RuleId = rule.Id, Step = 0, SentUtc = T0.AddMinutes(10) });

        Assert.Equal([1], NotificationEscalation.DueSteps(rule, alert, T0.AddMinutes(45)));
    }

    [Fact]
    public void An_acknowledged_or_recovered_alert_or_another_state_does_not_escalate()
    {
        var rule = Rule(10);
        var acknowledged = Alert();
        acknowledged.AcknowledgedUtc = T0.AddMinutes(2);
        var recovered = Alert();
        recovered.RecoveredUtc = T0.AddMinutes(2);
        var warning = Alert();
        warning.State = SensorState.Warning;
        var resolved = Alert();
        resolved.ResolvedUtc = T0.AddMinutes(2);

        Assert.Empty(NotificationEscalation.DueSteps(rule, acknowledged, T0.AddHours(1)));
        Assert.Empty(NotificationEscalation.DueSteps(rule, recovered, T0.AddHours(1)));
        Assert.Empty(NotificationEscalation.DueSteps(rule, warning, T0.AddHours(1)));
        Assert.Empty(NotificationEscalation.DueSteps(rule, resolved, T0.AddHours(1)));
    }

    [Fact]
    public void A_switched_off_rule_does_not_escalate()
    {
        var rule = Rule(10);
        rule.Enabled = false;

        Assert.Empty(NotificationEscalation.DueSteps(rule, Alert(), T0.AddHours(1)));
    }

    [Fact]
    public void The_stored_steps_are_sorted_capped_and_without_empty_rows()
    {
        var steps = NotificationEscalation.Normalize(
        [
            new() { AfterMinutes = 60 },
            new() { AfterMinutes = 0 },
            new() { AfterMinutes = 15 },
            new() { AfterMinutes = 999_999 },
            new() { AfterMinutes = 30 }
        ]);

        Assert.Equal([15, 30, 60], steps.Select(step => step.AfterMinutes));
    }

    private sealed class RecordingSink(List<AlertNotificationEvent> events) : INotificationSink
    {
        public void Enqueue(AlertNotificationEvent notificationEvent) => events.Add(notificationEvent);
    }
}

file sealed class DelayTestHostEnvironment(string contentRoot) : IHostEnvironment
{
    public string ApplicationName { get; set; } = "Matmon.Tests";
    public string EnvironmentName { get; set; } = "Development";
    public string ContentRootPath { get; set; } = contentRoot;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
