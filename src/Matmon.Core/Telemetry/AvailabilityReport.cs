using System.Globalization;
using System.Text;
using Matmon.Core.Domain;

namespace Matmon.Core.Telemetry;

/// <summary>A stretch of time a sensor spent in one state (inside the report period).</summary>
public sealed record ReportStateSpan(DateTimeOffset StartUtc, DateTimeOffset EndUtc, SensorState State)
{
    public TimeSpan Duration => EndUtc - StartUtc;
}

/// <summary>An outage: a stretch in Error, clipped to the report period. <see cref="Ongoing"/> = still in Error at the end.</summary>
public sealed record ReportOutage(Guid SensorId, string SensorName, string SensorPath, DateTimeOffset StartUtc, DateTimeOffset EndUtc, bool Ongoing, string? Message)
{
    public TimeSpan Duration => EndUtc - StartUtc;
}

/// <summary>Average / lowest / highest of the sensor's main reading over the period.</summary>
public sealed record ReportChannelStats(string Label, string? Unit, double? Average, double? Minimum, double? Maximum);

/// <summary>One sensor's line in the report.</summary>
/// <param name="UptimePercent">From the state counts (a warning counts as up); null = no data in the period.</param>
/// <param name="Downtime">Time in Error, from the status history.</param>
/// <param name="BelowTarget">Below the agreed availability.</param>
public sealed record ReportSensorRow(
    Guid SensorId,
    string Name,
    string Path,
    string? Machine,
    string TypeName,
    SensorState CurrentState,
    double? UptimePercent,
    long Samples,
    TimeSpan Downtime,
    TimeSpan WarningTime,
    int OutageCount,
    TimeSpan? LongestOutage,
    ReportChannelStats? Channel,
    bool BelowTarget);

/// <summary>An alert raised in (or still open during) the period.</summary>
public sealed record ReportAlertRow(
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset? EndUtc,
    string SensorPath,
    SensorState State,
    string Message,
    string? AcknowledgedBy,
    bool Open)
{
    public TimeSpan DurationUntil(DateTimeOffset nowUtc) => (EndUtc ?? nowUtc) - FirstSeenUtc;
}

/// <summary>A chart: one sensor's main reading over the period, and its states as a band under it.</summary>
/// <param name="FromAverages">The points are hourly / daily averages (the raw checks of that time are no longer kept).</param>
public sealed record ReportChart(
    Guid SensorId,
    string Title,
    string Subtitle,
    string? Unit,
    IReadOnlyList<(DateTimeOffset At, double Value)> Points,
    IReadOnlyList<ReportStateSpan> States,
    bool FromAverages);

/// <summary>Everything a report shows, gathered once and rendered as PDF, CSV or an e-mail summary.</summary>
public sealed record AvailabilityReportData
{
    public required string Title { get; init; }

    /// <summary>What it covers, in words ("Customer A / Office", "tag #backup").</summary>
    public required string Scope { get; init; }

    public string? PreparedFor { get; init; }

    public string? Note { get; init; }

    public required ReportWindow Window { get; init; }

    public required DateTimeOffset GeneratedUtc { get; init; }

    public required TimeZoneInfo Zone { get; init; }

    public ReportSections Sections { get; init; } = ReportSections.All;

    public double? SlaTargetPercent { get; init; }

    public UptimeSummary Overall { get; init; }

    public TimeSpan TotalDowntime { get; init; }

    public int SensorCount { get; init; }

    public int SensorsWithoutData { get; init; }

    public int MachineCount { get; init; }

    public IReadOnlyList<ReportSensorRow> Sensors { get; init; } = [];

    public IReadOnlyList<ReportOutage> Outages { get; init; } = [];

    public IReadOnlyList<ReportAlertRow> Alerts { get; init; } = [];

    public IReadOnlyList<ReportChart> Charts { get; init; } = [];

    /// <summary>How many sensors have a chart of their own (the rest are left out - noted under the charts).</summary>
    public int ChartsLeftOut { get; init; }

    /// <summary>What the numbers are made from, when it matters ("availability from daily totals").</summary>
    public IReadOnlyList<string> DataNotes { get; init; } = [];

    /// <summary>The managing partner's branding (logo, colours, "Managed by"); null = Matmon's own look.</summary>
    public SummaryReportBranding? Branding { get; init; }

    public int OutageCount => Outages.Count;

    public int SensorsBelowTarget => Sensors.Count(row => row.BelowTarget);

