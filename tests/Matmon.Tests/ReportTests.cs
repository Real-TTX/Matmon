using System.Text;
using Matmon.Core.Domain;
using Matmon.Core.Telemetry;
using Matmon.Host.Services;
using Matmon.Host.Services.Reports;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

/// <summary>
/// Reports: the periods and the schedule (whole local days, sent once per slot, never at once for a slot that passed
/// before the report existed), the outage arithmetic over the status history, the CSV (and its guard against a sensor
/// name a spreadsheet would run as a formula), the branded e-mail and PDF (a colour or a link from the cloud is
/// re-validated), saving and backing up, and the collector against a real store.
/// </summary>
public sealed class ReportTests : IDisposable
{
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
    private static readonly Guid SensorId = new("44444444-4444-4444-4444-444444444444");
    private static readonly Guid HostId = new("55555555-5555-5555-5555-555555555555");
    private const string OnePixelPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "matmon-report-" + Guid.NewGuid().ToString("N"));

    public ReportTests()
    {
        Directory.CreateDirectory(_dir);
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    // --- periods ------------------------------------------------------------------------------------------

    [Fact]
    public void Previous_month_is_the_whole_calendar_month_in_the_local_zone()
    {
        var now = new DateTimeOffset(2026, 10, 31, 9, 0, 0, TimeSpan.Zero);
        var window = ReportRules.Resolve(ReportPeriod.PreviousMonth, now, Berlin);

        Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.FromHours(2)), window.FromUtc);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.FromHours(2)), window.ToUtc);
        Assert.Equal("September 2026", window.Label);
    }

    [Fact]
    public void Previous_week_runs_monday_to_monday_and_yesterday_is_one_local_day()
    {
        var now = new DateTimeOffset(2026, 10, 29, 10, 0, 0, TimeSpan.Zero); // Thursday
        var week = ReportRules.Resolve(ReportPeriod.PreviousWeek, now, Berlin);
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.FromHours(2)), week.FromUtc);
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.FromHours(1)), week.ToUtc); // DST ended on the 25th
        Assert.StartsWith("Week 43/2026", week.Label);

        var yesterday = ReportRules.Resolve(ReportPeriod.Yesterday, now, Berlin);
        Assert.Equal(TimeSpan.FromHours(24), yesterday.Length);
    }

    [Fact]
    public void A_custom_period_never_reaches_into_the_future()
    {
        var now = new DateTimeOffset(2026, 10, 31, 9, 0, 0, TimeSpan.Zero);
        var window = ReportRules.Resolve(ReportPeriod.Custom, now, Berlin, now.AddDays(-3), now.AddDays(5));
        Assert.Equal(now, window.ToUtc);
    }

    // --- schedule -----------------------------------------------------------------------------------------

    [Fact]
    public void A_report_saved_after_todays_slot_waits_for_the_next_one()
    {
        var report = Scheduled(ReportSchedule.Daily, created: new DateTimeOffset(2026, 10, 31, 9, 0, 0, TimeSpan.Zero)); // 10:00 Berlin
        Assert.False(ReportRules.IsDue(report, new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero), Berlin));
        Assert.True(ReportRules.IsDue(report, new DateTimeOffset(2026, 11, 1, 6, 1, 0, TimeSpan.Zero), Berlin)); // 07:01 Berlin

        report.LastSentUtc = new DateTimeOffset(2026, 11, 1, 6, 1, 0, TimeSpan.Zero);
        Assert.False(ReportRules.IsDue(report, new DateTimeOffset(2026, 11, 1, 20, 0, 0, TimeSpan.Zero), Berlin));
    }

    [Fact]
    public void A_monthly_report_goes_out_on_its_day_and_names_the_next()
    {
        var report = Scheduled(ReportSchedule.Monthly, created: new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));
        report.DayOfMonth = 1;
        var slot = ReportRules.MostRecentSlot(report, new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero), Berlin);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 7, 0, 0, TimeSpan.FromHours(2)), slot);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 7, 0, 0, TimeSpan.FromHours(1)), ReportRules.NextSlot(report, new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero), Berlin));
        Assert.True(ReportRules.IsDue(report, new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero), Berlin));
    }

    [Fact]
    public void Validation_refuses_what_cannot_work()
    {
        var report = Scheduled(ReportSchedule.Weekly, DateTimeOffset.UtcNow);
        Assert.Null(ReportRules.Validate(report));

        report.Recipients = string.Empty;
        Assert.Contains("recipient", ReportRules.Validate(report));

        report.Recipients = "a@example.com";
        report.Period = ReportPeriod.Custom;
        report.CustomFromUtc = DateTimeOffset.UtcNow.AddDays(-2);
        report.CustomToUtc = DateTimeOffset.UtcNow;
        Assert.Contains("moves along", ReportRules.Validate(report));

        report.Schedule = ReportSchedule.None;
        report.SlaTargetPercent = 120;
        Assert.Contains("percentage", ReportRules.Validate(report));

        report.SlaTargetPercent = null;
        report.Sections = ReportSections.None;
        Assert.Contains("at least one", ReportRules.Validate(report));
    }

    // --- outages ------------------------------------------------------------------------------------------

    [Fact]
    public void Outages_are_the_error_stretches_clipped_to_the_period()
    {
        var t0 = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var history = new[]
        {
            Change(t0.AddHours(-5), SensorState.Critical),   // started before the period
            Change(t0.AddHours(1), SensorState.Healthy),
            Change(t0.AddHours(3), SensorState.Warning),
            Change(t0.AddHours(4), SensorState.Critical),
            Change(t0.AddHours(4.5), SensorState.Healthy)
        };

        var spans = ReportMath.StateSpans(history, t0, t0.AddHours(10), t0.AddDays(1));
        var outages = ReportMath.Outages(spans, SensorId, "Ping", "Office / Ping", t0.AddDays(1));

        Assert.Equal(2, outages.Count);
        Assert.Equal(t0, outages[0].StartUtc);                 // clipped
        Assert.Equal(TimeSpan.FromHours(1), outages[0].Duration);
        Assert.Equal(TimeSpan.FromMinutes(30), outages[1].Duration);
        Assert.All(outages, outage => Assert.False(outage.Ongoing));
        Assert.Equal(TimeSpan.FromHours(1.5), ReportMath.TimeIn(spans, SensorState.Critical));
        Assert.Equal(TimeSpan.FromHours(1), ReportMath.TimeIn(spans, SensorState.Warning));
    }

    [Fact]
    public void An_outage_still_going_at_the_end_is_ongoing_and_a_pause_is_no_outage()
    {
        var t0 = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var now = t0.AddHours(6);
        var history = new[]
        {
            Change(t0, SensorState.Critical),
            Entry(t0.AddHours(1), SensorStatusKind.Paused),
            Entry(t0.AddHours(3), SensorStatusKind.Resumed),
            Change(t0.AddHours(3.5), SensorState.Healthy),
            Change(t0.AddHours(5), SensorState.Critical)
        };

        var spans = ReportMath.StateSpans(history, t0, t0.AddDays(1), now);
        Assert.Equal(TimeSpan.FromHours(2), ReportMath.TimeIn(spans, SensorState.Paused));
        // Critical: 0-1 h, then (resumed into Critical) 3-3.5 h, then 5-6 h (now).
        Assert.Equal(TimeSpan.FromHours(2.5), ReportMath.TimeIn(spans, SensorState.Critical));
        var outages = ReportMath.Outages(spans, SensorId, "Ping", "Ping", now);
        Assert.True(outages[^1].Ongoing);
        Assert.False(outages[0].Ongoing);
    }

    [Fact]
    public void Without_any_state_change_the_current_state_holds_and_nothing_is_guessed_otherwise()
    {
        var t0 = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Empty(ReportMath.StateSpans([], t0, t0.AddHours(2), t0.AddHours(3)));
        var assumed = ReportMath.StateSpans([], t0, t0.AddHours(2), t0.AddHours(3), SensorState.Healthy);
        Assert.Equal(TimeSpan.FromHours(2), ReportMath.TimeIn(assumed, SensorState.Healthy));

        // A first change inside the period: the time before it is left out.
        var spans = ReportMath.StateSpans([Change(t0.AddHours(1), SensorState.Healthy)], t0, t0.AddHours(2), t0.AddHours(3), SensorState.Critical);
        Assert.Equal(t0.AddHours(1), spans[0].StartUtc);
    }

    [Fact]
    public void Daily_totals_give_availability_for_long_periods_with_a_warning_counting_as_up()
    {
        var day = new DateOnly(2026, 10, 1);
        var states = new[]
        {
            new SensorDailyState(SensorId, day, 90, 5, 5),
            new SensorDailyState(SensorId, day.AddDays(1), 100, 0, 0),
            new SensorDailyState(SensorId, day.AddDays(5), 0, 0, 100) // outside
        };

        var summary = ReportMath.FromDailyStates(states, day, day.AddDays(2));
        Assert.Equal(97.5, summary.Percent!.Value, 3);
        Assert.Null(ReportMath.FromDailyStates([], day, day.AddDays(1)).Percent);
    }

    // --- CSV / SVG / e-mail / PDF ------------------------------------------------------------------------

    [Fact]
    public void Csv_quotes_fields_and_defuses_formulas_but_keeps_negative_numbers()
    {
        Assert.Equal("\"a,b\"", ReportCsv.Field("a,b"));
        Assert.Equal("\"say \"\"hi\"\"\"", ReportCsv.Field("say \"hi\""));
        Assert.Equal("'=HYPERLINK(\"x\")", ReportCsv.Field("=HYPERLINK(\"x\")").Trim('"').Replace("\"\"", "\""));
        Assert.Equal("'@SUM(A1)", ReportCsv.Field("@SUM(A1)"));
        Assert.Equal("-12.5", ReportCsv.Field("-12.5"));

        var bytes = ReportCsv.Sensors(SampleData(branding: null));
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        var text = Encoding.UTF8.GetString(bytes[3..]);
        Assert.StartsWith("Sensor,Path,Machine", text);
        Assert.Contains("Ping NAS", text);
    }

    [Fact]
    public void The_chart_breaks_its_line_over_a_gap_and_draws_the_state_band()
    {
        var t0 = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var points = Enumerable.Range(0, 10).Select(i => (t0.AddMinutes(i * 5), 10d + i))
            .Concat(Enumerable.Range(0, 10).Select(i => (t0.AddHours(10).AddMinutes(i * 5), 20d)))
            .ToArray();
        var chart = new ReportChart(SensorId, "Ping", "NAS", "ms", points, [new ReportStateSpan(t0, t0.AddHours(12), SensorState.Healthy)], false);

        var svg = ReportChartSvg.Render(chart, t0, t0.AddHours(12), "not-a-colour");
        Assert.Equal(2, CountOf(svg, "<polyline"));
        Assert.Contains("#16A34A", svg);
        Assert.Contains("#2563EB", svg); // the invalid accent fell back
        Assert.DoesNotContain("not-a-colour", svg);
    }

    [Fact]
    public void The_email_carries_the_partner_brand_and_drops_what_does_not_validate()
    {
        var branding = new SummaryReportBranding("Example IT", Convert.FromBase64String(OnePixelPng), "image/png",
            "red;background:url(x)", "javascript:alert(1)", "NetGuard", "We watch it", "#123ABC");
        var mail = ReportEmail.Build(SampleData(branding), "The full report is attached.");

        Assert.Contains("Managed by Example IT", mail.HtmlBody);
        Assert.Contains("NetGuard &#183; powered by Matmon", mail.HtmlBody);
        Assert.Contains("#123ABC", mail.HtmlBody);
        Assert.Contains("data:image/png;base64,", mail.HtmlBody);
        Assert.DoesNotContain("javascript:", mail.HtmlBody);
        Assert.DoesNotContain("url(x)", mail.HtmlBody);
        Assert.Contains("&lt;script&gt;", mail.HtmlBody); // the scope text is encoded
        Assert.Contains("99.000 %", mail.Subject);
    }

    [Fact]
    public void The_pdf_is_a_real_pdf_with_charts_and_branding_and_without_a_partner()
    {
        // Without a partner logo the reports carry the Matmon logo - embedded, so it is there in any process.
        Assert.Contains("<svg", ReportPdfChrome.MatmonLogoSvg);
        var builder = new ReportPdfBuilder();
        var branded = builder.Build(SampleData(new SummaryReportBranding("Example IT", Convert.FromBase64String(OnePixelPng), "image/png", "#AA0000", "https://example.com/support", "NetGuard", "We watch it", "#00AA00")));
        var plain = builder.Build(SampleData(branding: null));
        var notRaster = builder.Build(SampleData(new SummaryReportBranding("X", Encoding.UTF8.GetBytes("<svg/>"), "image/svg+xml", null, null)));

        foreach (var pdf in new[] { branded, plain, notRaster })
        {
            Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
        }

        var table = builder.BuildTable("Checks - Ping", ["Office / Ping"], [("Time", 1f), ("State", 1f)], [["2026-10-01 10:00", "Error"]], null, 1);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(table, 0, 4));
    }

    // --- store + collector --------------------------------------------------------------------------------

    [Fact]
    public void Reports_are_saved_validated_and_carried_by_the_notifications_backup()
    {
        var (store, telemetry) = Boot();
        using (telemetry) using (store)
        {
            Assert.Throws<InvalidOperationException>(() => store.SaveReport(new ReportDefinition { Name = "No target" }));
            Assert.Throws<InvalidOperationException>(() => store.SaveReport(new ReportDefinition { Name = "Gone", TargetToken = Guid.NewGuid().ToString() }));

            var saved = store.SaveReport(new ReportDefinition { Name = " Monthly ", TargetToken = HostId.ToString(), Schedule = ReportSchedule.Monthly, Recipients = "a@example.com" });
            Assert.Equal("Monthly", saved.Name);
            store.MarkReportSent(saved.Id, DateTimeOffset.UtcNow, failed: true, "Not sent: no sender");
            Assert.True(store.FindReport(saved.Id)!.LastSendFailed);

            // Editing keeps the bookkeeping; changing the schedule restarts it.
            var edited = store.FindReport(saved.Id)!;
            edited.Note = "Hello";
            store.SaveReport(edited);
            Assert.NotNull(store.FindReport(saved.Id)!.LastSentUtc);
            edited.Schedule = ReportSchedule.Weekly;
            store.SaveReport(edited);
            Assert.Null(store.FindReport(saved.Id)!.LastSentUtc);

            var withNotifications = store.CreateBackupBytes(WorkspaceBackupSection.Notifications);
            var withoutNotifications = store.CreateBackupBytes(WorkspaceBackupSection.Topology);
            Assert.Contains("\"Monthly\"", Encoding.UTF8.GetString(withNotifications));
            Assert.DoesNotContain("\"Monthly\"", Encoding.UTF8.GetString(withoutNotifications));

            Assert.True(store.DeleteReport(saved.Id));
            Assert.Empty(store.GetReports());
            store.RestoreBackupBytes(withNotifications, WorkspaceBackupSection.Notifications);
            Assert.Single(store.GetReports());
        }
    }

    [Fact]
    public void The_collector_counts_an_hour_of_errors_as_one_outage_of_an_hour()
    {
        var (store, telemetry) = Boot();
        using (telemetry) using (store)
        {
            var now = DateTimeOffset.UtcNow;
            var start = now.AddHours(-3);
            for (var i = 0; i < 18; i++)
            {
                var result = i is >= 6 and < 12
                    ? SensorExecutionResult.Critical(TimeSpan.FromMilliseconds(5), "Timed out")
                    : SensorExecutionResult.Healthy(TimeSpan.FromMilliseconds(5), "OK", 12 + i) with
                    {
                        Channels = [new SensorChannelValue { Key = "latency", Label = "Latency", Value = 12 + i, Unit = "ms", IsDefault = true }]
                    };
                store.RecordSensorObservation(SensorId, result, start.AddMinutes(i * 10));
            }

            var collector = new ReportDataCollector(store);
            var data = collector.Collect(new ReportRequest(HostId.ToString(), new ReportWindow(start, now, "Last 3 hours"), "Office", SlaTargetPercent: 99.9), now);

            Assert.Equal(1, data.SensorCount);
            var row = Assert.Single(data.Sensors);
            Assert.Equal("NAS", row.Machine);
            Assert.Equal(1, row.OutageCount);
            Assert.Equal(TimeSpan.FromHours(1), row.Downtime);
            Assert.Equal(12d / 18 * 100, row.UptimePercent!.Value, 3);
            Assert.True(row.BelowTarget);
            Assert.Equal("Timed out", data.Outages[0].Message);
            var chart = Assert.Single(data.Charts);
            Assert.Equal(12, chart.Points.Count); // the failed checks carry no reading and are not drawn as 0
            Assert.Equal("ms", row.Channel!.Unit);
            Assert.Equal("Primary Probe / NAS", data.Scope);

            // Everything renders.
            Assert.Equal("%PDF", Encoding.ASCII.GetString(new ReportPdfBuilder().Build(data), 0, 4));
            Assert.Contains("Ping NAS", Encoding.UTF8.GetString(ReportCsv.Sensors(data)));
            Assert.Contains("Timed out", Encoding.UTF8.GetString(ReportCsv.Outages(data)));

            // A tag covers what carries it; nothing tagged = nothing reported.
            Assert.Equal(0, collector.Collect(new ReportRequest("tag:none", new ReportWindow(start, now, "x")), now).SensorCount);
        }
    }

    // --- helpers ------------------------------------------------------------------------------------------

    private static ReportDefinition Scheduled(ReportSchedule schedule, DateTimeOffset created) => new()
    {
        Name = "Report",
        TargetToken = "tag:office",
        Period = ReportRules.SuggestedPeriod(schedule),
        Schedule = schedule,
        HourOfDay = 7,
        DayOfWeek = DayOfWeek.Monday,
        Recipients = "it@example.com",
        CreatedUtc = created
    };

    private static SensorStatusEntry Change(DateTimeOffset at, SensorState to) =>
        new(at, SensorStatusKind.StateChanged, SensorStatusCategory.States, null, to, null, string.Empty);

    private static SensorStatusEntry Entry(DateTimeOffset at, SensorStatusKind kind) =>
        new(at, kind, SensorStatusHistory.CategoryOf(kind), null, null, null, string.Empty);

    private static int CountOf(string text, string part) =>
        (text.Length - text.Replace(part, string.Empty).Length) / part.Length;

    private static AvailabilityReportData SampleData(SummaryReportBranding? branding)
    {
        var t0 = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var to = t0.AddDays(7);
        var points = Enumerable.Range(0, 200).Select(i => (t0.AddMinutes(i * 50), 10d + i % 17)).ToArray();
        var spans = new[] { new ReportStateSpan(t0, t0.AddDays(3), SensorState.Healthy), new ReportStateSpan(t0.AddDays(3), t0.AddDays(3).AddHours(2), SensorState.Critical), new ReportStateSpan(t0.AddDays(3).AddHours(2), to, SensorState.Healthy) };
        return new AvailabilityReportData
        {
            Title = "Weekly availability",
            Scope = "Office <script>",
            PreparedFor = "Example GmbH",
            Note = "Router replaced on Thursday.",
            Window = new ReportWindow(t0, to, "Week 40/2026"),
            GeneratedUtc = to,
            Zone = Berlin,
            SlaTargetPercent = 99.5,
            Overall = new UptimeSummary(990, 0, 10, 1000, 99.0),
            TotalDowntime = TimeSpan.FromHours(2),
            SensorCount = 1,
            MachineCount = 1,
            Sensors =
            [
                new ReportSensorRow(SensorId, "Ping NAS", "Office / NAS / Ping NAS", "NAS", "Ping", SensorState.Healthy, 99.0, 1000,
                    TimeSpan.FromHours(2), TimeSpan.Zero, 1, TimeSpan.FromHours(2), new ReportChannelStats("Latency", "ms", 12.3, 1.2, 80), true)
            ],
            Outages = [new ReportOutage(SensorId, "Ping NAS", "Office / NAS / Ping NAS", t0.AddDays(3), t0.AddDays(3).AddHours(2), false, "Timed out")],
            Alerts = [new ReportAlertRow(t0.AddDays(3), t0.AddDays(3).AddHours(2), "Office / NAS / Ping NAS", SensorState.Critical, "Timed out", "matthias", false)],
            Charts = [new ReportChart(SensorId, "Ping NAS", "NAS · Latency", "ms", points, spans, false)],
            DataNotes = ["Downtime from state changes."],
            Branding = branding
        };
    }

    private (InMemoryMonitoringWorkspaceStore Store, SqliteTelemetryRepository Telemetry) Boot()
    {
        var path = Path.Combine(_dir, $"ws-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, $$"""
            {
              "schemaVersion": {{InMemoryMonitoringWorkspaceStore.CurrentSchemaVersion}},
              "rootProbe": { "$kind": "probe", "probeId": "primary", "id": "11111111-1111-1111-1111-111111111111", "name": "Primary Probe", "children": [
                { "$kind": "host", "id": "{{HostId}}", "parentId": "11111111-1111-1111-1111-111111111111", "name": "NAS", "address": "10.0.0.5", "children": [
                  { "$kind": "sensor", "id": "{{SensorId}}", "parentId": "{{HostId}}", "name": "Ping NAS", "sensorTypeKey": "ping", "target": "10.0.0.5" }
                ] }
              ] }
            }
            """);

        var telemetry = new SqliteTelemetryRepository(Path.Combine(_dir, $"t-{Guid.NewGuid():N}.db"));
        var store = new InMemoryMonitoringWorkspaceStore(
            new ReportTestHostEnvironment(_dir),
            new MatmonRuntimeOptions { WorkspacePath = path },
            new MatmonAuthOptions(),
            new EphemeralDataProtectionProvider(),
            telemetry,
            NullLogger<InMemoryMonitoringWorkspaceStore>.Instance);
        return (store, telemetry);
    }
}

file sealed class ReportTestHostEnvironment(string contentRoot) : IHostEnvironment
{
    public string ApplicationName { get; set; } = "Matmon.Tests";
    public string EnvironmentName { get; set; } = "Development";
    public string ContentRootPath { get; set; } = contentRoot;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
