namespace Matmon.Core.Domain;

/// <summary>
/// A public status page: a handful of COMPONENTS ("Website", "E-mail", "VPN") whose state comes from the sensors
/// behind them, a bar per day for the last weeks, the maintenance that covers them and incident messages an operator
/// writes. It answers "is it me or is it them" for the people who use a service, in their words - which is why a
/// component is named by the admin and never shows a sensor name, a host or an address.
/// </summary>
public sealed class StatusPage
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "Status";

    /// <summary>One line under the name ("Services of Example GmbH").</summary>
    public string? Description { get; set; }

    /// <summary>The anonymous page answers only while this is on.</summary>
    public bool PublicEnabled { get; set; }

    /// <summary>The unguessable part of the public address, rotated by the admin.</summary>
    public string PublicToken { get; set; } = string.Empty;

    /// <summary>How many days of daily bars the page shows (30, 60 or 90).</summary>
    public int UptimeDays { get; set; } = 90;

    /// <summary>List the maintenance windows that cover a component (now and the next 7 days).</summary>
    public bool ShowMaintenance { get; set; } = true;

    public List<StatusPageComponent> Components { get; set; } = [];

    public List<StatusIncident> Incidents { get; set; } = [];

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public StatusPage Clone() => new()
    {
        Id = Id,
        Name = Name,
        Description = Description,
        PublicEnabled = PublicEnabled,
        PublicToken = PublicToken,
        UptimeDays = UptimeDays,
        ShowMaintenance = ShowMaintenance,
        Components = Components.Select(component => component.Clone()).ToList(),
        Incidents = Incidents.Select(incident => incident.Clone()).ToList(),
        CreatedUtc = CreatedUtc,
        UpdatedUtc = UpdatedUtc
    };
}

public sealed class StatusPageComponent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    /// <summary>A heading the component is listed under ("Customer services", "Internal").</summary>
    public string? Group { get; set; }

    public string? Description { get; set; }

    /// <summary>Element id or "tag:name": every sensor under it counts for the component.</summary>
    public string? TargetToken { get; set; }

    public StatusPageComponent Clone() => new()
    {
        Id = Id,
        Name = Name,
        Group = Group,
        Description = Description,
        TargetToken = TargetToken
    };
}

public enum StatusIncidentImpact
{
    Degraded = 0,
    PartialOutage = 1,
    MajorOutage = 2
}

public enum StatusIncidentPhase
{
    Investigating = 0,
    Identified = 1,
    Monitoring = 2,
    Resolved = 3
}

/// <summary>An incident message: what is wrong, which components it affects, and its updates over time.</summary>
public sealed class StatusIncident
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Title { get; set; } = string.Empty;

    public StatusIncidentImpact Impact { get; set; } = StatusIncidentImpact.PartialOutage;

    public List<Guid> ComponentIds { get; set; } = [];

    /// <summary>Oldest first.</summary>
    public List<StatusIncidentUpdate> Updates { get; set; } = [];

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? ResolvedUtc { get; set; }

    public bool IsOpen => ResolvedUtc is null;

    public StatusIncidentPhase Phase => Updates.Count == 0 ? StatusIncidentPhase.Investigating : Updates[^1].Phase;

    public StatusIncident Clone() => new()
    {
        Id = Id,
        Title = Title,
        Impact = Impact,
        ComponentIds = [.. ComponentIds],
        Updates = Updates.Select(update => update.Clone()).ToList(),
        CreatedUtc = CreatedUtc,
        ResolvedUtc = ResolvedUtc
    };
}

public sealed class StatusIncidentUpdate
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTimeOffset AtUtc { get; set; } = DateTimeOffset.UtcNow;

    public StatusIncidentPhase Phase { get; set; }

    public string Message { get; set; } = string.Empty;

    /// <summary>Who wrote it - never shown on the public page.</summary>
    public string? By { get; set; }

    public StatusIncidentUpdate Clone() => new()
    {
        Id = Id,
        AtUtc = AtUtc,
        Phase = Phase,
        Message = Message,
        By = By
    };
}