    /// <summary>Sensors that had at least one outage - the summary says this rather than downtime summed over sensors
    /// (twenty sensors down for the same hour would read as twenty hours).</summary>
    public int SensorsWithOutages => Sensors.Count(row => row.OutageCount > 0);

    public TimeSpan LongestOutage => Outages.Count == 0 ? TimeSpan.Zero : Outages.Max(outage => outage.Duration);

    public string LocalTime(DateTimeOffset utc, string format = "dd.MM.yyyy HH:mm") =>
        TimeZoneInfo.ConvertTime(utc, Zone).ToString(format, CultureInfo.InvariantCulture);
}

/// <summary>The pure arithmetic of reports - states over time, outages, availability from daily totals - tested without a store.</summary>
public static class ReportMath
{
    /// <summary>
    /// The states a sensor was in during [from, to), from its status history (state changes, pauses). The state at
    /// <paramref name="fromUtc"/> is the last change before it; time before the first known state is left out (never
    /// guessed); the period ends at <paramref name="nowUtc"/> at the latest. A pause holds until the resume, after which
    /// the state before the pause holds until the next change.
    /// </summary>
    /// <param name="assumedState">The state to assume over the whole period when the history holds no state change at
    /// all (a sensor that has not changed since its events were pruned) - its current state.</param>
    public static IReadOnlyList<ReportStateSpan> StateSpans(IEnumerable<SensorStatusEntry> history, DateTimeOffset fromUtc, DateTimeOffset toUtc, DateTimeOffset nowUtc, SensorState? assumedState = null)
    {
        var end = toUtc < nowUtc ? toUtc : nowUtc;
        if (end <= fromUtc)
        {
            return [];
        }

        var changes = new List<(DateTimeOffset At, SensorState State)>();
        SensorState? beforePause = null;
        SensorState? current = null;
        foreach (var entry in history.OrderBy(entry => entry.TimestampUtc))
        {
            switch (entry.Kind)
            {
                case SensorStatusKind.StateChanged when entry.ToState is { } state:
                    if (current == SensorState.Paused)
                    {
                        // A check while paused cannot happen; a state change means the sensor runs again.
                        beforePause = null;
                    }

                    current = state;
                    changes.Add((entry.TimestampUtc, state));
                    break;
                case SensorStatusKind.Paused:
                    if (current != SensorState.Paused)
                    {
                        beforePause = current;
                        current = SensorState.Paused;
                        changes.Add((entry.TimestampUtc, SensorState.Paused));
                    }

                    break;
                case SensorStatusKind.Resumed:
                    if (current == SensorState.Paused)
                    {
                        current = beforePause ?? SensorState.Unknown;
                        beforePause = null;
                        changes.Add((entry.TimestampUtc, current.Value));
                    }

                    break;
            }
        }

        if (changes.Count == 0 && assumedState is { } assumed)
        {
            changes.Add((fromUtc, assumed));
        }

        var spans = new List<ReportStateSpan>();
        for (var i = 0; i < changes.Count; i++)
        {
            var start = changes[i].At;
            var stop = i + 1 < changes.Count ? changes[i + 1].At : end;
            if (stop <= fromUtc || start >= end)
            {
                continue;
            }

            var clippedStart = start < fromUtc ? fromUtc : start;
            var clippedStop = stop > end ? end : stop;
            if (clippedStop <= clippedStart)
            {
                continue;
            }

            if (spans.Count > 0 && spans[^1].State == changes[i].State && spans[^1].EndUtc == clippedStart)
            {
                spans[^1] = spans[^1] with { EndUtc = clippedStop };
            }
            else
            {
                spans.Add(new ReportStateSpan(clippedStart, clippedStop, changes[i].State));
            }
        }

        return spans;
    }

    /// <summary>The Error stretches of <paramref name="spans"/> as outages. The last one is ongoing when it reaches
    /// <paramref name="nowUtc"/> (the period is not over yet, or ended now).</summary>
    public static IReadOnlyList<ReportOutage> Outages(
        IReadOnlyList<ReportStateSpan> spans,
        Guid sensorId,
        string sensorName,
        string sensorPath,
        DateTimeOffset nowUtc,
        Func<DateTimeOffset, string?>? messageAt = null)
    {
        var result = new List<ReportOutage>();
        foreach (var span in spans.Where(span => span.State == SensorState.Critical))
        {
            var ongoing = span.EndUtc >= nowUtc.AddSeconds(-1);
            result.Add(new ReportOutage(sensorId, sensorName, sensorPath, span.StartUtc, span.EndUtc, ongoing, messageAt?.Invoke(span.StartUtc)));
        }

        return result;
    }

