using Matmon.Core.Domain;
using Matmon.Host.Services;

namespace Matmon.Tests;

public class AuditReportPdfBuilderTests
{
    static AuditReportPdfBuilderTests()
    {
        // The license is normally set in Program.cs; the test process doesn't run it.
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
    }

    private static SummaryReportData Sample(bool withRows) => new(
        WorkspaceName: "HQ",
        FromUtc: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        ToUtc: new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero),
        ProbeCount: 2,
        SensorCount: 5,
        PausedSensorCount: 1,
        ActiveAlertCount: 1,
        AcknowledgedAlertCount: 0,
        ErrorSensorCount: 1,
        WarningSensorCount: 0,
        LowestUptime: withRows
            ? [new SummaryReportSensorLine("HQ / Office / NAS", SensorState.Critical, 95.0, 80.0, "%", 288)]
            : [],
        RecentEvents: withRows
            ? [new SummaryReportEventLine(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero), "AlertRaised", "HQ / Office / NAS", "disk full")]
            : []);

    [Fact]
    public void Build_produces_a_valid_pdf()
    {
        var pdf = new AuditReportPdfBuilder().Build(Sample(withRows: true));

        Assert.True(pdf.Length > 500);
        // PDF files start with the "%PDF" magic bytes.
        Assert.Equal(new byte[] { 0x25, 0x50, 0x44, 0x46 }, pdf[..4]);
    }

    [Fact]
    public void Build_handles_empty_data()
    {
        var pdf = new AuditReportPdfBuilder().Build(Sample(withRows: false));

        Assert.True(pdf.Length > 100);
        Assert.Equal(new byte[] { 0x25, 0x50, 0x44, 0x46 }, pdf[..4]);
    }
}
