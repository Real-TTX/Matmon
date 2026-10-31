using Matmon.Core.Domain;
using Matmon.Core.Telemetry;

namespace Matmon.Host.Services.Reports;

/// <summary>What to make a report about - a saved report or a quick one from the Reports page.</summary>
public sealed record ReportRequest(
    string TargetToken,
    ReportWindow Window,
    string? Title = null,
    string? PreparedFor = null,
    string? Note = null,
    ReportSections Sections = ReportSections.All,
    double? SlaTargetPercent = null);

/// <summary>
/// Gathers a report's data from the store and the telemetry: per sensor its availability (state counts), its downtime and
/// outages (status history), its main reading (average / lowest / highest) and a chart; the alerts of the period; the
/// managing partner's branding. Each figure comes from the finest source that still covers the period - the raw checks
/// (kept ~3 days), the statistics (~14 days), the daily totals (400 days) - and the report says which when it matters.
/// </summary>
public sealed class ReportDataCollector(IMonitoringWorkspaceStore store)
{
    /// <summary>More sensors than this are not reported on in one go (the PDF would run to hundreds of pages).</summary>
    public const int MaxSensors = 2000;

    public const int MaxCharts = 24;

    public const int MaxOutages = 1000;

    private const int MaxRawPerSensor = 20_000;

    private const int ChartPoints = 160;

    /// <summary>The statistics are kept 14 days by default; a period starting earlier is read from the daily totals.</summary>
    private static readonly TimeSpan StatisticsReach = TimeSpan.FromDays(13);