    /// <summary>Total time per state.</summary>
    public static TimeSpan TimeIn(IEnumerable<ReportStateSpan> spans, SensorState state) =>
        spans.Where(span => span.State == state).Aggregate(TimeSpan.Zero, (sum, span) => sum + span.Duration);

    /// <summary>Availability from the daily totals of the UTC days [fromDay, toDay) - for periods longer than the
    /// statistics keep (14 days by default). The same rule as everywhere: a warning counts as up.</summary>
    public static UptimeSummary FromDailyStates(IEnumerable<SensorDailyState> days, DateOnly fromDay, DateOnly toDayExclusive)
    {
        long healthy = 0, warning = 0, critical = 0;
        foreach (var day in days)
        {
            if (day.Day < fromDay || day.Day >= toDayExclusive)
            {
                continue;
            }

            healthy += day.Healthy;
            warning += day.Warning;
            critical += day.Critical;
        }

        var all = healthy + warning + critical;
        return new UptimeSummary(healthy, warning, critical, all, all > 0 ? (double)(healthy + warning) / all * 100 : null);
    }

    /// <summary>Average / lowest / highest of a series (null values left out).</summary>
    public static (double? Average, double? Minimum, double? Maximum) Summarize(IEnumerable<double> values)
    {
        double sum = 0, min = double.MaxValue, max = double.MinValue;
        var count = 0;
        foreach (var value in values)
        {
            if (!double.IsFinite(value))
            {
                continue;
            }

            sum += value;
            count++;
            min = Math.Min(min, value);
            max = Math.Max(max, value);
        }

        return count == 0 ? (null, null, null) : (sum / count, min, max);
    }

    /// <summary>At most <paramref name="buckets"/> points: the average of each equal time slice of the period.</summary>
    public static IReadOnlyList<(DateTimeOffset At, double Value)> Downsample(IReadOnlyList<(DateTimeOffset At, double Value)> points, DateTimeOffset fromUtc, DateTimeOffset toUtc, int buckets)
    {
        if (points.Count <= buckets || buckets <= 0 || toUtc <= fromUtc)
        {
            return points;
        }

        var length = (toUtc - fromUtc).Ticks;
        return points
            .GroupBy(point => Math.Clamp((int)((point.At - fromUtc).Ticks * (double)buckets / length), 0, buckets - 1))
            .OrderBy(group => group.Key)
            .Select(group => (new DateTimeOffset(Convert.ToInt64(group.Average(point => (double)point.At.UtcTicks)), TimeSpan.Zero), group.Average(point => point.Value)))
            .ToArray();
    }

    /// <summary>A span in words, short ("3 h 20 min", "2 d 4 h").</summary>
    public static string Span(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        if (span.TotalSeconds < 60)
        {
            return $"{(int)span.TotalSeconds} s";
        }

        if (span.TotalMinutes < 60)
        {
            return $"{(int)span.TotalMinutes} min";
        }

        if (span.TotalHours < 24)
        {
            return span.Minutes == 0 ? $"{(int)span.TotalHours} h" : $"{(int)span.TotalHours} h {span.Minutes} min";
        }

        return span.Hours == 0 ? $"{(int)span.TotalDays} d" : $"{(int)span.TotalDays} d {span.Hours} h";
    }

    /// <summary>A percentage as printed: three decimals near 100 % (99.987 %), else one.</summary>
    public static string Percent(double? percent)
    {
        if (percent is not { } value)
        {
            return "n/a";
        }

        var format = value >= 99 && value < 100 ? "0.000" : "0.#";
        return value.ToString(format, CultureInfo.InvariantCulture) + " %";
    }

