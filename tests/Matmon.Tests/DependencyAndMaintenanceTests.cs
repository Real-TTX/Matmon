using Matmon.Core.Domain;
using Matmon.Core.Telemetry;
using Matmon.Host.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

/// <summary>
/// Dependencies (one outage, one alert: what hangs on a down router is held - Unknown, no alert) and maintenance windows (no
/// new alert while one is on). Both on the real store, through the poll path that notifies and the dashboard sync that does
/// not, plus the pure rules behind them.
/// </summary>
public sealed class DependencyAndMaintenanceTests : IDisposable
{
    private static readonly Guid RootId = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SiteId = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid RouterId = new("33333333-3333-3333-3333-333333333333");
    private static readonly Guid SwitchId = new("44444444-4444-4444-4444-444444444444");
    private static readonly Guid ServerId = new("55555555-5555-5555-5555-555555555555");
    private static readonly Guid HttpId = new("66666666-6666-6666-6666-666666666666");
    private static readonly Guid LoneId = new("77777777-7777-7777-7777-777777777777");
    private static readonly Guid OptedOutId = new("88888888-8888-8888-8888-888888888888");
    private static readonly DateTimeOffset T0 = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "matmon-dep-" + Guid.NewGuid().ToString("N"));
    private readonly List<AlertNotificationEvent> _notified = [];
    private readonly SensorWakeQueue _wake = new();

    public DependencyAndMaintenanceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    /// <summary>Site (depends on the router ping inside it) > router ping, switch ping, server (depends on the switch ping) >
    /// HTTP + an opted-out sensor. Next to the site a sensor without any dependency.</summary>
    private (InMemoryMonitoringWorkspaceStore Store, SqliteTelemetryRepository Telemetry) Boot()
    {
        var path = Path.Combine(_dir, $"ws-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, $$"""
            {
              "schemaVersion": {{InMemoryMonitoringWorkspaceStore.CurrentSchemaVersion}},
              "rootProbe": { "$kind": "probe", "probeId": "primary", "id": "{{RootId}}", "name": "Primary Probe", "children": [
                { "$kind": "folder", "id": "{{SiteId}}", "parentId": "{{RootId}}", "name": "Site", "settings": { "dependencySensorId": "{{RouterId}}" }, "children": [
                  { "$kind": "sensor", "id": "{{RouterId}}", "parentId": "{{SiteId}}", "name": "Router ping", "sensorTypeKey": "ping", "target": "10.0.0.1" },
                  { "$kind": "sensor", "id": "{{SwitchId}}", "parentId": "{{SiteId}}", "name": "Switch ping", "sensorTypeKey": "ping", "target": "10.0.0.2" },
                  { "$kind": "host", "id": "{{ServerId}}", "parentId": "{{SiteId}}", "name": "Server", "address": "10.0.0.10", "tags": ["patch"], "settings": { "dependencySensorId": "{{SwitchId}}" }, "children": [
                    { "$kind": "sensor", "id": "{{HttpId}}", "parentId": "{{ServerId}}", "name": "HTTP", "sensorTypeKey": "http", "target": "http://10.0.0.10" },
                    { "$kind": "sensor", "id": "{{OptedOutId}}", "parentId": "{{ServerId}}", "name": "Own uplink", "sensorTypeKey": "ping", "target": "10.0.0.11", "settings": { "dependencySensorId": "{{Guid.Empty}}" } }
                  ] }
                ] },
                { "$kind": "sensor", "id": "{{LoneId}}", "parentId": "{{RootId}}", "name": "Lone", "sensorTypeKey": "ping", "target": "10.9.9.9" }
              ] }
            }
            """);

        var telemetry = new SqliteTelemetryRepository(Path.Combine(_dir, $"t-{Guid.NewGuid():N}.db"));
        var store = new InMemoryMonitoringWorkspaceStore(
            new DependencyTestHostEnvironment(_dir),
            new MatmonRuntimeOptions { WorkspacePath = path },
            new MatmonAuthOptions(),
            new EphemeralDataProtectionProvider(),
            telemetry,
            NullLogger<InMemoryMonitoringWorkspaceStore>.Instance,
            new RecordingSink(_notified),
            wakeQueue: _wake);
        return (store, telemetry);
    }

    private static SensorExecutionResult Down(string message = "timed out") => SensorExecutionResult.Critical(TimeSpan.FromMilliseconds(5), message);

    private static SensorExecutionResult Up() => SensorExecutionResult.Healthy(TimeSpan.FromMilliseconds(5), "ok");

    private List<Guid> WakeRequests()
    {
        var ids = new List<Guid>();
        while (_wake.Reader.TryRead(out var id))
        {
            ids.Add(id);
        }

        return ids;
    }

    private static MonitoringAlert? AlertOf(InMemoryMonitoringWorkspaceStore store, Guid id) =>
        store.Workspace.Alerts.FirstOrDefault(alert => alert.IsActive && alert.ElementId == id);

    // ---- dependencies on the store ---------------------------------------------------------------------------------

    [Fact]
    public void Behind_a_down_router_a_failing_sensor_is_held_Unknown_with_the_reason_and_raises_no_alert()
    {
        var (store, telemetry) = Boot();
        using (telemetry) using (store)
        {
            store.RecordSensorObservation(RouterId, Down(), T0);
            store.RecordSensorObservation(SwitchId, Down("no answer"), T0.AddSeconds(10));

            var held = store.GetLatestSensorObservations()[SwitchId];
            Assert.Equal(SensorState.Unknown, held.State);
            Assert.Contains("Router ping", held.Message);
            Assert.Contains("no answer", held.Message);
            Assert.NotNull(AlertOf(store, RouterId));
            Assert.Null(AlertOf(store, SwitchId));
            Assert.Equal([RouterId], _notified.Select(notification => notification.ElementId));
            Assert.True(store.GetDependency(SwitchId)!.HoldsThisSensor);
        }
    }

    [Fact]
    public void A_chain_holds_further_down_router_then_switch_then_server()
    {
        var (store, telemetry) = Boot();
        using (telemetry) using (store)
        {
            store.RecordSensorObservation(RouterId, Down(), T0);
            store.RecordSensorObservation(SwitchId, Down(), T0.AddSeconds(5));
            store.RecordSensorObservation(HttpId, Down(), T0.AddSeconds(10));

            Assert.Equal(SensorState.Unknown, store.GetLatestSensorObservations()[HttpId].State);
            Assert.Contains("Switch ping", store.GetLatestSensorObservations()[HttpId].Message);
            Assert.Equal([RouterId], _notified.Select(notification => notification.ElementId));
        }
    }

    [Fact]
    public void When_the_dependent_notices_first_its_alert_waits_for_the_router_which_is_polled_at_once()
    {
        var (store, telemetry) = Boot();
        using (telemetry) using (store)
        {
            store.RecordSensorObservation(RouterId, Up(), T0.AddSeconds(-60));
            WakeRequests();

            store.RecordSensorObservation(SwitchId, Down(), T0);
            Assert.Null(AlertOf(store, SwitchId));
            Assert.Contains(RouterId, WakeRequests());

            // The router turns out to be down: the switch is held at once (no waiting for its own next poll), one alert.
            store.RecordSensorObservation(RouterId, Down(), T0.AddSeconds(3));
            Assert.Equal(SensorState.Unknown, store.GetLatestSensorObservations()[SwitchId].State);
            Assert.Null(AlertOf(store, SwitchId));
            Assert.Equal([RouterId], _notified.Select(notification => notification.ElementId));
        }
    }

    [Fact]
    public void When_the_router_turns_out_to_be_up_the_waiting_sensor_alerts_right_then()
    {
        var (store, telemetry) = Boot();
        using (telemetry) using (store)
        {
            store.RecordSensorObservation(RouterId, Up(), T0.AddSeconds(-60));
            store.RecordSensorObservation(SwitchId, Down(), T0);
            Assert.Null(AlertOf(store, SwitchId));

            store.RecordSensorObservation(RouterId, Up(), T0.AddSeconds(3));

            Assert.NotNull(AlertOf(store, SwitchId));
            Assert.Equal([SwitchId], _notified.Select(notification => notification.ElementId));
        }
    }

    [Fact]
    public void A_router_that_never_answers_the_check_does_not_hold_the_alert_for_ever()
    {
        var (store, telemetry) = Boot();
        using (telemetry) using (store)
        {
            store.RecordSensorObservation(RouterId, Up(), T0.AddSeconds(-60));
            store.RecordSensorObservation(SwitchId, Down(), T0);
            store.RecordSensorObservation(SwitchId, Down(), T0 + MonitoringDependencies.CheckWait);

            Assert.NotNull(AlertOf(store, SwitchId));
        }
    }

    [Fact]
    public void A_dependency_that_never_ran_does_not_hold_anything()
    {
        var (store, telemetry) = Boot();
        using (telemetry) using (store)
        {
            store.RecordSensorObservation(SwitchId, Down(), T0);

            Assert.NotNull(AlertOf(store, SwitchId));
        }
    }

    [Fact]
    public void When_the_router_is_back_its_held_dependents_are_polled_again_and_come_back_without_an_alert()
    {
        var (store, telemetry) = Boot();
        using (telemetry) using (store)
        {
            store.RecordSensorObservation(RouterId, Down(), T0);
            store.RecordSensorObservation(SwitchId, Down(), T0.AddSeconds(5));
            WakeRequests();

            store.RecordSensorObservation(RouterId, Up(), T0.AddMinutes(3));
            Assert.Contains(SwitchId, WakeRequests());

            store.RecordSensorObservation(SwitchId, Up(), T0.AddMinutes(3).AddSeconds(2));
            Assert.Equal(SensorState.Healthy, store.GetLatestSensorObservations()[SwitchId].State);
            Assert.False(store.GetDependency(SwitchId)!.HoldsThisSensor);
            Assert.DoesNotContain(_notified, notification => notification.ElementId == SwitchId);
        }
    }

    [Fact]
    public void An_alert_raised_before_the_router_went_down_stays_open_and_is_not_called_recovered()
    {
        var (store, telemetry) = Boot();
        using (telemetry) using (store)
        {
            store.RecordSensorObservation(SwitchId, Down(), T0);
            store.RecordSensorObservation(RouterId, Up(), T0.AddSeconds(1)); // checked after the problem began, and up
            Assert.NotNull(AlertOf(store, SwitchId));

            store.RecordSensorObservation(RouterId, Down(), T0.AddMinutes(1));
            Assert.Equal(SensorState.Unknown, store.GetLatestSensorObservations()[SwitchId].State);
            store.SyncAlerts([], T0.AddMinutes(1).AddSeconds(5)); // the dashboard sees no problem candidate for the held switch

            var alert = AlertOf(store, SwitchId);
            Assert.NotNull(alert);
            Assert.Null(alert.RecoveredUtc);
            Assert.DoesNotContain(_notified, notification => notification.ElementId == SwitchId && notification.Transition == NotificationTransition.Recovered);
        }
    }

    [Fact]
    public void The_router_ping_inside_the_folder_it_guards_does_not_depend_on_itself_and_none_switches_it_off()
    {
        var (store, telemetry) = Boot();
        using (telemetry) using (store)
        {
            Assert.Null(store.GetDependency(RouterId));
            Assert.Equal(RouterId, store.GetDependency(SwitchId)!.SensorId);
            Assert.Equal(SwitchId, store.GetDependency(HttpId)!.SensorId);
            Assert.Null(store.GetDependency(OptedOutId));
            Assert.Null(store.GetDependency(LoneId));

            store.RecordSensorObservation(RouterId, Down(), T0);
            store.RecordSensorObservation(SwitchId, Down(), T0.AddSeconds(1));
            store.RecordSensorObservation(OptedOutId, Down(), T0.AddSeconds(2));
            Assert.NotNull(AlertOf(store, OptedOutId));
        }
    }

    [Fact]
    public void The_editor_is_told_why_a_dependency_cannot_be_set()
    {
        var (store, telemetry) = Boot();
        using (telemetry) using (store)
        {
            Assert.Contains("itself", store.DescribeDependencyProblem(SwitchId, SwitchId));
            Assert.Contains("sensor", store.DescribeDependencyProblem(SwitchId, SiteId));
            // The router depending on the switch, which depends on the router through the site: a loop.
            Assert.Contains("loop", store.DescribeDependencyProblem(RouterId, SwitchId));
            Assert.Null(store.DescribeDependencyProblem(RouterId, LoneId));
            Assert.Null(store.DescribeDependencyProblem(RouterId, MonitoringDependencies.None));
            Assert.Null(store.DescribeDependencyProblem(RouterId, null));
            // the try-out left nothing behind
            Assert.Null(store.FindElement(RouterId)!.Settings.DependencySensorId);
        }
    }

    [Fact]
    public void The_editor_shows_what_inherit_means()
    {
        var (store, telemetry) = Boot();
        using (telemetry) using (store)
        {
            Assert.Equal(RouterId, store.GetInheritedDependency(ServerId)!.SensorId);
            Assert.Equal(SwitchId, store.GetInheritedDependency(HttpId)!.SensorId);
            Assert.Null(store.GetInheritedDependency(RouterId)); // the site's own dependency is the router itself
        }
    }

    // ---- maintenance windows on the store --------------------------------------------------------------------------

    private static MaintenanceWindow OnceWindow(Guid? target = null, string? tag = null) => new()
    {
        Name = "Patch night",
        Recurrence = MaintenanceRecurrence.Once,
        TargetElementId = target,
        TargetTag = tag,
        StartUtc = T0.AddMinutes(-10),
        EndUtc = T0.AddHours(1)
    };

    [Fact]
    public void In_a_maintenance_window_a_problem_raises_no_alert_and_after_it_the_next_check_does()
    {
        var (store, telemetry) = Boot();
        using (telemetry) using (store)
        {
            store.SaveMaintenanceWindow(OnceWindow(LoneId));

            Assert.True(store.IsInMaintenance(LoneId, T0));
            store.RecordSensorObservation(LoneId, Down(), T0);
            store.SyncAlerts([new MonitoringAlertCandidate(LoneId, MonitoringElementKind.Sensor, "Lone", "Primary Probe / Lone", SensorState.Critical, "timed out")], T0.AddSeconds(5));
            Assert.Null(AlertOf(store, LoneId));
            Assert.Empty(_notified);

            store.RecordSensorObservation(LoneId, Down(), T0.AddHours(1));
            Assert.NotNull(AlertOf(store, LoneId));
            Assert.Single(_notified);
        }
    }

    [Fact]
    public void A_window_covers_everything_below_its_element_or_every_element_with_its_tag()
    {
        var (store, telemetry) = Boot();
        using (telemetry) using (store)
        {
            var site = store.SaveMaintenanceWindow(OnceWindow(SiteId));
            Assert.True(store.IsInMaintenance(HttpId, T0));
            Assert.False(store.IsInMaintenance(LoneId, T0));
            Assert.Equal("Patch night", store.GetActiveMaintenance(RouterId, T0)!.Name);
            store.DeleteMaintenanceWindow(site.Id);

            store.SaveMaintenanceWindow(OnceWindow(tag: "patch"));
            Assert.True(store.IsInMaintenance(HttpId, T0));   // the server's tag cascades to its sensors
            Assert.False(store.IsInMaintenance(SwitchId, T0));
            Assert.False(store.IsInMaintenance(HttpId, T0.AddHours(2)));
        }
    }

    [Fact]
    public void A_window_that_is_not_valid_is_refused_with_the_reason()
    {
        var (store, telemetry) = Boot();
        using (telemetry) using (store)
        {
            var noTarget = OnceWindow();
            Assert.Contains("covers", Assert.Throws<InvalidOperationException>(() => store.SaveMaintenanceWindow(noTarget)).Message);

            var backwards = OnceWindow(LoneId);
            backwards.EndUtc = backwards.StartUtc;
            Assert.Contains("after the start", Assert.Throws<InvalidOperationException>(() => store.SaveMaintenanceWindow(backwards)).Message);

            var gone = OnceWindow(Guid.NewGuid());
            Assert.Contains("no longer exists", Assert.Throws<InvalidOperationException>(() => store.SaveMaintenanceWindow(gone)).Message);

            var weekly = new MaintenanceWindow { Name = "Weekly", TargetElementId = LoneId, Recurrence = MaintenanceRecurrence.Weekly };
            Assert.Contains("weekday", Assert.Throws<InvalidOperationException>(() => store.SaveMaintenanceWindow(weekly)).Message);
            Assert.Empty(store.GetMaintenanceWindows());
        }
    }

    // ---- the pure rules ------------------------------------------------------------------------------------------------

    [Fact]
    public void Only_a_dependency_chain_back_to_the_sensor_itself_is_ignored()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var chain = new Dictionary<Guid, Guid?> { [a] = b, [b] = a, [c] = a };

        Assert.Null(MonitoringDependencies.ResolveWithoutLoop(a, id => chain.GetValueOrDefault(id)));
        Assert.Equal(a, MonitoringDependencies.ResolveWithoutLoop(c, id => chain.GetValueOrDefault(id))); // the loop does not include c
        Assert.Null(MonitoringDependencies.ResolveWithoutLoop(Guid.NewGuid(), _ => null));
    }

    [Fact]
    public void A_failing_dependent_waits_only_while_the_dependency_is_older_than_the_problem_and_not_too_long()
    {
        Assert.True(MonitoringDependencies.WaitForCheck(T0, T0.AddSeconds(-30), T0.AddSeconds(10)));
        Assert.False(MonitoringDependencies.WaitForCheck(T0, T0.AddSeconds(1), T0.AddSeconds(10)));
        Assert.False(MonitoringDependencies.WaitForCheck(T0, null, T0.AddSeconds(10)));
        Assert.False(MonitoringDependencies.WaitForCheck(T0, T0.AddSeconds(-30), T0 + MonitoringDependencies.CheckWait));
    }

    private static MaintenanceWindow Weekly(params DayOfWeek[] days) => new()
    {
        Name = "Patch Tuesday",
        TargetElementId = LoneId,
        Recurrence = MaintenanceRecurrence.Weekly,
        DaysOfWeek = [.. days],
        StartTime = new TimeOnly(22, 0),
        DurationMinutes = 120
    };

    [Fact]
    public void A_weekly_window_runs_past_midnight_and_its_end_is_exclusive()
    {
        var window = Weekly(DayOfWeek.Tuesday);
        var tuesday = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero); // a Tuesday

        Assert.Null(MaintenanceSchedule.ActiveAt(window, tuesday.AddHours(21).AddMinutes(59), TimeZoneInfo.Utc));
        Assert.NotNull(MaintenanceSchedule.ActiveAt(window, tuesday.AddHours(23).AddMinutes(30), TimeZoneInfo.Utc));
        Assert.NotNull(MaintenanceSchedule.ActiveAt(window, tuesday.AddHours(23).AddMinutes(59), TimeZoneInfo.Utc));
        Assert.Null(MaintenanceSchedule.ActiveAt(window, tuesday.AddDays(1), TimeZoneInfo.Utc));
        Assert.Equal(tuesday.AddDays(7).AddHours(22), MaintenanceSchedule.Next(window, tuesday.AddDays(1), TimeZoneInfo.Utc)!.Value.StartUtc);

        window.Enabled = false;
        Assert.Null(MaintenanceSchedule.ActiveAt(window, tuesday.AddHours(23), TimeZoneInfo.Utc));
        Assert.Null(MaintenanceSchedule.Next(window, tuesday, TimeZoneInfo.Utc));
    }

    [Fact]
    public void Day_31_of_a_monthly_window_is_the_last_day_of_a_short_month()
    {
        var window = new MaintenanceWindow { Name = "Month end", TargetElementId = LoneId, Recurrence = MaintenanceRecurrence.Monthly, DayOfMonth = 31, StartTime = new TimeOnly(4, 0), DurationMinutes = 60 };

        var next = MaintenanceSchedule.Next(window, new DateTimeOffset(2027, 2, 1, 0, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc);

        Assert.Equal(new DateTimeOffset(2027, 2, 28, 4, 0, 0, TimeSpan.Zero), next!.Value.StartUtc);
    }

    [Fact]
    public void A_recurring_time_is_wall_clock_time_in_its_zone_across_daylight_saving()
    {
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        var window = new MaintenanceWindow { Name = "Nightly", TargetElementId = LoneId, Recurrence = MaintenanceRecurrence.Daily, StartTime = new TimeOnly(22, 0), DurationMinutes = 60 };

        // 22:00 in Berlin is 20:00 UTC in summer and 21:00 UTC in winter.
        Assert.Equal(new DateTimeOffset(2026, 7, 1, 20, 0, 0, TimeSpan.Zero), MaintenanceSchedule.Next(window, new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero), berlin)!.Value.StartUtc);
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 21, 0, 0, TimeSpan.Zero), MaintenanceSchedule.Next(window, new DateTimeOffset(2026, 12, 1, 12, 0, 0, TimeSpan.Zero), berlin)!.Value.StartUtc);

        // 02:30 does not exist on the night the clocks go forward - it starts at the first minute that does (03:00 = 01:00 UTC).
        window.StartTime = new TimeOnly(2, 30);
        Assert.Equal(new DateTimeOffset(2026, 3, 29, 1, 0, 0, TimeSpan.Zero), MaintenanceSchedule.Next(window, new DateTimeOffset(2026, 3, 28, 12, 0, 0, TimeSpan.Zero), berlin)!.Value.StartUtc);
    }

    [Fact]
    public void A_one_time_window_is_on_between_its_start_and_its_end_and_then_over()
    {
        var window = OnceWindow(LoneId);

        Assert.Equal(window.StartUtc, MaintenanceSchedule.Next(window, T0.AddHours(-1), TimeZoneInfo.Utc)!.Value.StartUtc);
        Assert.NotNull(MaintenanceSchedule.ActiveAt(window, T0, TimeZoneInfo.Utc));
        Assert.Null(MaintenanceSchedule.ActiveAt(window, window.EndUtc!.Value, TimeZoneInfo.Utc));
        Assert.True(MaintenanceSchedule.IsOver(window, window.EndUtc!.Value));
        Assert.Null(MaintenanceSchedule.Next(window, T0.AddHours(2), TimeZoneInfo.Utc));
    }

    [Fact]
    public void A_schedule_reads_as_one_line()
    {
        Assert.Equal("Tue, Thu 22:00 for 2 h", MaintenanceSchedule.Describe(Weekly(DayOfWeek.Thursday, DayOfWeek.Tuesday), TimeZoneInfo.Utc));
        Assert.Equal("daily 03:00 for 30 min", MaintenanceSchedule.Describe(new MaintenanceWindow { Recurrence = MaintenanceRecurrence.Daily, StartTime = new TimeOnly(3, 0), DurationMinutes = 30 }, TimeZoneInfo.Utc));
        Assert.Equal("2026-10-22 11:50 - 2026-10-22 13:00", MaintenanceSchedule.Describe(OnceWindow(LoneId), TimeZoneInfo.Utc));
    }

    private sealed class RecordingSink(List<AlertNotificationEvent> events) : INotificationSink
    {
        public void Enqueue(AlertNotificationEvent notificationEvent) => events.Add(notificationEvent);
    }
}

file sealed class DependencyTestHostEnvironment(string contentRoot) : IHostEnvironment
{
    public string ApplicationName { get; set; } = "Matmon.Tests";
    public string EnvironmentName { get; set; } = "Development";
    public string ContentRootPath { get; set; } = contentRoot;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
