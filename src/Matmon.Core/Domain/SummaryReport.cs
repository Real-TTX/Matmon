using System.Globalization;
using System.Net;
using System.Text;

namespace Matmon.Core.Domain;

public sealed record SummaryReportSensorLine(
    string Path,
    SensorState State,
    double? UptimePercent,
    double? LastValue,
    string? Unit,
    int SampleCount);

public sealed record SummaryReportEventLine(
    DateTimeOffset TimestampUtc,
    string Kind,
    string Path,
    string Message);

public sealed record SummaryReportData(
    string WorkspaceName,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    int ProbeCount,
    int SensorCount,
    int PausedSensorCount,
    int ActiveAlertCount,
    int AcknowledgedAlertCount,
    int ErrorSensorCount,
    int WarningSensorCount,
    IReadOnlyList<SummaryReportSensorLine> LowestUptime,
    IReadOnlyList<SummaryReportEventLine> RecentEvents);

public sealed record SummaryReport(string Subject, string TextBody, string HtmlBody);

/// <summary>Pure renderer that turns <see cref="SummaryReportData"/> into a subject + text + HTML e-mail body.</summary>
public static class SummaryReportBuilder
{
    public static SummaryReport Build(SummaryReportData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var period = $"{data.FromUtc.ToLocalTime():g} – {data.ToUtc.ToLocalTime():g}";
        var subject = $"{data.WorkspaceName} summary - {data.ActiveAlertCount} active alert{(data.ActiveAlertCount == 1 ? string.Empty : "s")}";

        return new SummaryReport(subject, BuildText(data, period), BuildHtml(data, period));
    }

    private static string BuildText(SummaryReportData data, string period)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{data.WorkspaceName} - summary report");
        sb.AppendLine(period);
        sb.AppendLine();
        sb.AppendLine($"Probes: {data.ProbeCount}   Sensors: {data.SensorCount} ({data.PausedSensorCount} paused)");
        sb.AppendLine($"Now: {data.ErrorSensorCount} error / {data.WarningSensorCount} warning");
        sb.AppendLine($"Alerts: {data.ActiveAlertCount} active ({data.AcknowledgedAlertCount} acknowledged)");
        sb.AppendLine();

        if (data.LowestUptime.Count > 0)
        {
            sb.AppendLine("Lowest uptime:");
            foreach (var line in data.LowestUptime)
            {
                var uptime = line.UptimePercent.HasValue
                    ? line.UptimePercent.Value.ToString("0.#", CultureInfo.InvariantCulture) + "%"
                    : "n/a";
                sb.AppendLine($"  {uptime,-7} {line.Path} ({StateText(line.State)})");
            }

            sb.AppendLine();
        }

        if (data.RecentEvents.Count > 0)
        {
            sb.AppendLine("Recent events:");
            foreach (var line in data.RecentEvents)
            {
                sb.AppendLine($"  {line.TimestampUtc.ToLocalTime():g}  {line.Kind}  {line.Path} - {line.Message}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string BuildHtml(SummaryReportData data, string period)
    {
        var sb = new StringBuilder();
        sb.Append("<div style=\"font-family:Segoe UI,Arial,sans-serif;color:#111827;max-width:720px;\">");
        sb.Append($"<h2 style=\"margin:0 0 4px 0;\">{Enc(data.WorkspaceName)} - summary report</h2>");
        sb.Append($"<div style=\"color:#6b7280;margin-bottom:16px;\">{Enc(period)}</div>");

        sb.Append("<div style=\"display:flex;gap:10px;flex-wrap:wrap;margin-bottom:18px;\">");
        sb.Append(Card("Probes", data.ProbeCount.ToString(CultureInfo.InvariantCulture), "#374151"));
        sb.Append(Card("Sensors", $"{data.SensorCount} ({data.PausedSensorCount} paused)", "#374151"));
        sb.Append(Card("Errors now", data.ErrorSensorCount.ToString(CultureInfo.InvariantCulture), data.ErrorSensorCount > 0 ? "#dc2626" : "#16a34a"));
        sb.Append(Card("Warnings now", data.WarningSensorCount.ToString(CultureInfo.InvariantCulture), data.WarningSensorCount > 0 ? "#d97706" : "#16a34a"));
        sb.Append(Card("Active alerts", $"{data.ActiveAlertCount} ({data.AcknowledgedAlertCount} ack)", data.ActiveAlertCount > 0 ? "#dc2626" : "#16a34a"));
        sb.Append("</div>");

        if (data.LowestUptime.Count > 0)
        {
            sb.Append("<h3 style=\"margin:0 0 8px 0;\">Lowest uptime</h3>");
            sb.Append("<table style=\"width:100%;border-collapse:collapse;margin-bottom:18px;\">");
            sb.Append("<tr><th style=\"text-align:left;padding:6px 8px;border-bottom:1px solid #e5e7eb;\">Sensor</th><th style=\"text-align:right;padding:6px 8px;border-bottom:1px solid #e5e7eb;\">Uptime</th><th style=\"text-align:left;padding:6px 8px;border-bottom:1px solid #e5e7eb;\">State</th></tr>");
            foreach (var line in data.LowestUptime)
            {
                var uptime = line.UptimePercent.HasValue
                    ? line.UptimePercent.Value.ToString("0.#", CultureInfo.InvariantCulture) + "%"
                    : "n/a";
                sb.Append("<tr>");
                sb.Append($"<td style=\"padding:6px 8px;border-bottom:1px solid #f3f4f6;\">{Enc(line.Path)}</td>");
                sb.Append($"<td style=\"padding:6px 8px;border-bottom:1px solid #f3f4f6;text-align:right;\">{Enc(uptime)}</td>");
                sb.Append($"<td style=\"padding:6px 8px;border-bottom:1px solid #f3f4f6;color:{StateColor(line.State)};\">{Enc(StateText(line.State))}</td>");
                sb.Append("</tr>");
            }

            sb.Append("</table>");
        }

        if (data.RecentEvents.Count > 0)
        {
            sb.Append("<h3 style=\"margin:0 0 8px 0;\">Recent events</h3>");
            sb.Append("<table style=\"width:100%;border-collapse:collapse;\">");
            foreach (var line in data.RecentEvents)
            {
                sb.Append("<tr>");
                sb.Append($"<td style=\"padding:4px 8px;color:#6b7280;white-space:nowrap;\">{Enc(line.TimestampUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture))}</td>");
                sb.Append($"<td style=\"padding:4px 8px;\">{Enc(line.Kind)}</td>");
                sb.Append($"<td style=\"padding:4px 8px;\">{Enc(line.Path)} - {Enc(line.Message)}</td>");
                sb.Append("</tr>");
            }

            sb.Append("</table>");
        }

        sb.Append("</div>");
        return sb.ToString();
    }

    private static string Card(string label, string value, string color) =>
        $"<div style=\"flex:1;min-width:120px;border:1px solid #e5e7eb;border-radius:10px;padding:10px 12px;\">" +
        $"<div style=\"color:#6b7280;font-size:12px;\">{Enc(label)}</div>" +
        $"<div style=\"font-size:18px;font-weight:600;color:{color};\">{Enc(value)}</div></div>";

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
        SensorState.Critical => "#dc2626",
        SensorState.Warning => "#d97706",
        SensorState.Healthy => "#16a34a",
        _ => "#6b7280"
    };

    private static string Enc(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
}