    public AvailabilityReportData Collect(ReportRequest request, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        var window = request.Window;
        var zone = store.ReportZone();
        var elements = store.GetAllElements();
        var byId = elements.ToDictionary(element => element.Id);
        var definitions = store.GetSensorDefinitions().ToDictionary(definition => definition.Key, StringComparer.OrdinalIgnoreCase);

        var sensorIds = store.ResolveTargetSensors(request.TargetToken).Select(sensor => sensor.Id).ToHashSet();
        var sensors = elements.OfType<SensorElement>()
            .Where(sensor => sensorIds.Contains(sensor.Id))
            .Select(sensor => (Sensor: sensor, Path: Path(sensor.Id, byId)))
            .OrderBy(entry => entry.Path, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        var notes = new List<string>();
        if (sensors.Length > MaxSensors)
        {
            notes.Add($"The target holds {sensors.Length} sensors; the report covers the first {MaxSensors} by name.");
            sensors = sensors.Take(MaxSensors).ToArray();
        }

        var latest = store.GetLatestSensorObservations();
        var useDaily = window.FromUtc < nowUtc - StatisticsReach;
        var dailyByIds = useDaily
            ? store.GetDailyStates(sensors.Select(entry => entry.Sensor.Id).ToArray(), DateOnly.FromDateTime(window.FromUtc.UtcDateTime))
                .GroupBy(day => day.SensorId)
                .ToDictionary(group => group.Key, group => group.ToArray())
            : [];
        var fromDay = DateOnly.FromDateTime(window.FromUtc.UtcDateTime);
        var toDay = DateOnly.FromDateTime(window.ToUtc.UtcDateTime);
        if (window.ToUtc.UtcDateTime.TimeOfDay > TimeSpan.Zero)
        {
            toDay = toDay.AddDays(1);
        }

        var rows = new List<ReportSensorRow>(sensors.Length);
        var outages = new List<ReportOutage>();
        var alerts = new List<ReportAlertRow>();
        var charts = new List<(ReportChart Chart, double? Uptime, TimeSpan Downtime)>();
        var summaries = new List<UptimeSummary>();
        var usedRaw = false;
        var usedStatistics = false;
        var usedDaily = false;
        var anyDailyGap = false;

        foreach (var (sensor, path) in sensors)
        {
            latest.TryGetValue(sensor.Id, out var last);
            var raw = store.GetSensorObservationsBetween(sensor.Id, window.FromUtc, window.ToUtc, MaxRawPerSensor);
            var rawCovers = raw.Count > 0 && raw[0].TimestampUtc <= window.FromUtc + Slack(window);

            // --- availability: raw checks, else statistics buckets, else daily totals ---
            UptimeSummary uptime;
            IReadOnlyList<SensorStatisticsBucket>? buckets = null;
            if (rawCovers)
            {
                uptime = FromObservations(raw);
                usedRaw = true;
            }
            else if (!useDaily)
            {
                buckets = store.GetSensorStatistics(sensor.Id, window.FromUtc.AddDays(-1))
                    .Where(bucket => bucket.BucketStartUtc.AddMinutes(bucket.BucketMinutes) > window.FromUtc && bucket.BucketStartUtc < window.ToUtc)
                    .ToArray();
                uptime = SensorUptime.FromBuckets(buckets, DateTimeOffset.MinValue);
                usedStatistics |= uptime.HasData;
                if (!uptime.HasData && raw.Count > 0)
                {
                    uptime = FromObservations(raw);
                    usedRaw = true;
                }
            }
            else
            {
                var days = dailyByIds.TryGetValue(sensor.Id, out var found) ? found : [];
                uptime = ReportMath.FromDailyStates(days, fromDay, toDay);
                usedDaily |= uptime.HasData;
                var expectedDays = toDay.DayNumber - fromDay.DayNumber;
                anyDailyGap |= days.Select(day => day.Day).Where(day => day >= fromDay && day < toDay).Distinct().Count() < expectedDays;
            }

            summaries.Add(uptime);

            // --- states over time, outages, downtime: the status history ---
            var history = SensorStatusHistory.Build(store.GetElementEvents(sensor.Id), store.GetElementAlerts(sensor.Id), nowUtc);
            var spans = ReportMath.StateSpans(history, window.FromUtc, window.ToUtc, nowUtc, sensor.IsPaused ? SensorState.Paused : last?.State);
            var messages = history.Where(entry => entry.Kind == SensorStatusKind.StateChanged && entry.ToState == SensorState.Critical)
                .GroupBy(entry => entry.TimestampUtc)
                .ToDictionary(group => group.Key, group => group.First().Message);
            var sensorOutages = ReportMath.Outages(spans, sensor.Id, sensor.Name, path, nowUtc,
                at => messages.TryGetValue(at, out var message) && !string.IsNullOrWhiteSpace(message) ? message : null);
            outages.AddRange(sensorOutages);
            var downtime = ReportMath.TimeIn(spans, SensorState.Critical);
            var warningTime = ReportMath.TimeIn(spans, SensorState.Warning);

            // --- the main reading: raw checks, else the statistics of its channel ---
            // The channel comes from the newest check that measured something (the latest may be a failure without channels).
            var (channelKey, channelLabel, unit) = MainChannel(raw.LastOrDefault(observation => observation.Channels.Any(channel => !channel.IsVirtual && channel.Value.HasValue)) ?? last);
            ReportChannelStats? channel = null;
            IReadOnlyList<(DateTimeOffset At, double Value)> points = [];
            var fromAverages = false;
            // Only a reading a channel really measured: a failed check carries none, and the bare value of a sensor without
            // channels is its state (a 0 for every failure) - charting that would draw a made-up measurement.
            if (channelKey is not null && raw.Count > 0 && (rawCovers || window.Length <= TimeSpan.FromDays(3)))
            {
                points = raw
                    .Select(observation => (observation.TimestampUtc, Value: ReadingOf(observation, channelKey)))
                    .Where(point => point.Value is { } value && double.IsFinite(value))
                    .Select(point => (point.TimestampUtc, point.Value!.Value))
                    .ToArray();
            }

            if (channelKey is not null && points.Count == 0 && !useDaily)
            {
                buckets ??= store.GetSensorStatistics(sensor.Id, window.FromUtc.AddDays(-1))
                    .Where(bucket => bucket.BucketStartUtc.AddMinutes(bucket.BucketMinutes) > window.FromUtc && bucket.BucketStartUtc < window.ToUtc)
                    .ToArray();
                var ofChannel = BucketsOf(buckets, channelKey);
                if (ofChannel.Length > 0)
                {
                    points = ofChannel
                        .Select(bucket => (bucket.BucketStartUtc.AddMinutes(bucket.BucketMinutes / 2d), bucket.Average!.Value))
                        .Where(point => point.Item1 >= window.FromUtc && point.Item1 <= window.ToUtc)
                        .ToArray();
                    fromAverages = true;
                    var weighted = ofChannel.Sum(bucket => bucket.Average!.Value * Math.Max(1, bucket.SampleCount)) / ofChannel.Sum(bucket => Math.Max(1, bucket.SampleCount));
                    channel = new ReportChannelStats(channelLabel, unit, weighted,
                        ofChannel.Min(bucket => bucket.Minimum),
                        ofChannel.Max(bucket => bucket.Maximum));
                }
            }

            if (channel is null && points.Count > 0 && !fromAverages)
            {
                var (average, minimum, maximum) = ReportMath.Summarize(points.Select(point => point.Value));
                channel = new ReportChannelStats(channelLabel, unit, average, minimum, maximum);
            }

            if (points.Count > 0)
            {
                charts.Add((new ReportChart(
                    sensor.Id,
                    sensor.Name,
                    Machine(sensor.Id, byId) is { } machine ? $"{machine} · {channelLabel}" : channelLabel,
                    unit,
                    ReportMath.Downsample(points, window.FromUtc, window.ToUtc, ChartPoints),
                    spans,
                    fromAverages), uptime.Percent, downtime));
            }

            rows.Add(new ReportSensorRow(
                sensor.Id,
                sensor.Name,
                path,
                Machine(sensor.Id, byId),
                definitions.TryGetValue(sensor.SensorTypeKey, out var definition) && !string.IsNullOrWhiteSpace(definition.DisplayName) ? definition.DisplayName : sensor.SensorTypeKey,
                sensor.IsPaused ? SensorState.Paused : last?.State ?? SensorState.Unknown,
                uptime.Percent,
                uptime.Samples,
                downtime,
                warningTime,
                sensorOutages.Count,
                sensorOutages.Count == 0 ? null : sensorOutages.Max(outage => outage.Duration),
                channel,
                request.SlaTargetPercent is { } target && uptime.Percent is { } percent && percent < target));

            foreach (var alert in store.GetElementAlerts(sensor.Id))
            {
                var end = alert.RecoveredUtc ?? alert.ResolvedUtc;
                if (alert.FirstSeenUtc >= window.ToUtc || (end ?? nowUtc) <= window.FromUtc)
                {
                    continue;
                }

                alerts.Add(new ReportAlertRow(alert.FirstSeenUtc, end, path, alert.State, alert.Message ?? string.Empty, alert.AcknowledgedBy, end is null));
            }
        }

        if (usedDaily)
        {
            notes.Add("Availability over this period comes from the daily totals (whole UTC days).");
            if (anyDailyGap)
            {
                notes.Add("Some days have no totals - they are recorded from the version that brought status pages on, and for sensors that existed then.");
            }
        }
        else if (usedStatistics && usedRaw)
        {
            notes.Add("Availability is counted from the checks where they are still kept, else from the hourly / daily statistics.");
        }

        if (outages.Count > 0 || rows.Count > 0)
        {
            notes.Add("Downtime and outages come from the recorded state changes: time in Error, from the first failing check until the first good one.");
        }

        var orderedOutages = outages.OrderByDescending(outage => outage.StartUtc).ToList();
        if (orderedOutages.Count > MaxOutages)
        {
            notes.Add($"{orderedOutages.Count} outages - the report lists the newest {MaxOutages}.");
            orderedOutages = orderedOutages.Take(MaxOutages).ToList();
        }

        // Charts: the sensors that are most worth a look - lowest availability, most downtime - then by name.
        var chosenCharts = charts
            .OrderBy(entry => entry.Uptime ?? 101)
            .ThenByDescending(entry => entry.Downtime)
            .Take(MaxCharts)
            .Select(entry => entry.Chart)
            .ToArray();

        return new AvailabilityReportData
        {
            Title = string.IsNullOrWhiteSpace(request.Title) ? "Availability report" : request.Title.Trim(),
            Scope = DescribeTarget(request.TargetToken, byId),
            PreparedFor = request.PreparedFor,
            Note = request.Note,
            Window = window,
            GeneratedUtc = nowUtc,
            Zone = zone,
            Sections = request.Sections == ReportSections.None ? ReportSections.All : request.Sections,
            SlaTargetPercent = request.SlaTargetPercent,
            Overall = SensorUptime.Combine(summaries),
            TotalDowntime = rows.Aggregate(TimeSpan.Zero, (sum, row) => sum + row.Downtime),
            SensorCount = rows.Count,
            SensorsWithoutData = rows.Count(row => row.UptimePercent is null),
            MachineCount = rows.Select(row => row.Machine).Where(machine => machine is not null).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            Sensors = rows,
            Outages = orderedOutages,
            Alerts = alerts.OrderByDescending(alert => alert.FirstSeenUtc).Take(500).ToArray(),
            Charts = chosenCharts,
            ChartsLeftOut = Math.Max(0, charts.Count - chosenCharts.Length),
            DataNotes = notes,
            Branding = Branding(store)
        };
    }

    /// <summary>The managing partner's branding for a report - null without a partner or when the customer opted out.</summary>
    public static SummaryReportBranding? Branding(IMonitoringWorkspaceStore store)
    {
        var partner = store.GetServicePartnerInfo();
        return partner is { HasPartner: true, BrandingSuppressed: false }
            ? new SummaryReportBranding(partner.Name, partner.LogoPng, partner.LogoContentType, partner.BrandColor, partner.ContactUrl, partner.ProductName, partner.Slogan, partner.BrandColorSecondary)
            : null;
    }

    /// <summary>What a target token covers, in words.</summary>
    public string DescribeTarget(string? token) => DescribeTarget(token, store.GetAllElements().ToDictionary(element => element.Id));

    private static string DescribeTarget(string? token, IReadOnlyDictionary<Guid, MonitoringElement> byId)
    {
        if (MonitoringTargetResolver.TagName(token) is { } tag)
        {
            return $"Everything tagged #{tag}";
        }

        if (MonitoringTargetResolver.ElementId(token) is { } id && byId.ContainsKey(id))
        {
            return Path(id, byId);
        }

        return "An element that no longer exists";
    }

    // A raw series counts as covering the period when it starts close to the period's start.
    private static TimeSpan Slack(ReportWindow window) =>
        TimeSpan.FromTicks(Math.Max(TimeSpan.FromHours(2).Ticks, window.Length.Ticks / 20));

    private static UptimeSummary FromObservations(IReadOnlyList<SensorObservation> observations)
    {
        long healthy = 0, warning = 0, critical = 0;
        foreach (var observation in observations)
        {
            switch (observation.State)
            {
                case SensorState.Healthy:
                    healthy++;
                    break;
                case SensorState.Warning:
                    warning++;
                    break;
                case SensorState.Critical:
                    critical++;
                    break;
            }
        }

        var all = healthy + warning + critical;
        return new UptimeSummary(healthy, warning, critical, observations.Count, all > 0 ? (double)(healthy + warning) / all * 100 : null);
    }

    private static (string? Key, string Label, string? Unit) MainChannel(SensorObservation? observation)
    {
        if (observation?.Channels is not { Count: > 0 } channels)
        {
            return (null, "Value", null);
        }

        var chosen = channels.FirstOrDefault(channel => !channel.IsVirtual && channel.Value.HasValue
                         && string.Equals(channel.Key, observation.DefaultChannelKey, StringComparison.OrdinalIgnoreCase))
                     ?? channels.FirstOrDefault(channel => channel.IsDefault && !channel.IsVirtual && channel.Value.HasValue)
                     ?? channels.FirstOrDefault(channel => !channel.IsVirtual && channel.Value.HasValue);
        return chosen is null
            ? (null, "Value", null)
            : (chosen.Key, string.IsNullOrWhiteSpace(chosen.Label) ? chosen.Key : chosen.Label, chosen.Unit);
    }

    private static double? ReadingOf(SensorObservation observation, string? channelKey)
    {
        if (channelKey is null)
        {
            return observation.Value;
        }

        var channel = observation.Channels.FirstOrDefault(candidate => string.Equals(candidate.Key, channelKey, StringComparison.OrdinalIgnoreCase));
        return channel?.Value;
    }

    private static SensorStatisticsBucket[] BucketsOf(IReadOnlyList<SensorStatisticsBucket> buckets, string? channelKey)
    {
        var withAverage = buckets.Where(bucket => bucket.Average.HasValue).ToArray();
        if (withAverage.Length == 0)
        {
            return [];
        }

        var key = channelKey is not null && withAverage.Any(bucket => string.Equals(bucket.DefaultChannelKey, channelKey, StringComparison.OrdinalIgnoreCase))
            ? channelKey
            : withAverage.Any(bucket => string.Equals(bucket.DefaultChannelKey, "default", StringComparison.OrdinalIgnoreCase))
                ? "default"
                : withAverage.GroupBy(bucket => bucket.DefaultChannelKey, StringComparer.OrdinalIgnoreCase).MaxBy(group => group.Count())!.Key;
        var ofChannel = withAverage.Where(bucket => string.Equals(bucket.DefaultChannelKey, key, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (ofChannel.Length == 0)
        {
            return [];
        }

        // One bucket size, the finest a sensor has - mixing hourly and daily averages would draw a staircase.
        var finest = ofChannel.Min(bucket => bucket.BucketMinutes);
        return ofChannel.Where(bucket => bucket.BucketMinutes == finest).OrderBy(bucket => bucket.BucketStartUtc).ToArray();
    }

    private static string? Machine(Guid elementId, IReadOnlyDictionary<Guid, MonitoringElement> byId)
    {
        var current = elementId;
        var guard = 0;
        while (byId.TryGetValue(current, out var element) && guard++ < 256)
        {
            if (element is HostElement host)
            {
                return host.Name;
            }

            if (element.ParentId is not Guid parent)
            {
                break;
            }

            current = parent;
        }

        return null;
    }

    private static string Path(Guid elementId, IReadOnlyDictionary<Guid, MonitoringElement> byId)
    {
        var names = new List<string>();
        var current = elementId;
        var guard = 0;
        while (byId.TryGetValue(current, out var element) && guard++ < 256)
        {
            names.Add(element.Name);
            if (element.ParentId is not Guid parent)
            {
                break;
            }

            current = parent;
        }

        names.Reverse();
        return string.Join(" / ", names);
    }
}
