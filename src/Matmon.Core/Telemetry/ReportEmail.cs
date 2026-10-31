using System.Net;
using System.Text;
using Matmon.Core.Domain;

namespace Matmon.Core.Telemetry;

/// <summary>The e-mail a scheduled report goes out with: the figures at a glance, in the managing partner's branding, the
/// full report attached. Pure (no store, no SMTP), so it is tested - and every colour and link re-validated
/// (<see cref="BrandingSafety"/>) before it reaches the markup.</summary>
public static class ReportEmail
{
    public static SummaryReport Build(AvailabilityReportData data, string? attachmentNote)
    {
        ArgumentNullException.ThrowIfNull(data);
        var period = $"{data.Window.Label} ({data.LocalTime(data.Window.FromUtc)} – {data.LocalTime(data.Window.ToUtc)})";
        var subject = $"{data.Title} - {data.Window.Label} - {ReportMath.Percent(data.Overall.Percent)} available";
        return new SummaryReport(subject, Text(data, period, attachmentNote), Html(data, period, attachmentNote));
    }

    private static string Text(AvailabilityReportData data, string period, string? attachmentNote)
    {
        var sb = new StringBuilder();
        sb.AppendLine(data.Title);
        sb.AppendLine(data.Scope);
        if (!string.IsNullOrWhiteSpace(data.PreparedFor))
        {
            sb.AppendLine($"Prepared for {data.PreparedFor}");
        }

        sb.AppendLine(period);
        sb.AppendLine();
        if (data.Branding is { } brand)
        {
            if (!string.IsNullOrWhiteSpace(brand.PartnerName))
            {
                sb.AppendLine($"Managed by {brand.PartnerName}");
            }

            if (BrandingSafety.SafeContactUrl(brand.ContactUrl) is { } contact)
            {
                sb.AppendLine($"Support: {contact}");
            }

            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(data.Note))
        {
            sb.AppendLine(data.Note);
            sb.AppendLine();
        }

        sb.AppendLine($"Availability: {ReportMath.Percent(data.Overall.Percent)}{(data.SlaTargetPercent is { } target ? $" (agreed {target:0.###} %)" : string.Empty)}");
        sb.AppendLine($"Outages: {data.OutageCount} on {data.SensorsWithOutages} of {data.SensorCount} sensors{(data.OutageCount > 0 ? $", the longest {ReportMath.Span(data.LongestOutage)}" : string.Empty)}");
        sb.AppendLine($"Sensors: {data.SensorCount}   Alerts: {data.Alerts.Count}");
        var worst = Worst(data);
        if (worst.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Lowest availability:");
            foreach (var row in worst)
            {
                sb.AppendLine($"  {ReportMath.Percent(row.UptimePercent),-10} {row.Path}");
            }
        }

        if (!string.IsNullOrWhiteSpace(attachmentNote))
        {
            sb.AppendLine();
            sb.AppendLine(attachmentNote);
        }

        return sb.ToString().TrimEnd();
    }

    private static string Html(AvailabilityReportData data, string period, string? attachmentNote)
    {
        var accent = BrandingSafety.SafeHexColor(data.Branding?.BrandColor) ?? "#1D4ED8";
        var secondary = BrandingSafety.SafeHexColor(data.Branding?.SecondaryColor);
        var contact = BrandingSafety.SafeContactUrl(data.Branding?.ContactUrl);
        var rule = secondary is null ? "border-bottom:1px solid #e5e7eb;" : $"border-bottom:3px solid {secondary};";
        var sectionColor = secondary ?? "#111827";

        var sb = new StringBuilder();
        sb.Append("<div style=\"font-family:Segoe UI,Arial,sans-serif;color:#111827;max-width:680px;\">");
        if (data.Branding?.LogoPng is { Length: > 0 } logo && BrandingSafety.DetectRasterContentType(logo) is { } mime)
        {
            sb.Append($"<img src=\"data:{mime};base64,{Convert.ToBase64String(logo)}\" alt=\"{Enc(data.Branding.PartnerName)}\" style=\"max-height:44px;max-width:220px;display:block;margin:0 0 12px;\" />");
        }

        sb.Append($"<h2 style=\"margin:0 0 4px;padding-bottom:6px;{rule}color:{accent};\">{Enc(data.Title)}</h2>");
        sb.Append($"<div style=\"font-weight:600;margin-top:6px;\">{Enc(data.Scope)}</div>");
        if (!string.IsNullOrWhiteSpace(data.PreparedFor))
        {
            sb.Append($"<div style=\"color:#6b7280;\">Prepared for {Enc(data.PreparedFor)}</div>");
        }

        sb.Append($"<div style=\"color:#6b7280;margin-bottom:14px;\">{Enc(period)}</div>");

        if (data.Branding is { } brand)
        {
            if (!string.IsNullOrWhiteSpace(brand.PartnerName))
            {
                sb.Append($"<div style=\"color:#6b7280;font-size:13px;\">Managed by {Enc(brand.PartnerName)}</div>");
            }

            if (!string.IsNullOrWhiteSpace(brand.Slogan))
            {
                sb.Append($"<div style=\"color:#6b7280;font-size:12px;font-style:italic;\">{Enc(brand.Slogan)}</div>");
            }

            if (contact is not null)
            {
                sb.Append($"<div style=\"font-size:13px;margin-bottom:12px;\"><a href=\"{Enc(contact)}\" style=\"color:{accent};\">{Enc(contact)}</a></div>");
            }
        }

        if (!string.IsNullOrWhiteSpace(data.Note))
        {
            sb.Append($"<p style=\"background:#f3f4f6;padding:10px 12px;border-radius:8px;\">{Enc(data.Note)}</p>");
        }

        var availabilityColor = data.Overall.Percent is not { } percent ? "#6b7280"
            : data.SlaTargetPercent is { } target ? (percent >= target ? "#16a34a" : "#dc2626")
            : percent >= 99.9 ? "#16a34a" : percent >= 99 ? "#d97706" : "#dc2626";
        sb.Append("<table role=\"presentation\" style=\"width:100%;border-collapse:separate;border-spacing:8px 0;margin:6px -8px 16px;\"><tr>");
        sb.Append(Card("Availability", ReportMath.Percent(data.Overall.Percent), availabilityColor,
            data.SlaTargetPercent is { } agreed ? $"agreed {agreed:0.###} %" : null));
        sb.Append(Card("Affected sensors", data.SensorsWithOutages.ToString(System.Globalization.CultureInfo.InvariantCulture), data.SensorsWithOutages > 0 ? "#dc2626" : "#16a34a", $"of {data.SensorCount}"));
        sb.Append(Card("Outages", data.OutageCount.ToString(System.Globalization.CultureInfo.InvariantCulture), data.OutageCount > 0 ? "#dc2626" : "#16a34a", null));
        sb.Append(Card("Sensors", data.SensorCount.ToString(System.Globalization.CultureInfo.InvariantCulture), "#374151", null));
        sb.Append("</tr></table>");

        var worst = Worst(data);
        if (worst.Count > 0)
        {
            sb.Append($"<h3 style=\"margin:0 0 6px;color:{sectionColor};\">Lowest availability</h3>");
            sb.Append("<table style=\"width:100%;border-collapse:collapse;margin-bottom:14px;\">");
            foreach (var row in worst)
            {
                sb.Append("<tr>");
                sb.Append($"<td style=\"padding:5px 8px;border-bottom:1px solid #f3f4f6;\">{Enc(row.Path)}</td>");
                sb.Append($"<td style=\"padding:5px 8px;border-bottom:1px solid #f3f4f6;text-align:right;white-space:nowrap;\">{Enc(ReportMath.Percent(row.UptimePercent))}</td>");
                sb.Append($"<td style=\"padding:5px 8px;border-bottom:1px solid #f3f4f6;text-align:right;white-space:nowrap;color:#6b7280;\">{Enc(row.Downtime > TimeSpan.Zero ? ReportMath.Span(row.Downtime) + " down" : string.Empty)}</td>");
                sb.Append("</tr>");
            }

            sb.Append("</table>");
        }

        if (!string.IsNullOrWhiteSpace(attachmentNote))
        {
            sb.Append($"<p style=\"color:#6b7280;font-size:13px;\">{Enc(attachmentNote)}</p>");
        }

        var credit = data.Branding?.ProductName is { Length: > 0 } product ? $"{product} · powered by Matmon" : "Matmon";
        sb.Append($"<p style=\"color:#9ca3af;font-size:11px;margin-top:18px;\">{Enc(credit)}</p>");
        sb.Append("</div>");
        return sb.ToString();
    }

    /// <summary>The five sensors with the lowest availability below 100 % (none when everything was up).</summary>
    public static IReadOnlyList<ReportSensorRow> Worst(AvailabilityReportData data) => data.Sensors
        .Where(row => row.UptimePercent is < 100)
        .OrderBy(row => row.UptimePercent)
        .ThenByDescending(row => row.Downtime)
        .Take(5)
        .ToArray();

    private static string Card(string label, string value, string color, string? hint) =>
        "<td style=\"border:1px solid #e5e7eb;border-radius:10px;padding:10px 12px;vertical-align:top;\">" +
        $"<div style=\"color:#6b7280;font-size:12px;\">{Enc(label)}</div>" +
        $"<div style=\"font-size:18px;font-weight:600;color:{color};\">{Enc(value)}</div>" +
        (hint is null ? string.Empty : $"<div style=\"color:#9ca3af;font-size:11px;\">{Enc(hint)}</div>") +
        "</td>";

    private static string Enc(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
}