    /// <summary>A reading with its unit.</summary>
    public static string Value(double? value, string? unit)
    {
        if (value is not { } number)
        {
            return "-";
        }

        var format = Math.Abs(number) >= 100 ? "0" : Math.Abs(number) >= 10 ? "0.#" : "0.##";
        var text = number.ToString(format, CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(unit) ? text : $"{text} {unit}";
    }
}

/// <summary>The CSV forms of a report: one row per sensor, or one per outage. RFC 4180 (comma, quotes), invariant numbers,
/// times in the report's time zone, UTF-8 with a byte order mark so a spreadsheet opens umlauts correctly.</summary>
public static class ReportCsv
{
    public static byte[] Sensors(AvailabilityReportData data)
    {
        var sb = new StringBuilder();
        Row(sb, "Sensor", "Path", "Machine", "Type", "State now", "Availability %", "Checks", "Downtime (minutes)", "Warning time (minutes)",
            "Outages", "Longest outage (minutes)", "Reading", "Unit", "Average", "Minimum", "Maximum", "Below agreed availability");
        foreach (var row in data.Sensors)
        {
            Row(sb,
                row.Name,
                row.Path,
                row.Machine ?? string.Empty,
                row.TypeName,
                MonitoringStatePresentation.Label(row.CurrentState),
                Number(row.UptimePercent, "0.####"),
                row.Samples.ToString(CultureInfo.InvariantCulture),
                Number(row.Downtime.TotalMinutes, "0.#"),
                Number(row.WarningTime.TotalMinutes, "0.#"),
                row.OutageCount.ToString(CultureInfo.InvariantCulture),
                Number(row.LongestOutage?.TotalMinutes, "0.#"),
                row.Channel?.Label ?? string.Empty,
                row.Channel?.Unit ?? string.Empty,
                Number(row.Channel?.Average, "0.####"),
                Number(row.Channel?.Minimum, "0.####"),
                Number(row.Channel?.Maximum, "0.####"),
                data.SlaTargetPercent is null ? string.Empty : row.BelowTarget ? "yes" : "no");
        }

        return Encode(sb);
    }

    public static byte[] Outages(AvailabilityReportData data)
    {
        var sb = new StringBuilder();
        Row(sb, "Start", "End", "Duration (minutes)", "Ongoing", "Sensor", "Path", "Message");
        foreach (var outage in data.Outages)
        {
            Row(sb,
                data.LocalTime(outage.StartUtc, "yyyy-MM-dd HH:mm:ss"),
                data.LocalTime(outage.EndUtc, "yyyy-MM-dd HH:mm:ss"),
                Number(outage.Duration.TotalMinutes, "0.#"),
                outage.Ongoing ? "yes" : "no",
                outage.SensorName,
                outage.SensorPath,
                outage.Message ?? string.Empty);
        }

        return Encode(sb);
    }

    /// <summary>One CSV line, every field quoted when it needs to be. A field starting with = + - @ is prefixed with an
    /// apostrophe: a sensor name or a check message comes from the network, and a spreadsheet would run it as a formula.</summary>
    public static void Row(StringBuilder sb, params string[] fields)
    {
        for (var i = 0; i < fields.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append(Field(fields[i]));
        }

        sb.Append("\r\n");
    }

