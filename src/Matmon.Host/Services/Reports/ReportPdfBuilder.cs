using System.Globalization;
using Matmon.Core.Domain;
using Matmon.Core.Telemetry;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Matmon.Host.Services.Reports;

/// <summary>
/// The availability report as a PDF (QuestPDF): the summary figures, the sensors with their availability, downtime,
/// outages and main reading, the outages and alerts of the period, a chart per sensor (worst first) and what the numbers
/// are made from - in the managing partner's branding (<see cref="ReportPdfChrome"/>).
/// </summary>
public sealed class ReportPdfBuilder
{
    public byte[] Build(AvailabilityReportData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var accent = ReportPdfChrome.Accent(data.Branding);
        var sectionColor = ReportPdfChrome.SectionColor(data.Branding);
        var period = $"{data.Window.Label} · {data.LocalTime(data.Window.FromUtc)} – {data.LocalTime(data.Window.ToUtc)}";

        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(36);
                page.DefaultTextStyle(text => text.FontSize(9.5f).FontColor(Colors.Grey.Darken4));

                page.Header().ShowOnce().Element(header => ReportPdfChrome.Header(header, data.Title,
                [
                    data.Scope,
                    string.IsNullOrWhiteSpace(data.PreparedFor) ? null! : $"Prepared for {data.PreparedFor}",
                    period,
                    $"Generated {data.LocalTime(data.GeneratedUtc)} ({data.Zone.Id})"
                ], data.Branding));
                page.Content().PaddingVertical(10).Element(content => Content(content, data, accent, sectionColor));
                page.Footer().Element(footer => ReportPdfChrome.Footer(footer, data.Branding, data.Window.Label));
            });
        }).GeneratePdf();
    }

    /// <summary>A plain list as a PDF in the same branded frame - a sensor's checks, its status history, its alerts. A
    /// column named in <paramref name="stateColumn"/> is coloured by the state label it holds.</summary>
    public byte[] BuildTable(
        string title,
        IReadOnlyList<string> lines,
        IReadOnlyList<(string Header, float Weight)> columns,
        IReadOnlyList<string[]> rows,
        SummaryReportBranding? branding,
        int? stateColumn = null,
        string? footnote = null)
    {
        var sectionColor = ReportPdfChrome.SectionColor(branding);
        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(30);
                page.DefaultTextStyle(text => text.FontSize(8.5f).FontColor(Colors.Grey.Darken4));
                page.Header().ShowOnce().Element(header => ReportPdfChrome.Header(header, title, lines, branding));
                page.Content().PaddingVertical(10).Column(column =>
                {
                    column.Spacing(8);
                    if (rows.Count == 0)
                    {
                        column.Item().Text("Nothing to list.").Italic().FontColor(Colors.Grey.Darken1);
                    }
                    else
                    {
                        column.Item().Table(table =>
                        {
                            table.ColumnsDefinition(definition =>
                            {
                                foreach (var (_, weight) in columns)
                                {
                                    definition.RelativeColumn(weight);
                                }
                            });
                            table.Header(header =>
                            {
                                foreach (var (name, _) in columns)
                                {
                                    ReportPdfChrome.HeaderCell(header, name);
                                }
                            });
                            foreach (var row in rows)
                            {
                                for (var i = 0; i < columns.Count; i++)
                                {
                                    var value = i < row.Length ? row[i] : string.Empty;
                                    var text = ReportPdfChrome.BodyCell(table).Text(Shorten(value, 400) ?? string.Empty);
                                    if (i == stateColumn)
                                    {
                                        text.FontColor(StateColorOf(value)).SemiBold();
                                    }
                                }
                            }
                        });
                    }

                    if (!string.IsNullOrWhiteSpace(footnote))
                    {
                        column.Item().Text(footnote).FontSize(7.5f).FontColor(sectionColor == Colors.Grey.Darken4 ? Colors.Grey.Darken1 : sectionColor);
                    }
                });
                page.Footer().Element(footer => ReportPdfChrome.Footer(footer, branding, title));
            });
        }).GeneratePdf();
    }

    private static string StateColorOf(string label) =>
        Enum.GetValues<SensorState>().FirstOrDefault(state => string.Equals(MonitoringStatePresentation.Label(state), label, StringComparison.OrdinalIgnoreCase)) is var state
        && string.Equals(MonitoringStatePresentation.Label(state), label, StringComparison.OrdinalIgnoreCase)
            ? ReportPdfChrome.StateColor(state)
            : Colors.Grey.Darken4;

    private static void Content(IContainer container, AvailabilityReportData data, string accent, string sectionColor)
    {
        container.Column(column =>
        {
            column.Spacing(14);

            if (!string.IsNullOrWhiteSpace(data.Note))
            {
                column.Item().Background(Colors.Grey.Lighten4).Padding(8).Text(data.Note).FontSize(9.5f);
            }

            if (data.Sections.HasFlag(ReportSections.Summary))
            {
                column.Item().Element(item => Summary(item, data));
            }

            if (data.SensorCount == 0)
            {
                column.Item().Text("No sensor matches what this report covers.").Italic().FontColor(Colors.Grey.Darken1);
            }

            if (data.Sections.HasFlag(ReportSections.Sensors) && data.Sensors.Count > 0)
            {
                column.Item().Element(item => Sensors(item, data, sectionColor));
            }

            if (data.Sections.HasFlag(ReportSections.Outages))
            {
                column.Item().Element(item => Outages(item, data, sectionColor));
            }

            if (data.Sections.HasFlag(ReportSections.Alerts))
            {
                column.Item().Element(item => Alerts(item, data, sectionColor));
            }

            if (data.Sections.HasFlag(ReportSections.Charts) && data.Charts.Count > 0)
            {
                column.Item().Element(item => Charts(item, data, accent, sectionColor));
            }

            if (data.DataNotes.Count > 0)
            {
                column.Item().PaddingTop(4).Column(notes =>
                {
                    notes.Item().Text("About these numbers").FontSize(9).SemiBold().FontColor(Colors.Grey.Darken2);
                    foreach (var note in data.DataNotes)
                    {
                        notes.Item().Text($"• {note}").FontSize(8).FontColor(Colors.Grey.Darken1);
                    }

                    notes.Item().Text("A warning counts as available - the sensor answered. Paused sensors and time without data count as neither.")
                        .FontSize(8).FontColor(Colors.Grey.Darken1);
                });
            }
        });
    }

    private static void Summary(IContainer container, AvailabilityReportData data)
    {
        var availability = data.Overall.Percent;
        var availabilityColor = AvailabilityColor(availability, data.SlaTargetPercent);
        container.Column(column =>
        {
            column.Spacing(6);
            column.Item().Row(row =>
            {
                row.Spacing(8);
                Card(row, "Availability", ReportMath.Percent(availability), availabilityColor, 2,
                    data.SlaTargetPercent is { } target ? $"agreed {target.ToString("0.###", CultureInfo.InvariantCulture)} %" : null);
                Card(row, "Affected sensors", data.SensorsWithOutages.ToString(CultureInfo.InvariantCulture), data.SensorsWithOutages > 0 ? Colors.Red.Darken1 : Colors.Green.Darken1, 1,
                    $"of {data.SensorCount} had an outage");
                Card(row, "Outages", data.OutageCount.ToString(CultureInfo.InvariantCulture), data.OutageCount > 0 ? Colors.Red.Darken1 : Colors.Green.Darken1, 1,
                    data.OutageCount > 0 ? $"longest {ReportMath.Span(data.LongestOutage)}" : null);
                Card(row, "Sensors", data.SensorCount.ToString(CultureInfo.InvariantCulture), Colors.Grey.Darken3, 1,
                    data.MachineCount > 0 ? $"on {data.MachineCount} machine{(data.MachineCount == 1 ? "" : "s")}" : null);
                Card(row, "Alerts", data.Alerts.Count.ToString(CultureInfo.InvariantCulture), data.Alerts.Count > 0 ? Colors.Orange.Darken2 : Colors.Green.Darken1, 1, null);
            });

            if (data.SlaTargetPercent is not null && data.SensorsBelowTarget > 0)
            {
                column.Item().Text($"{data.SensorsBelowTarget} sensor{(data.SensorsBelowTarget == 1 ? " was" : "s were")} below the agreed availability.")
                    .FontSize(9).FontColor(Colors.Red.Darken1);
            }

            if (data.SensorsWithoutData > 0)
            {
                column.Item().Text($"{data.SensorsWithoutData} sensor{(data.SensorsWithoutData == 1 ? " has" : "s have")} no data in this period (new, paused or not recorded).")
                    .FontSize(8.5f).FontColor(Colors.Grey.Darken1);
            }
        });
    }

    private static void Card(RowDescriptor row, string label, string value, string color, float weight, string? hint)
    {
        row.RelativeItem(weight).Border(1).BorderColor(Colors.Grey.Lighten2).Padding(8).Column(column =>
        {
            column.Item().Text(label).FontSize(8).FontColor(Colors.Grey.Darken1);
            column.Item().Text(value).FontSize(weight > 1 ? 18 : 14).SemiBold().FontColor(color);
            if (hint is not null)
            {
                column.Item().Text(hint).FontSize(7.5f).FontColor(Colors.Grey.Medium);
            }
        });
    }

    private static void Sensors(IContainer container, AvailabilityReportData data, string sectionColor)
    {
        container.Column(section =>
        {
            Heading(section, "Sensors", sectionColor, $"{data.Sensors.Count}");
            section.Item().Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.RelativeColumn(5);
                    columns.ConstantColumn(64);
                    columns.ConstantColumn(58);
                    columns.ConstantColumn(42);
                    columns.RelativeColumn(3);
                    columns.ConstantColumn(46);
                });

                table.Header(header =>
                {
                    ReportPdfChrome.HeaderCell(header, "Sensor");
                    ReportPdfChrome.HeaderCell(header, "Availability", alignRight: true);
                    ReportPdfChrome.HeaderCell(header, "Downtime", alignRight: true);
                    ReportPdfChrome.HeaderCell(header, "Outages", alignRight: true);
                    ReportPdfChrome.HeaderCell(header, "Reading: average (low – high)");
                    ReportPdfChrome.HeaderCell(header, "Now");
                });

                foreach (var row in data.Sensors)
                {
                    ReportPdfChrome.BodyCell(table).Column(cell =>
                    {
                        cell.Item().Text(row.Name).SemiBold();
                        cell.Item().Text(row.Machine is null ? row.Path : $"{row.Machine} · {row.TypeName}").FontSize(7.5f).FontColor(Colors.Grey.Darken1);
                    });
                    ReportPdfChrome.BodyCell(table).AlignRight().Text(ReportMath.Percent(row.UptimePercent))
                        .FontColor(row.BelowTarget ? Colors.Red.Darken1 : AvailabilityColor(row.UptimePercent, data.SlaTargetPercent)).SemiBold();
                    ReportPdfChrome.BodyCell(table).AlignRight().Text(row.Downtime > TimeSpan.Zero ? ReportMath.Span(row.Downtime) : "-");
                    ReportPdfChrome.BodyCell(table).AlignRight().Text(row.OutageCount > 0 ? row.OutageCount.ToString(CultureInfo.InvariantCulture) : "-");
                    ReportPdfChrome.BodyCell(table).Text(row.Channel is { } channel
                        ? $"{ReportMath.Value(channel.Average, channel.Unit)} ({ReportMath.Value(channel.Minimum, null)} – {ReportMath.Value(channel.Maximum, null)})"
                        : "-").FontSize(8.5f);
                    ReportPdfChrome.BodyCell(table).Text(MonitoringStatePresentation.Label(row.CurrentState)).FontSize(8.5f).FontColor(ReportPdfChrome.StateColor(row.CurrentState));
                }
            });
        });
    }

    private static void Outages(IContainer container, AvailabilityReportData data, string sectionColor)
    {
        container.Column(section =>
        {
            Heading(section, "Outages", sectionColor, data.OutageCount.ToString(CultureInfo.InvariantCulture));
            if (data.Outages.Count == 0)
            {
                section.Item().Text("No outage in this period.").FontColor(Colors.Green.Darken1);
                return;
            }

            section.Item().Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.ConstantColumn(78);
                    columns.ConstantColumn(78);
                    columns.ConstantColumn(56);
                    columns.RelativeColumn(3);
                    columns.RelativeColumn(4);
                });

                table.Header(header =>
                {
                    ReportPdfChrome.HeaderCell(header, "Start");
                    ReportPdfChrome.HeaderCell(header, "End");
                    ReportPdfChrome.HeaderCell(header, "Lasted", alignRight: true);
                    ReportPdfChrome.HeaderCell(header, "Sensor");
                    ReportPdfChrome.HeaderCell(header, "What it said");
                });

                foreach (var outage in data.Outages)
                {
                    ReportPdfChrome.BodyCell(table).Text(data.LocalTime(outage.StartUtc)).FontSize(8.5f);
                    ReportPdfChrome.BodyCell(table).Text(outage.Ongoing ? "still down" : data.LocalTime(outage.EndUtc)).FontSize(8.5f)
                        .FontColor(outage.Ongoing ? Colors.Red.Darken1 : Colors.Grey.Darken4);
                    ReportPdfChrome.BodyCell(table).AlignRight().Text(ReportMath.Span(outage.Duration)).FontSize(8.5f).SemiBold();
                    ReportPdfChrome.BodyCell(table).Column(cell =>
                    {
                        cell.Item().Text(outage.SensorName).FontSize(8.5f);
                        cell.Item().Text(outage.SensorPath).FontSize(7f).FontColor(Colors.Grey.Darken1);
                    });
                    ReportPdfChrome.BodyCell(table).Text(Shorten(outage.Message, 160) ?? "-").FontSize(8f).FontColor(Colors.Grey.Darken2);
                }
            });
        });
    }

    private static void Alerts(IContainer container, AvailabilityReportData data, string sectionColor)
    {
        container.Column(section =>
        {
            Heading(section, "Alerts", sectionColor, data.Alerts.Count.ToString(CultureInfo.InvariantCulture));
            if (data.Alerts.Count == 0)
            {
                section.Item().Text("No alert in this period.").FontColor(Colors.Green.Darken1);
                return;
            }

            section.Item().Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.ConstantColumn(78);
                    columns.ConstantColumn(48);
                    columns.ConstantColumn(60);
                    columns.RelativeColumn(4);
                    columns.RelativeColumn(2);
                });

                table.Header(header =>
                {
                    ReportPdfChrome.HeaderCell(header, "Raised");
                    ReportPdfChrome.HeaderCell(header, "State");
                    ReportPdfChrome.HeaderCell(header, "Lasted", alignRight: true);
                    ReportPdfChrome.HeaderCell(header, "Sensor / message");
                    ReportPdfChrome.HeaderCell(header, "Acknowledged by");
                });

                foreach (var alert in data.Alerts)
                {
                    ReportPdfChrome.BodyCell(table).Text(data.LocalTime(alert.FirstSeenUtc)).FontSize(8.5f);
                    ReportPdfChrome.BodyCell(table).Text(MonitoringStatePresentation.Label(alert.State)).FontSize(8.5f).FontColor(ReportPdfChrome.StateColor(alert.State));
                    ReportPdfChrome.BodyCell(table).AlignRight().Text(alert.Open ? "open" : ReportMath.Span(alert.DurationUntil(data.GeneratedUtc))).FontSize(8.5f);
                    ReportPdfChrome.BodyCell(table).Column(cell =>
                    {
                        cell.Item().Text(alert.SensorPath).FontSize(8f);
                        if (Shorten(alert.Message, 140) is { } message)
                        {
                            cell.Item().Text(message).FontSize(7.5f).FontColor(Colors.Grey.Darken1);
                        }
                    });
                    ReportPdfChrome.BodyCell(table).Text(alert.AcknowledgedBy ?? "-").FontSize(8f);
                }
            });
        });
    }

    private static void Charts(IContainer container, AvailabilityReportData data, string accent, string sectionColor)
    {
        container.Column(section =>
        {
            Heading(section, "Charts", sectionColor, data.ChartsLeftOut > 0 ? $"{data.Charts.Count} of {data.Charts.Count + data.ChartsLeftOut}, lowest availability first" : null);
            section.Spacing(10);
            var middle = data.Window.FromUtc + TimeSpan.FromTicks(data.Window.Length.Ticks / 2);
            var format = data.Window.Length <= TimeSpan.FromDays(2) ? "dd.MM HH:mm" : "dd.MM.yyyy";

            foreach (var chart in data.Charts)
            {
                var (min, max) = ReportChartSvg.Scale(chart);
                var svg = ReportChartSvg.Render(chart, data.Window.FromUtc, data.Window.ToUtc, accent);
                section.Item().ShowEntire().Column(block =>
                {
                    block.Item().Row(row =>
                    {
                        row.RelativeItem().Text(text =>
                        {
                            text.Span(chart.Title).SemiBold();
                            text.Span($"   {chart.Subtitle}").FontSize(8).FontColor(Colors.Grey.Darken1);
                        });
                        if (chart.FromAverages)
                        {
                            row.AutoItem().Text("hourly / daily averages").FontSize(7.5f).FontColor(Colors.Grey.Medium);
                        }
                    });
                    block.Item().PaddingTop(3).Row(row =>
                    {
                        // Three fixed thirds of the plot's height - an Extend() between them would eat the space of the last.
                        row.ConstantItem(50).Column(axis =>
                        {
                            axis.Item().Height(32).AlignTop().AlignRight().PaddingRight(4).Text(ReportMath.Value(max, chart.Unit)).FontSize(7).FontColor(Colors.Grey.Darken1).ClampLines(1);
                            axis.Item().Height(32).AlignMiddle().AlignRight().PaddingRight(4).Text(ReportMath.Value((min + max) / 2, null)).FontSize(7).FontColor(Colors.Grey.Darken1).ClampLines(1);
                            axis.Item().Height(32).AlignBottom().AlignRight().PaddingRight(4).Text(ReportMath.Value(min, null)).FontSize(7).FontColor(Colors.Grey.Darken1).ClampLines(1);
                        });
                        row.RelativeItem().Svg(svg);
                    });
                    block.Item().PaddingLeft(50).Row(row =>
                    {
                        row.RelativeItem().Text(data.LocalTime(data.Window.FromUtc, format)).FontSize(7).FontColor(Colors.Grey.Darken1);
                        row.RelativeItem().AlignCenter().Text(data.LocalTime(middle, format)).FontSize(7).FontColor(Colors.Grey.Darken1);
                        row.RelativeItem().AlignRight().Text(data.LocalTime(data.Window.ToUtc, format)).FontSize(7).FontColor(Colors.Grey.Darken1);
                    });
                });
            }

            section.Item().Text(text =>
            {
                text.DefaultTextStyle(style => style.FontSize(7.5f).FontColor(Colors.Grey.Darken1));
                text.Span("The band under each chart is the state: ");
                text.Span("OK").FontColor(ReportChartSvg.StateColor(SensorState.Healthy));
                text.Span(" · ");
                text.Span("Warning").FontColor(ReportChartSvg.StateColor(SensorState.Warning));
                text.Span(" · ");
                text.Span("Error").FontColor(ReportChartSvg.StateColor(SensorState.Critical));
                text.Span(" · grey = paused or no data.");
            });
        });
    }

    private static void Heading(ColumnDescriptor section, string title, string color, string? count)
    {
        section.Item().PaddingBottom(4).Text(text =>
        {
            text.Span(title).FontSize(13).Bold().FontColor(color);
            if (!string.IsNullOrWhiteSpace(count))
            {
                text.Span($"   {count}").FontSize(9).FontColor(Colors.Grey.Medium);
            }
        });
    }

    private static string AvailabilityColor(double? percent, double? target)
    {
        if (percent is not { } value)
        {
            return Colors.Grey.Darken1;
        }

        if (target is { } agreed)
        {
            return value >= agreed ? Colors.Green.Darken1 : Colors.Red.Darken1;
        }

        return value >= 99.9 ? Colors.Green.Darken1 : value >= 99 ? Colors.Orange.Darken2 : Colors.Red.Darken1;
    }

    private static string? Shorten(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var single = text.ReplaceLineEndings(" ").Trim();
        return single.Length <= max ? single : single[..(max - 1)] + "…";
    }
}
