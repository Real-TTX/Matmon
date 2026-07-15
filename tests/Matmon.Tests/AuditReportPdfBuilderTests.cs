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

    // A real, minimal 1x1 PNG so the logo branch actually decodes through QuestPDF/SkiaSharp.
    private static readonly byte[] OnePxPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR4nGNgAAIAAAUAAen63NgAAAAASUVORK5CYII=");

    [Fact]
    public void Build_with_partner_branding_stays_a_valid_pdf()
    {
        var data = Sample(withRows: true) with
        {
            Partner = new SummaryReportBranding("ACME MSP", OnePxPng, "image/png", "#AABBCC", "https://acme.example/support", "ACME Monitor")
        };

        var pdf = new AuditReportPdfBuilder().Build(data);

        Assert.True(pdf.Length > 500);
        Assert.Equal(new byte[] { 0x25, 0x50, 0x44, 0x46 }, pdf[..4]);
    }

    // A wide (120x16, 7.5:1) PNG - a real partner logo is usually wide. At a fixed header height a wide logo
    // scaled by height alone overflows the column width and threw a QuestPDF layout exception; this pins the fix.
    private static readonly byte[] WidePng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAHgAAAAQCAYAAADdw7vlAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAABESURBVFhH7dExEQAgEMCw14U2LKMBdiTkOmTp2tnr3LjmD7E0GNdgXINxDcY1GNdgXINxDcY1GNdgXINxDcY1GNdg3AMZuMAeQjztiwAAAABJRU5ErkJggg==");

    [Fact]
    public void Build_with_a_wide_partner_logo_stays_a_valid_pdf()
    {
        var data = Sample(withRows: true) with
        {
            Partner = new SummaryReportBranding("Very Wide Partner Co", WidePng, "image/png", "#7C3AED", "https://partner.example/support")
        };

        var pdf = new AuditReportPdfBuilder().Build(data);

        Assert.Equal(new byte[] { 0x25, 0x50, 0x44, 0x46 }, pdf[..4]);
    }

    [Fact]
    public void Build_skips_a_non_raster_logo_without_crashing()
    {
        // A non-PNG/JPEG logo must be dropped by the magic-byte guard - handing an SVG/garbage blob to the
        // raster-only renderer would throw at GeneratePdf() and take down the whole report.
        var data = Sample(withRows: false) with
        {
            Partner = new SummaryReportBranding("ACME", [0x00, 0x01, 0x02, 0x03], "image/svg+xml", "#AABBCC", null)
        };

        var pdf = new AuditReportPdfBuilder().Build(data);

        Assert.Equal(new byte[] { 0x25, 0x50, 0x44, 0x46 }, pdf[..4]);
    }
}