    public static string Field(string? value)
    {
        var text = value ?? string.Empty;
        if (text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r' && !IsPlainNumber(text))
        {
            text = "'" + text;
        }

        return text.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? "\"" + text.Replace("\"", "\"\"") + "\""
            : text;
    }

    private static bool IsPlainNumber(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    public static byte[] Encode(StringBuilder sb) => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble()
        .Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();

    private static string Number(double? value, string format) =>
        value is { } number && double.IsFinite(number) ? number.ToString(format, CultureInfo.InvariantCulture) : string.Empty;
}

/// <summary>The chart of a report as SVG (line + area over time, the state band under it) - drawn here, pure, so the PDF
/// gets a vector picture and the tests can read it. The labels are left to the caller (the PDF lays them out as text).</summary>
public static class ReportChartSvg
{
    public const double Width = 480;
    public const double Height = 110;
    public const double PlotHeight = 96;
    public const double BandTop = 101;
    public const double BandHeight = 7;

    /// <summary>The axis bounds of a chart (round numbers; a percentage stays within 0..100).</summary>
    public static (double Min, double Max) Scale(ReportChart chart)
    {
        if (chart.Points.Count == 0)
        {
            return (0, 1);
        }

        var min = chart.Points.Min(point => point.Value);
        var max = chart.Points.Max(point => point.Value);
        var percent = chart.Unit?.Trim() == "%";
        if (min > 0 && (percent || min < max * 0.5))
        {
            min = 0;
        }

        if (min >= 0 && max <= 0)
        {
            return (0, 1); // a flat zero line - widening it around 0 would invent a negative axis
        }

        var (niceMin, niceMax) = SparklineGeometry.NiceScale(min, max, percent);
        return (min >= 0 ? Math.Max(0, niceMin) : niceMin, niceMax);
    }

    public static string Render(ReportChart chart, DateTimeOffset fromUtc, DateTimeOffset toUtc, string accentHex)
    {
        var accent = BrandingSafety.SafeHexColor(accentHex) ?? "#2563EB";
        var (min, max) = Scale(chart);
        var length = Math.Max(1, (toUtc - fromUtc).Ticks);
        double X(DateTimeOffset at) => Math.Clamp((at - fromUtc).Ticks / (double)length, 0, 1) * Width;
        double Y(double value) => PlotHeight - (max > min ? (value - min) / (max - min) : 0.5) * (PlotHeight - 4) - 2;

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {Width} {Height}\" width=\"{Width}\" height=\"{Height}\">");

        // Grid: top, middle, bottom of the plot.
        foreach (var fraction in new[] { 0d, 0.5, 1d })
        {
            var y = Fmt(2 + fraction * (PlotHeight - 4));
            // A thin rect, not a <line>: the PDF renderer drew some stroked lines black.
            sb.Append(CultureInfo.InvariantCulture, $"<rect x=\"0\" y=\"{y}\" width=\"{Width}\" height=\"0.6\" fill=\"#E5E7EB\"/>");
        }

        // The line breaks where the checks stop for longer than five times their usual gap (a sensor that was paused or
        // gone): drawing straight across would invent readings.
        var points = chart.Points.OrderBy(point => point.At).ToArray();
        if (points.Length > 0)
        {
            var gaps = points.Zip(points.Skip(1), (a, b) => (b.At - a.At).Ticks).OrderBy(gap => gap).ToArray();
            var typical = gaps.Length > 0 ? gaps[gaps.Length / 2] : length;
            var breakAfter = Math.Max(typical * 5, length / 60);

            var segments = new List<List<(double X, double Y)>> { new() };
            for (var i = 0; i < points.Length; i++)
            {
                if (i > 0 && (points[i].At - points[i - 1].At).Ticks > breakAfter)
                {
                    segments.Add([]);
                }

                segments[^1].Add((X(points[i].At), Y(points[i].Value)));
            }

            foreach (var segment in segments.Where(segment => segment.Count > 0))
            {
                if (segment.Count == 1)
                {
                    sb.Append(CultureInfo.InvariantCulture, $"<circle cx=\"{Fmt(segment[0].X)}\" cy=\"{Fmt(segment[0].Y)}\" r=\"1.6\" fill=\"{accent}\"/>");
                    continue;
                }

                var line = string.Join(" ", segment.Select(point => $"{Fmt(point.X)},{Fmt(point.Y)}"));
                var area = $"{Fmt(segment[0].X)},{Fmt(PlotHeight)} {line} {Fmt(segment[^1].X)},{Fmt(PlotHeight)}";
                sb.Append(CultureInfo.InvariantCulture, $"<polygon points=\"{area}\" fill=\"{accent}\" fill-opacity=\"0.12\"/>");
                sb.Append(CultureInfo.InvariantCulture, $"<polyline points=\"{line}\" fill=\"none\" stroke=\"{accent}\" stroke-width=\"1.3\" stroke-linejoin=\"round\" stroke-linecap=\"round\"/>");
            }
        }

        // The state band: what the sensor reported, the same colours as everywhere in Matmon; no colour = no data.
        sb.Append(CultureInfo.InvariantCulture, $"<rect x=\"0\" y=\"{Fmt(BandTop)}\" width=\"{Width}\" height=\"{BandHeight}\" fill=\"#F3F4F6\"/>");
        foreach (var span in chart.States)
        {
            var x1 = X(span.StartUtc);
            var x2 = X(span.EndUtc);
            var width = Math.Max(0.6, x2 - x1);
            sb.Append(CultureInfo.InvariantCulture, $"<rect x=\"{Fmt(x1)}\" y=\"{Fmt(BandTop)}\" width=\"{Fmt(width)}\" height=\"{BandHeight}\" fill=\"{StateColor(span.State)}\"/>");
        }

        sb.Append("</svg>");
        return sb.ToString();
    }

    public static string StateColor(SensorState state) => state switch
    {
        SensorState.Critical => "#DC2626",
        SensorState.Warning => "#D97706",
        SensorState.Healthy => "#16A34A",
        SensorState.Paused => "#9CA3AF",
        _ => "#D1D5DB"
    };

    private static string Fmt(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
