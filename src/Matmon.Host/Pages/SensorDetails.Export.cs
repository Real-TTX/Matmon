using System.Globalization;
using System.Text;
using Matmon.Core.Domain;
using Matmon.Core.Telemetry;
using Matmon.Host.Services.Reports;
using Microsoft.AspNetCore.Mvc;

namespace Matmon.Host.Pages;

// The History tab's lists as files: the checks, the status history or the alerts - with the filter that is set - as CSV
// (for a spreadsheet) or PDF (to hand on, in the managing partner's branding). The whole filtered list, not one page.
public sealed partial class SensorDetailsModel
{
    /// <summary>More checks than this are not exported (a 30-second sensor keeps ~8 600 a day at most a few days).</summary>
    public const int MaxExportedChecks = 50_000;

    /// <summary>A PDF stops here - past a few thousand rows nobody reads it, the CSV is the way.</summary>
    public const int MaxPdfRows = 5_000;

    /// <param name="list">checks | status | alerts</param>
    /// <param name="format">csv | pdf</param>
    public IActionResult OnGetExportHistory(Guid sensorId, string? list, string? format)
    {
        if (_workspaceStore.FindElement(sensorId) is not SensorElement sensor)
        {
            return NotFound();
        }

        var path = SensorPath(sensorId);
        var (columns, rows, stateColumn, name) = (list?.Trim().ToLowerInvariant()) switch
        {
            "status" => StatusExport(sensor),
            "alerts" => AlertsExport(sensor),
            _ => ChecksExport(sensor)
        };

        var stamp = DateTimeOffset.UtcNow.ToDisplay().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var fileStem = $"{Slug(sensor.Name)}-{name}-{stamp}";
        if (string.Equals(format, "pdf", StringComparison.OrdinalIgnoreCase))
        {
            var shown = rows.Count > MaxPdfRows ? rows.Take(MaxPdfRows).ToArray() : rows;
            var builder = HttpContext.RequestServices.GetRequiredService<ReportPdfBuilder>();
            var filterText = FilterText(list);
            var pdf = builder.BuildTable(
                $"{TitleOf(name)} - {sensor.Name}",
                [path, filterText is null ? $"{rows.Count} entries" : $"{rows.Count} entries · {filterText}", $"Exported {DateTimeOffset.UtcNow.ToDisplay():dd.MM.yyyy HH:mm}"],
                columns,
                shown,
                ReportDataCollector.Branding(_workspaceStore),
                stateColumn,
                rows.Count > MaxPdfRows ? $"The newest {MaxPdfRows} of {rows.Count} entries - the CSV holds them all." : null);
            return File(pdf, "application/pdf", fileStem + ".pdf");
        }

        var sb = new StringBuilder();
        ReportCsv.Row(sb, columns.Select(column => column.Item1).ToArray());
        foreach (var row in rows)
        {
            ReportCsv.Row(sb, row);
        }

        return File(ReportCsv.Encode(sb), "text/csv; charset=utf-8", fileStem + ".csv");
    }

