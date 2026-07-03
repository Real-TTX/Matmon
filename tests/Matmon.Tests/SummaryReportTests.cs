using Matmon.Core.Domain;

namespace Matmon.Tests;

public class SummaryReportScheduleTests
{
    private static readonly TimeSpan Utc = TimeSpan.Zero;

    private static SummaryReportSettings Daily(int hour, DateTimeOffset? lastSent = null) => new()
    {
        Enabled = true,
        Cadence = SummaryReportCadence.Daily,
        HourOfDay = hour,
        LastSentUtc = lastSent
    };

    [Fact]
    public void Disabled_is_never_due()
    {
        var settings = Daily(7);
        settings.Enabled = false;
        Assert.False(SummaryReportSchedule.IsDue(settings, new DateTimeOffset(2026, 1, 1, 8, 0, 0, Utc)));
    }

    [Fact]
    public void Enabled_but_never_sent_is_due()
    {
        // A past slot always exists, so an enabled report that has never been sent fires at the next tick.
        Assert.True(SummaryReportSchedule.IsDue(Daily(7), new DateTimeOffset(2026, 1, 1, 8, 0, 0, Utc)));
    }

    [Fact]
    public void Daily_not_due_again_same_day_after_sending()
    {
        var sent = new DateTimeOffset(2026, 1, 1, 7, 0, 30, Utc);
        Assert.False(SummaryReportSchedule.IsDue(Daily(7, sent), new DateTimeOffset(2026, 1, 1, 20, 0, 0, Utc)));
    }

    [Fact]
    public void Daily_due_next_day_after_sending()
    {
        var sent = new DateTimeOffset(2026, 1, 1, 7, 0, 30, Utc);
        Assert.True(SummaryReportSchedule.IsDue(Daily(7, sent), new DateTimeOffset(2026, 1, 2, 7, 5, 0, Utc)));
    }

    [Fact]
    public void Weekly_not_due_again_within_the_same_week()
    {
        // 2026-01-05 is a Monday.
        var settings = new SummaryReportSettings
        {
            Enabled = true,
            Cadence = SummaryReportCadence.Weekly,
            HourOfDay = 6,
            DayOfWeek = DayOfWeek.Monday,
            LastSentUtc = new DateTimeOffset(2026, 1, 5, 6, 0, 10, Utc)
        };

        Assert.False(SummaryReportSchedule.IsDue(settings, new DateTimeOffset(2026, 1, 8, 12, 0, 0, Utc)));  // Thursday same week
        Assert.True(SummaryReportSchedule.IsDue(settings, new DateTimeOffset(2026, 1, 12, 6, 5, 0, Utc)));   // next Monday
    }

    [Fact]
    public void MostRecentSlot_weekly_lands_on_configured_weekday_and_hour()
    {
        var settings = new SummaryReportSettings
        {
            Enabled = true,
            Cadence = SummaryReportCadence.Weekly,
            HourOfDay = 6,
            DayOfWeek = DayOfWeek.Monday
        };

        // From Thursday 2026-01-08, the most recent Monday-06:00 slot is Monday 2026-01-05 06:00.
        var slot = SummaryReportSchedule.MostRecentSlot(settings, new DateTimeOffset(2026, 1, 8, 12, 0, 0, Utc));
        Assert.Equal(new DateTimeOffset(2026, 1, 5, 6, 0, 0, Utc), slot);
    }
}

public class SummaryReportBuilderTests
{
    [Fact]
    public void Build_produces_subject_text_and_html_with_key_figures()
    {
        var data = new SummaryReportData(
            WorkspaceName: "HQ",
            FromUtc: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            ToUtc: new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero),
            ProbeCount: 2,
            SensorCount: 10,
            PausedSensorCount: 1,
            ActiveAlertCount: 3,
            AcknowledgedAlertCount: 1,
            ErrorSensorCount: 2,
            WarningSensorCount: 1,
            LowestUptime:
            [
                new SummaryReportSensorLine("HQ / Office / NAS", SensorState.Critical, 92.5, 87.4, "%", 288)
            ],
            RecentEvents:
            [
                new SummaryReportEventLine(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero), "AlertRaised", "HQ / Office / NAS", "disk full")
            ]);

        var report = SummaryReportBuilder.Build(data);

        Assert.Contains("HQ", report.Subject);
        Assert.Contains("3 active", report.Subject);
        Assert.Contains("NAS", report.TextBody);
        Assert.Contains("92.5%", report.TextBody);
        Assert.Contains("<table", report.HtmlBody);
        Assert.Contains("NAS", report.HtmlBody);
        Assert.Contains("disk full", report.HtmlBody);
    }

    [Fact]
    public void Build_html_encodes_names()
    {
        var data = new SummaryReportData(
            "A<b>C", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0, 0, 0, 0, 0, 0, 0, [], []);

        var report = SummaryReportBuilder.Build(data);

        Assert.DoesNotContain("A<b>C", report.HtmlBody);
        Assert.Contains("A&lt;b&gt;C", report.HtmlBody);
    }
}