/// <summary>What the public page says about a component. Ordered by how bad it is, so the worst can be taken by Max.</summary>
public enum StatusComponentState
{
    Unknown = 0,
    Operational = 1,
    Maintenance = 2,
    Degraded = 3,
    PartialOutage = 4,
    MajorOutage = 5
}

/// <summary>One sensor's state counts for one UTC day (from the statistics buckets of that day).</summary>
public readonly record struct SensorDailyState(Guid SensorId, DateOnly Day, long Healthy, long Warning, long Critical)
{
    public long StateSamples => Healthy + Warning + Critical;
}

/// <summary>One bar of the page: a day, its availability (null = no data) and its tone.</summary>
public readonly record struct StatusDayBar(DateOnly Day, double? Percent, string Tone);

/// <summary>The rules of a status page, pure so they are tested without a store.</summary>
public static class StatusPageRules
{
    public static readonly int[] UptimeDayChoices = [30, 60, 90];

    public const int MaxComponents = 50;

    /// <summary>
    /// The state of a component from the CURRENT states of its sensors: all failing = major outage, some = partial, a
    /// warning = degraded. A maintenance window that covers it says "maintenance" unless it is fine anyway, and an open
    /// incident the operator posted raises it to at least the incident's impact - the operator knows more than the
    /// sensors (a service can answer and still be broken).
    /// </summary>
    public static StatusComponentState ComponentState(
        IReadOnlyCollection<SensorState> sensorStates,
        bool inMaintenance,
        StatusIncidentImpact? openIncidentImpact)
    {
        var counted = sensorStates.Where(state => state is SensorState.Healthy or SensorState.Warning or SensorState.Critical).ToList();
        StatusComponentState measured;
        if (counted.Count == 0)
        {
            measured = StatusComponentState.Unknown;
        }
        else if (counted.All(state => state == SensorState.Critical))
        {
            measured = StatusComponentState.MajorOutage;
        }
        else if (counted.Any(state => state == SensorState.Critical))
        {
            measured = StatusComponentState.PartialOutage;
        }
        else if (counted.Any(state => state == SensorState.Warning))
        {
            measured = StatusComponentState.Degraded;
        }
        else
        {
            measured = StatusComponentState.Operational;
        }

        if (inMaintenance && measured != StatusComponentState.Operational)
        {
            measured = StatusComponentState.Maintenance;
        }

        if (openIncidentImpact is { } impact)
        {
            var fromIncident = impact switch
            {
                StatusIncidentImpact.MajorOutage => StatusComponentState.MajorOutage,
                StatusIncidentImpact.PartialOutage => StatusComponentState.PartialOutage,
                _ => StatusComponentState.Degraded
            };
            measured = (StatusComponentState)Math.Max((int)measured, (int)fromIncident);
        }

        return measured;
    }

    /// <summary>The headline of the page from its components' states.</summary>
    public static (StatusComponentState State, string Text) Overall(IReadOnlyCollection<StatusComponentState> states)
    {
        if (states.Count == 0)
        {
            return (StatusComponentState.Unknown, "No components yet");
        }

        var worst = (StatusComponentState)states.Max(state => (int)state);
        return worst switch
        {
            StatusComponentState.MajorOutage => (worst, "Major outage"),
            StatusComponentState.PartialOutage => (worst, "Partial outage"),
            StatusComponentState.Degraded => (worst, "Degraded performance"),
            StatusComponentState.Maintenance => (worst, "Maintenance in progress"),
            StatusComponentState.Unknown when states.All(state => state == StatusComponentState.Unknown) => (worst, "No data yet"),
            _ => (StatusComponentState.Operational, "All systems operational")
        };
    }