    private (IReadOnlyList<(string, float)>, IReadOnlyList<string[]>, int?, string) ChecksExport(SensorElement sensor)
    {
        var filter = CheckFilters.FirstOrDefault(option => string.Equals(option.Key, ChecksFilter?.Trim(), StringComparison.OrdinalIgnoreCase));
        var page = _workspaceStore.GetSensorObservationPage(sensor.Id, filter.States, 0, MaxExportedChecks);
        var rows = page.Select(observation =>
        {
            var main = observation.Channels.FirstOrDefault(channel => !channel.IsVirtual && channel.Value.HasValue
                           && string.Equals(channel.Key, observation.DefaultChannelKey, StringComparison.OrdinalIgnoreCase))
                       ?? observation.Channels.FirstOrDefault(channel => channel.IsDefault && !channel.IsVirtual);
            // A failed check without a measured channel has no reading - its bare value (0) is no measurement.
            var value = main?.Value ?? (observation.State == SensorState.Critical ? null : observation.Value);
            var channels = string.Join("; ", observation.Channels
                .Where(channel => !channel.IsVirtual && channel.Value.HasValue)
                .Select(channel => $"{(string.IsNullOrWhiteSpace(channel.Label) ? channel.Key : channel.Label)}={channel.Value!.Value.ToString("0.####", CultureInfo.InvariantCulture)}{(string.IsNullOrWhiteSpace(channel.Unit) ? string.Empty : " " + channel.Unit)}"));
            return new[]
            {
                observation.TimestampUtc.ToDisplay().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                MonitoringStatePresentation.Label(observation.State),
                value is { } number ? number.ToString("0.####", CultureInfo.InvariantCulture) : string.Empty,
                main?.Unit ?? string.Empty,
                ((long)observation.Duration.TotalMilliseconds).ToString(CultureInfo.InvariantCulture),
                observation.ExecutedByProbeName ?? string.Empty,
                observation.Message ?? string.Empty,
                channels
            };
        }).ToArray();

        return ([("Time", 1.4f), ("State", 0.8f), ("Value", 0.8f), ("Unit", 0.5f), ("Duration (ms)", 0.8f), ("Probe", 1.1f), ("Message", 3f), ("Channels", 3f)], rows, 1, "checks");
    }

    private (IReadOnlyList<(string, float)>, IReadOnlyList<string[]>, int?, string) StatusExport(SensorElement sensor)
    {
        var all = SensorStatusHistory.Build(_workspaceStore.GetElementEvents(sensor.Id), _workspaceStore.GetElementAlerts(sensor.Id), DateTimeOffset.UtcNow);
        var filter = StatusFilters.FirstOrDefault(option => string.Equals(option.Key, StatusFilter?.Trim(), StringComparison.OrdinalIgnoreCase));
        var rows = all
            .Where(entry => filter.Key is null || entry.Category == filter.Category)
            .Select(entry =>
            {
                var row = ToStatusRow(entry, sensor.Id);
                return new[]
                {
                    entry.TimestampUtc.ToDisplay().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    row.KindLabel,
                    row.FromLabel ?? string.Empty,
                    row.ToLabel ?? string.Empty,
                    row.DurationText ?? string.Empty,
                    row.Actor ?? string.Empty,
                    row.Message ?? string.Empty
                };
            })
            .ToArray();

        return ([("Time", 1.4f), ("What", 1.2f), ("From", 0.8f), ("To", 0.8f), ("Duration", 1.2f), ("By", 1f), ("Message", 4f)], rows, 3, "status-history");
    }

    private (IReadOnlyList<(string, float)>, IReadOnlyList<string[]>, int?, string) AlertsExport(SensorElement sensor)
    {
        var now = DateTimeOffset.UtcNow;
        static string Phase(MonitoringAlert alert) => !alert.IsActive ? "resolved" : alert.IsRecovered ? "recovered" : "open";
        var filter = AlertFilters.FirstOrDefault(option => string.Equals(option.Key, AlertsFilter?.Trim(), StringComparison.OrdinalIgnoreCase));
        string Time(DateTimeOffset? at) => at is { } value ? value.ToDisplay().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : string.Empty;
        var rows = _workspaceStore.GetElementAlerts(sensor.Id)
            .Where(alert => filter.Key is null || Phase(alert) == filter.Key)
            .OrderByDescending(alert => alert.FirstSeenUtc)
            .Select(alert =>
            {
                var end = alert.RecoveredUtc ?? alert.ResolvedUtc;
                return new[]
                {
                    Time(alert.FirstSeenUtc),
                    MonitoringStatePresentation.Label(alert.State),
                    Phase(alert) switch { "open" => "Open", "recovered" => "Recovered", _ => "Closed" },
                    Time(end),
                    ReportMath.Span((end ?? now) - alert.FirstSeenUtc) + (end is null ? " so far" : string.Empty),
                    alert.AcknowledgedUtc is { } acked ? $"{Time(acked)}{(string.IsNullOrWhiteSpace(alert.AcknowledgedBy) ? string.Empty : " by " + alert.AcknowledgedBy)}" : string.Empty,
                    alert.Message ?? string.Empty
                };
            })
            .ToArray();

        return ([("Raised", 1.4f), ("State", 0.8f), ("Now", 0.9f), ("Ended", 1.4f), ("Lasted", 1f), ("Acknowledged", 1.8f), ("Message", 4f)], rows, 1, "alerts");
    }

    private string? FilterText(string? list) => (list?.Trim().ToLowerInvariant()) switch
    {
        "status" => StatusFilters.FirstOrDefault(option => string.Equals(option.Key, StatusFilter?.Trim(), StringComparison.OrdinalIgnoreCase)).Label,
        "alerts" => AlertFilters.FirstOrDefault(option => string.Equals(option.Key, AlertsFilter?.Trim(), StringComparison.OrdinalIgnoreCase)).Label,
        _ => CheckFilters.FirstOrDefault(option => string.Equals(option.Key, ChecksFilter?.Trim(), StringComparison.OrdinalIgnoreCase)).Label
    };

    private static string TitleOf(string name) => name switch
    {
        "status-history" => "Status history",
        "alerts" => "Alerts",
        _ => "Checks"
    };

    private string SensorPath(Guid sensorId)
    {
        var names = new List<string>();
        var current = _workspaceStore.FindElement(sensorId);
        var guard = 0;
        while (current is not null && guard++ < 256)
        {
            names.Add(current.Name);
            current = current.ParentId is { } parent ? _workspaceStore.FindElement(parent) : null;
        }

        names.Reverse();
        return string.Join(" / ", names);
    }

    private static string Slug(string text)
    {
        var sb = new StringBuilder();
        foreach (var character in text.ToLowerInvariant())
        {
            sb.Append(character is >= 'a' and <= 'z' or >= '0' and <= '9' ? character : '-');
        }

        var slug = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), "-{2,}", "-").Trim('-');
        return slug.Length == 0 ? "sensor" : slug.Length > 50 ? slug[..50].Trim('-') : slug;
    }
}
