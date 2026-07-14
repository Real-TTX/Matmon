using System.Globalization;
using Matmon.Core.Domain;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Matmon.Host.Services;

/// <summary>Renders a customer-facing PDF audit report from <see cref="SummaryReportData"/> (QuestPDF).</summary>
public sealed class AuditReportPdfBuilder
{
    public byte[] Build(SummaryReportData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(36);
                page.DefaultTextStyle(text => text.FontSize(10).FontColor(Colors.Grey.Darken4));

                page.Header().Element(header => ComposeHeader(header, data));
                page.Content().PaddingVertical(12).Element(content => ComposeContent(content, data));
                page.Footer().Element(ComposeFooter);
            });
        }).GeneratePdf();
    }

    private static void ComposeHeader(IContainer container, SummaryReportData data)
    {
        container.Column(outer =>
        {
            outer.Item().Row(row =>
            {
                row.RelativeItem().Column(column =>
                {
                    column.Item().Text("Monitoring Audit Report").FontSize(20).Bold().FontColor(Colors.Blue.Darken2);
                    column.Item().Text(data.WorkspaceName).FontSize(13).SemiBold();
                    if (data.Partner?.PartnerName is { Length: > 0 } partnerName)
                    {
                        column.Item().Text($"Managed by {partnerName}").FontSize(9).FontColor(Colors.Grey.Darken1);
                    }
                    column.Item().PaddingTop(2).Text(text =>
                    {
                        text.Span("Period: ").FontColor(Colors.Grey.Darken1);
                        text.Span($"{data.FromUtc.ToDisplay():g} – {data.ToUtc.ToDisplay():g}");
                        text.Span($"    Generated: {DateTimeOffset.Now:g}").FontColor(Colors.Grey.Medium);
                    });
                });

                // Reseller co-branding: partner logo top-right, when present.
                if (data.Partner?.LogoPng is { Length: > 0 } logo)
                {
                    row.ConstantItem(150).AlignTop().AlignRight().Height(44).Image(logo).FitHeight();
                }
            });

            outer.Item().PaddingTop(6).LineHorizontal(1).LineColor(Colors.Grey.Lighten2);
        });
    }

    private static void ComposeContent(IContainer container, SummaryReportData data)
    {
        container.Column(column =>
        {
            column.Spacing(16);

            column.Item().Row(row =>
            {
                row.Spacing(8);
                SummaryCard(row, "Probes", data.ProbeCount.ToString(CultureInfo.InvariantCulture), Colors.Grey.Darken3);
                SummaryCard(row, "Sensors", $"{data.SensorCount} ({data.PausedSensorCount} paused)", Colors.Grey.Darken3);
                SummaryCard(row, "Errors now", data.ErrorSensorCount.ToString(CultureInfo.InvariantCulture), data.ErrorSensorCount > 0 ? Colors.Red.Darken1 : Colors.Green.Darken1);
                SummaryCard(row, "Warnings now", data.WarningSensorCount.ToString(CultureInfo.InvariantCulture), data.WarningSensorCount > 0 ? Colors.Orange.Darken2 : Colors.Green.Darken1);
                SummaryCard(row, "Active alerts", $"{data.ActiveAlertCount} ({data.AcknowledgedAlertCount} ack)", data.ActiveAlertCount > 0 ? Colors.Red.Darken1 : Colors.Green.Darken1);
            });

            if (data.LowestUptime.Count > 0)
            {
                column.Item().Column(section =>
                {
                    section.Item().PaddingBottom(4).Text("Sensor uptime").FontSize(13).Bold();
                    section.Item().Table(table =>
                    {
                        table.ColumnsDefinition(columns =>
                        {
                            columns.RelativeColumn(5);
                            columns.ConstantColumn(70);
                            columns.ConstantColumn(70);
                            columns.RelativeColumn(2);
                        });

                        table.Header(header =>
                        {
                            HeaderCell(header, "Sensor");
                            HeaderCell(header, "Uptime", TextAlignmentRight: true);
                            HeaderCell(header, "State");
                            HeaderCell(header, "Last value", TextAlignmentRight: true);
                        });

                        foreach (var line in data.LowestUptime)
                        {
                            BodyCell(table).Text(line.Path);
                            BodyCell(table).AlignRight().Text(FormatUptime(line.UptimePercent));
                            BodyCell(table).Text(StateText(line.State)).FontColor(StateColor(line.State));
                            BodyCell(table).AlignRight().Text(FormatValue(line.LastValue, line.Unit));
                        }
                    });
                });
            }

            if (data.RecentEvents.Count > 0)
            {
                column.Item().Column(section =>
                {
                    section.Item().PaddingBottom(4).Text("Recent events").FontSize(13).Bold();
                    section.Item().Table(table =>
                    {
                        table.ColumnsDefinition(columns =>
                        {
                            columns.ConstantColumn(110);
                            columns.ConstantColumn(90);
                            columns.RelativeColumn(4);
                        });

                        table.Header(header =>
                        {
                            HeaderCell(header, "Time");
                            HeaderCell(header, "Kind");
                            HeaderCell(header, "Element / message");
                        });

                        foreach (var line in data.RecentEvents)
                        {
                            BodyCell(table).Text(line.TimestampUtc.ToDisplay().ToString("g", CultureInfo.CurrentCulture));
                            BodyCell(table).Text(line.Kind);
                            BodyCell(table).Text($"{line.Path} - {line.Message}");
                        }
                    });
                });
            }
        });
    }

    private static void ComposeFooter(IContainer container)
    {
        container.Row(row =>
        {
            row.RelativeItem().Text("Generated by Matmon").FontSize(8).FontColor(Colors.Grey.Medium);
            row.RelativeItem().AlignRight().Text(text =>
            {
                text.DefaultTextStyle(style => style.FontSize(8).FontColor(Colors.Grey.Medium));
                text.Span("Page ");
                text.CurrentPageNumber();
                text.Span(" / ");
                text.TotalPages();
            });
        });
    }

    private static void SummaryCard(RowDescriptor row, string label, string value, string valueColor)
    {
        row.RelativeItem().Border(1).BorderColor(Colors.Grey.Lighten2).Padding(8).Column(column =>
        {
            column.Item().Text(label).FontSize(8).FontColor(Colors.Grey.Darken1);
            column.Item().Text(value).FontSize(13).SemiBold().FontColor(valueColor);
        });
    }

    private static void HeaderCell(TableCellDescriptor header, string text, bool TextAlignmentRight = false)
    {
        var cell = header.Cell().BorderBottom(1).BorderColor(Colors.Grey.Lighten1).PaddingVertical(4).PaddingHorizontal(4);
        var content = TextAlignmentRight ? cell.AlignRight() : cell;
        content.Text(text).SemiBold().FontColor(Colors.Grey.Darken2);
    }

    private static IContainer BodyCell(TableDescriptor table) =>
        table.Cell().BorderBottom(1).BorderColor(Colors.Grey.Lighten3).PaddingVertical(3).PaddingHorizontal(4);

    private static string FormatUptime(double? uptime) =>
        uptime.HasValue ? uptime.Value.ToString("0.#", CultureInfo.InvariantCulture) + "%" : "n/a";

    private static string FormatValue(double? value, string? unit)
    {
        if (!value.HasValue)
        {
            return "-";
        }

        var text = value.Value.ToString("0.###", CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(unit) ? text : $"{text} {unit}";
    }

    private static string StateText(SensorState state) => state switch
    {
        SensorState.Critical => "Critical",
        SensorState.Warning => "Warning",
        SensorState.Healthy => "Healthy",
        SensorState.Paused => "Paused",
        SensorState.Disabled => "Disabled",
        _ => "Unknown"
    };

    private static string StateColor(SensorState state) => state switch
    {
        SensorState.Critical => Colors.Red.Darken1,
        SensorState.Warning => Colors.Orange.Darken2,
        SensorState.Healthy => Colors.Green.Darken1,
        _ => Colors.Grey.Darken1
    };
}
