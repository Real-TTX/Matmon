using Matmon.Core.Domain;

namespace Matmon.Host.Services;

// Saved reports (Reports): what a report covers, its period and what it shows, and when it is e-mailed. The report itself
// is made on demand from the telemetry (ReportDataCollector); only the definition and its send bookkeeping live here.
public sealed partial class InMemoryMonitoringWorkspaceStore
{
    public IReadOnlyList<ReportDefinition> GetReports()
    {
        lock (_gate)
        {
            return (_document.Reports ??= [])
                .OrderBy(report => report.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(report => report.Clone())
                .ToList();
        }
    }

    public ReportDefinition? FindReport(Guid id)
    {
        lock (_gate)
        {
            return _document.Reports?.FirstOrDefault(report => report.Id == id)?.Clone();
        }
    }

    public ReportDefinition SaveReport(ReportDefinition report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var copy = report.Clone();
        copy.Name = copy.Name.Trim();
        copy.TargetToken = string.IsNullOrWhiteSpace(copy.TargetToken) ? null : copy.TargetToken.Trim();
        copy.PreparedFor = string.IsNullOrWhiteSpace(copy.PreparedFor) ? null : copy.PreparedFor.Trim();
        copy.Note = string.IsNullOrWhiteSpace(copy.Note) ? null : copy.Note.Trim();
        copy.Recipients = (copy.Recipients ?? string.Empty).Trim();
        if (copy.Period != ReportPeriod.Custom)
        {
            copy.CustomFromUtc = null;
            copy.CustomToUtc = null;
        }

        if (ReportRules.Validate(copy) is { } problem)
        {
            throw new InvalidOperationException(problem);
        }

        lock (_gate)
        {
            if (MonitoringTargetResolver.ElementId(copy.TargetToken) is { } elementId && FindElementInternal(elementId) is null)
            {
                throw new InvalidOperationException("The element the report covers no longer exists - choose another.");
            }

            _document.Reports ??= [];
            var index = _document.Reports.FindIndex(existing => existing.Id == copy.Id);
            copy.UpdatedUtc = DateTimeOffset.UtcNow;
            if (index >= 0)
            {
                var existing = _document.Reports[index];
                copy.CreatedUtc = existing.CreatedUtc;
                copy.CreatedBy = existing.CreatedBy;
                copy.LastSentUtc = existing.LastSentUtc;
                copy.LastResult = existing.LastResult;
                copy.LastSendFailed = existing.LastSendFailed;

                // A schedule switched on (or changed) starts counting from now: it must not send at once for a slot that
                // passed before anybody asked for it.
                if (existing.Schedule != copy.Schedule || existing.HourOfDay != copy.HourOfDay
                    || existing.DayOfWeek != copy.DayOfWeek || existing.DayOfMonth != copy.DayOfMonth)
                {
                    copy.LastSentUtc = null;
                    copy.CreatedUtc = DateTimeOffset.UtcNow;
                }

                _document.Reports[index] = copy;
            }
            else
            {
                copy.CreatedUtc = DateTimeOffset.UtcNow;
                copy.LastSentUtc = null;
                copy.LastResult = null;
                copy.LastSendFailed = false;
                _document.Reports.Add(copy);
            }

            QueueSave(SavePriority.Configuration);
            return copy.Clone();
        }
    }

    public bool DeleteReport(Guid id)
    {
        lock (_gate)
        {
            if (_document.Reports?.RemoveAll(report => report.Id == id) is not > 0)
            {
                return false;
            }

            QueueSave(SavePriority.Configuration);
            return true;
        }
    }

    public void MarkReportSent(Guid id, DateTimeOffset sentUtc, bool failed, string result)
    {
        lock (_gate)
        {
            if (_document.Reports?.FirstOrDefault(report => report.Id == id) is not { } report)
            {
                return;
            }

            report.LastSentUtc = sentUtc;
            report.LastSendFailed = failed;
            report.LastResult = result.Length > 300 ? result[..300] : result;
            QueueSave(SavePriority.Configuration);
        }
    }

    public TimeZoneInfo ReportZone()
    {
        lock (_gate)
        {
            return DisplayTimeZone.Resolve(_document.DisplayTimeZoneId) ?? TimeZoneInfo.Local;
        }
    }

    public IReadOnlyList<Matmon.Core.Domain.SensorObservation> GetSensorObservationsBetween(Guid sensorId, DateTimeOffset fromUtc, DateTimeOffset toUtc, int maxCount)
    {
        if (maxCount <= 0 || toUtc < fromUtc)
        {
            return [];
        }

        // Read from the start, cap at the end: GetObservations keeps the NEWEST maxCount from fromUtc on, so the rows after
        // toUtc are dropped here - a past period (the previous month) never has many of those, raw checks are kept days.
        return _telemetry.GetObservations(sensorId, fromUtc, null)
            .Where(observation => observation.TimestampUtc <= toUtc)
            .TakeLast(maxCount)
            .ToArray();
    }
}
