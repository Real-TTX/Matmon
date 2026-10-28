using Matmon.Core.Domain;
using Matmon.Core.Telemetry;
using Matmon.Host.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

/// <summary>Status pages: the state rules, the day bars, the daily rollup behind them, and the store.</summary>
public sealed class StatusPageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "matmon-status-" + Guid.NewGuid().ToString("N"));

    public StatusPageTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private InMemoryMonitoringWorkspaceStore NewStore(ITelemetryRepository telemetry) =>
        new(new StatusHostEnvironment(_dir),
            new MatmonRuntimeOptions { WorkspacePath = Path.Combine(_dir, "ws.json") },
            new MatmonAuthOptions(),
            new EphemeralDataProtectionProvider(),
            telemetry,
            NullLogger<InMemoryMonitoringWorkspaceStore>.Instance);

    [Theory]
    [InlineData(new[] { SensorState.Healthy, SensorState.Healthy }, StatusComponentState.Operational)]
    [InlineData(new[] { SensorState.Healthy, SensorState.Warning }, StatusComponentState.Degraded)]
    [InlineData(new[] { SensorState.Healthy, SensorState.Critical }, StatusComponentState.PartialOutage)]
    [InlineData(new[] { SensorState.Critical, SensorState.Critical }, StatusComponentState.MajorOutage)]
    [InlineData(new[] { SensorState.Unknown, SensorState.Paused }, StatusComponentState.Unknown)]
    [InlineData(new SensorState[0], StatusComponentState.Unknown)]
    public void A_component_is_as_bad_as_its_sensors(SensorState[] states, StatusComponentState expected)
    {
        Assert.Equal(expected, StatusPageRules.ComponentState(states, inMaintenance: false, openIncidentImpact: null));
    }

    [Fact]
    public void Maintenance_explains_a_problem_but_not_a_healthy_component()
    {
        Assert.Equal(StatusComponentState.Maintenance, StatusPageRules.ComponentState([SensorState.Critical], true, null));
        Assert.Equal(StatusComponentState.Operational, StatusPageRules.ComponentState([SensorState.Healthy], true, null));
    }

    [Fact]
    public void An_open_incident_raises_the_state_but_never_lowers_it()
    {
        Assert.Equal(StatusComponentState.PartialOutage, StatusPageRules.ComponentState([SensorState.Healthy], false, StatusIncidentImpact.PartialOutage));
        Assert.Equal(StatusComponentState.MajorOutage, StatusPageRules.ComponentState([SensorState.Critical], false, StatusIncidentImpact.Degraded));
    }

    [Fact]
    public void The_headline_is_the_worst_component()
    {
        Assert.Equal("All systems operational", StatusPageRules.Overall([StatusComponentState.Operational, StatusComponentState.Unknown]).Text);
        Assert.Equal("Partial outage", StatusPageRules.Overall([StatusComponentState.Operational, StatusComponentState.PartialOutage]).Text);
        Assert.Equal("No data yet", StatusPageRules.Overall([StatusComponentState.Unknown]).Text);
    }

    [Fact]
    public void Day_bars_end_today_count_a_warning_as_up_and_leave_days_without_data_empty()
    {
        var sensor = Guid.NewGuid();
        var today = new DateOnly(2026, 10, 28);
        var bars = StatusPageRules.DayBars(
        [
            new SensorDailyState(sensor, today, Healthy: 90, Warning: 10, Critical: 0),
            new SensorDailyState(sensor, today.AddDays(-1), Healthy: 50, Warning: 0, Critical: 50)
        ], today, 30);

        Assert.Equal(30, bars.Count);
        Assert.Equal(today, bars[^1].Day);
        Assert.Equal(100, bars[^1].Percent);
        Assert.Equal("ok", bars[^1].Tone);
        Assert.Equal(50, bars[^2].Percent);
        Assert.Equal("error", bars[^2].Tone);
        Assert.Null(bars[0].Percent);
        Assert.Equal("none", bars[0].Tone);
    }

    [Fact]
    public void The_rollup_writes_todays_counts_from_the_statistics_buckets_deduped_per_window()
    {
        using var telemetry = new SqliteTelemetryRepository(Path.Combine(_dir, "t.db"));
        using var store = NewStore(telemetry);
        var rootId = store.GetAllElements().OfType<ProbeElement>().First().Id;
        var sensor = store.CreateSensor(rootId, "Web", "http", "https://example.com", null);
        var now = DateTimeOffset.UtcNow;
        var hour = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero);
        // Two channels of the same window carry the same distribution - counted once.
        foreach (var channel in new[] { "latency", "status" })
        {
            telemetry.UpsertStatisticsBucket(new SensorStatisticsBucket
            {
                SensorId = sensor.Id,
                BucketStartUtc = hour,
                BucketMinutes = 60,
                DefaultChannelKey = channel,
                SampleCount = 12,
                HealthyCount = 10,
                WarningCount = 1,
                CriticalCount = 1
            });
        }

        store.RunTelemetryMaintenance(now);

        var state = Assert.Single(store.GetDailyStates([sensor.Id], DateOnly.FromDateTime(now.UtcDateTime)));
        Assert.Equal(10, state.Healthy);
        Assert.Equal(1, state.Warning);
        Assert.Equal(1, state.Critical);
    }

    [Fact]
    public void Daily_states_are_pruned_by_day_and_removed_with_their_sensor()
    {
        using var telemetry = new SqliteTelemetryRepository(Path.Combine(_dir, "t.db"));
        var keep = Guid.NewGuid();
        var gone = Guid.NewGuid();
        var today = new DateOnly(2026, 10, 28);
        telemetry.UpsertDailyState(new SensorDailyState(keep, today, 1, 0, 0));
        telemetry.UpsertDailyState(new SensorDailyState(keep, today.AddDays(-500), 1, 0, 0));
        telemetry.UpsertDailyState(new SensorDailyState(gone, today, 1, 0, 0));

        telemetry.PruneDailyStates(today.AddDays(-400));
        telemetry.PurgeSensor(gone);

        var left = telemetry.GetDailyStates([keep, gone], today.AddDays(-1000));
        var only = Assert.Single(left);
        Assert.Equal(keep, only.SensorId);
        Assert.Equal(today, only.Day);
    }

    [Fact]
    public void A_page_keeps_its_token_and_incidents_when_the_editor_saves_it()
    {
        using var telemetry = new SqliteTelemetryRepository(Path.Combine(_dir, "t.db"));
        using var store = NewStore(telemetry);
        var saved = store.SaveStatusPage(new StatusPage
        {
            Name = "Example status",
            PublicEnabled = true,
            Components = [new StatusPageComponent { Name = "Website" }, new StatusPageComponent { Name = "Mail" }]
        });
        Assert.False(string.IsNullOrEmpty(saved.PublicToken));
        var mail = saved.Components[1].Id;
        var incident = store.PostStatusIncident(saved.Id, "Mail delayed", StatusIncidentImpact.Degraded, [mail], StatusIncidentPhase.Investigating, "Looking into it.", "admin");

        // The editor posts settings and components only - and drops "Mail".
        var edited = saved.Clone();
        edited.Incidents = [];
        edited.PublicToken = "forged";
        edited.Components.RemoveAt(1);
        store.SaveStatusPage(edited);

        var reloaded = store.FindStatusPage(saved.Id)!;
        Assert.Equal(saved.PublicToken, reloaded.PublicToken);
        var kept = Assert.Single(reloaded.Incidents);
        Assert.Equal(incident.Id, kept.Id);
        Assert.Empty(kept.ComponentIds);
        Assert.NotNull(store.FindStatusPageByPublicToken(saved.PublicToken));
    }

    [Fact]
    public void Resolving_an_incident_closes_it_and_a_switched_off_page_has_no_public_address()
    {
        using var telemetry = new SqliteTelemetryRepository(Path.Combine(_dir, "t.db"));
        using var store = NewStore(telemetry);
        var page = store.SaveStatusPage(new StatusPage { Name = "Internal", PublicEnabled = false });
        var incident = store.PostStatusIncident(page.Id, "VPN down", StatusIncidentImpact.MajorOutage, [], StatusIncidentPhase.Identified, "Provider outage.", null);

        Assert.True(store.AddStatusIncidentUpdate(page.Id, incident.Id, StatusIncidentPhase.Resolved, "Back.", null));

        var reloaded = store.FindStatusPage(page.Id)!;
        Assert.False(reloaded.Incidents[0].IsOpen);
        Assert.Equal(StatusIncidentPhase.Resolved, reloaded.Incidents[0].Phase);
        Assert.Null(store.FindStatusPageByPublicToken(page.PublicToken));
    }

    [Fact]
    public void A_page_with_two_components_of_one_name_is_refused_in_words()
    {
        var problem = StatusPageRules.Validate(new StatusPage
        {
            Name = "Status",
            Components = [new StatusPageComponent { Name = "Mail" }, new StatusPageComponent { Name = "mail" }]
        });
        Assert.Equal("Two components are called 'Mail'.", problem);
    }
}

file sealed class StatusHostEnvironment(string root) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Development";
    public string ApplicationName { get; set; } = "Matmon.Tests";
    public string ContentRootPath { get; set; } = root;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