    public static string Label(StatusComponentState state) => state switch
    {
        StatusComponentState.Operational => "Operational",
        StatusComponentState.Maintenance => "Maintenance",
        StatusComponentState.Degraded => "Degraded",
        StatusComponentState.PartialOutage => "Partial outage",
        StatusComponentState.MajorOutage => "Major outage",
        _ => "No data"
    };

    /// <summary>The MatUi tone of a state (ok / info / warning / error / unknown).</summary>
    public static string Tone(StatusComponentState state) => state switch
    {
        StatusComponentState.Operational => "ok",
        StatusComponentState.Maintenance => "info",
        StatusComponentState.Degraded => "warning",
        StatusComponentState.PartialOutage => "warning",
        StatusComponentState.MajorOutage => "error",
        _ => "unknown"
    };

    public static string PhaseLabel(StatusIncidentPhase phase) => phase switch
    {
        StatusIncidentPhase.Identified => "Identified",
        StatusIncidentPhase.Monitoring => "Monitoring",
        StatusIncidentPhase.Resolved => "Resolved",
        _ => "Investigating"
    };

    public static string ImpactLabel(StatusIncidentImpact impact) => impact switch
    {
        StatusIncidentImpact.MajorOutage => "Major outage",
        StatusIncidentImpact.PartialOutage => "Partial outage",
        _ => "Degraded"
    };

    /// <summary>
    /// One bar per day for the last <paramref name="days"/> days up to <paramref name="today"/>, from the daily state
    /// counts of every sensor of a component. A Warning counts as up (the service answered - see SensorUptime); a day
    /// without any sample has no percentage and is drawn as "no data", never as 0 %.
    /// </summary>
    public static IReadOnlyList<StatusDayBar> DayBars(IEnumerable<SensorDailyState> states, DateOnly today, int days)
    {
        var byDay = states
            .GroupBy(state => state.Day)
            .ToDictionary(group => group.Key, group => (Up: group.Sum(s => s.Healthy + s.Warning), All: group.Sum(s => s.StateSamples)));
        var bars = new List<StatusDayBar>(days);
        for (var offset = days - 1; offset >= 0; offset--)
        {
            var day = today.AddDays(-offset);
            double? percent = byDay.TryGetValue(day, out var counts) && counts.All > 0
                ? (double)counts.Up / counts.All * 100
                : null;
            bars.Add(new StatusDayBar(day, percent, DayTone(percent)));
        }

        return bars;
    }

    /// <summary>Availability over the days that HAVE data (null when none has).</summary>
    public static double? Uptime(IEnumerable<SensorDailyState> states)
    {
        var up = 0L;
        var all = 0L;
        foreach (var state in states)
        {
            up += state.Healthy + state.Warning;
            all += state.StateSamples;
        }

        return all > 0 ? (double)up / all * 100 : null;
    }

    public static string DayTone(double? percent) => percent switch
    {
        null => "none",
        >= 99.95 => "ok",
        >= 95 => "warning",
        _ => "error"
    };

    /// <summary>"99.98 %" with no trailing zeros; "-" for no data.</summary>
    public static string FormatPercent(double? percent) =>
        percent is { } value
            ? (Math.Floor(value * 100) / 100).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " %"
            : "-";

    /// <summary>The checks a page must pass before it is stored; null = fine.</summary>
    public static string? Validate(StatusPage page)
    {
        if (string.IsNullOrWhiteSpace(page.Name))
        {
            return "Give the status page a name.";
        }

        if (page.Name.Length > 120)
        {
            return "The name is longer than 120 characters.";
        }

        if (!UptimeDayChoices.Contains(page.UptimeDays))
        {
            return "Show 30, 60 or 90 days.";
        }

        if (page.Components.Count > MaxComponents)
        {
            return $"A status page holds at most {MaxComponents} components.";
        }

        if (page.Components.Any(component => string.IsNullOrWhiteSpace(component.Name)))
        {
            return "Every component needs a name.";
        }

        var duplicate = page.Components
            .GroupBy(component => component.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        return duplicate is null ? null : $"Two components are called '{duplicate.Key}'.";
    }
}
