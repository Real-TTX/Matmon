using System.Net;
using Matmon.Core.Syslog;
using Matmon.Host.Services.Plugins;
using Matmon.Probe.Syslog;

namespace Matmon.Host.Services;

/// <summary>The storage form as it is posted (how long and how much the log keeps). Kept apart from <see cref="SyslogSettings"/>
/// so a half-typed form can be refused with a reason instead of being clamped into something the admin did not ask for.</summary>
public sealed class SyslogStorageInput
{
    public int RetentionDays { get; set; } = SyslogSettings.DefaultRetentionDays;

    public int MaxMessages { get; set; } = SyslogSettings.DefaultMaxMessages;

    public static SyslogStorageInput From(SyslogSettings settings) => new()
    {
        RetentionDays = settings.RetentionDays,
        MaxMessages = settings.MaxMessages
    };
}

/// <summary>One line of the Senders tab: a device as the log knows it.</summary>
public sealed record SyslogSender(
    string SourceAddress,
    string? Hostname,
    bool SharedAddress,
    long Messages,
    long Errors,
    DateTimeOffset LastReceivedUtc)
{
    /// <summary>What to filter the log by to see exactly this device: its host name when it shares its address with others,
    /// otherwise the address.</summary>
    public string Filter => SharedAddress && Hostname is not null ? Hostname : SourceAddress;
}

/// <summary>The host a sender was matched to, and the NAME that linked them - null when its address did.</summary>
public sealed record SyslogHostMatch(HostElement Host, string? MatchedName);

/// <summary>The receiver in one line: the state pill's key (ok / warning / error / paused), its label and a sentence.</summary>
public sealed record SyslogReceiverHead(string State, string Label, string Text);

/// <summary>
/// What the syslog UI (the Events tab and System > Syslog) decides without a request: the filter bar becomes a query, a posted form becomes settings (or a
/// reason why not), and a sender is matched to the host it belongs to in the tree. Pure, so it is tested without a server.
/// </summary>
internal static class SyslogPageSupport
{
    public const int PageSize = 200;
    public const int MaxTextLength = 500;
    private const int MaxWindowMinutes = 525_600; // a year - more than any retention

    /// <summary>The time filter: minutes back from now, 0 = everything that is still kept.</summary>
    public static IReadOnlyList<(int Minutes, string Label)> Windows { get; } =
    [
        (15, "Last 15 minutes"),
        (60, "Last hour"),
        (360, "Last 6 hours"),
        (1440, "Last 24 hours"),
        (10080, "Last 7 days"),
        (0, "Everything kept")
    ];

    /// <summary>The severity filter. "At least this severe" - a LOWER number is MORE severe, so the value is the highest
    /// number still let through.</summary>
    public static IReadOnlyList<(string Value, string Label)> SeverityChoices { get; } =
    [
        (string.Empty, "Any severity"),
        ("6", "Info and worse"),
        ("5", "Notice and worse"),
        ("4", "Warning and worse"),
        ("3", "Error and worse"),
        ("2", "Critical and worse")
    ];

    /// <summary>How long a sender stays listed on the Sources tab.</summary>
    public static IReadOnlyList<(int Days, string Label)> SourceWindows { get; } =
    [
        (1, "Last 24 hours"),
        (7, "Last 7 days"),
        (30, "Last 30 days"),
        (0, "Everything kept")
    ];

    /// <summary>Turns the filter bar into a query. False with a sentence when something in it cannot be used (an invalid
    /// regular expression, an unknown severity) - the page shows it instead of an empty list.</summary>
    public static bool TryBuildQuery(
        int windowMinutes,
        string? source,
        string? severity,
        string? text,
        bool regex,
        long? beforeId,
        DateTimeOffset now,
        out SyslogQuery query,
        out string? error)
    {
        query = new SyslogQuery();
        error = null;

        int? maxSeverity = null;
        if (!string.IsNullOrWhiteSpace(severity))
        {
            if (!SyslogSeverity.TryParse(severity, out var parsed))
            {
                error = $"\"{severity.Trim()}\" is not a severity.";
                return false;
            }

            maxSeverity = parsed;
        }

        var needle = text?.Trim();
        if (needle is { Length: > MaxTextLength })
        {
            error = $"The search text is longer than {MaxTextLength} characters.";
            return false;
        }

        string? contains = null;
        string? regexText = null;
        if (!string.IsNullOrEmpty(needle))
        {
            if (regex)
            {
                if (!SyslogRegex.TryCreate(needle, out _, out var regexError))
                {
                    error = $"The regular expression is not valid: {regexError}";
                    return false;
                }

                regexText = needle;
            }
            else
            {
                contains = needle;
            }
        }

        query = new SyslogQuery
        {
            FromUtc = windowMinutes > 0 ? now - TimeSpan.FromMinutes(Math.Min(windowMinutes, MaxWindowMinutes)) : null,
            Sources = SyslogSensorSupport.SplitList(source),
            MaxSeverity = maxSeverity,
            Contains = contains,
            Regex = regexText,
            BeforeId = beforeId is > 0 ? beforeId : null
        };
        return true;
    }

    /// <summary>
    /// The posted storage form on top of the current settings, or the reason it is refused - it refuses rather than clamps,
    /// so a typo does not quietly become another retention than the one the admin meant.
    /// </summary>
    public static bool TryBuildStorage(SyslogStorageInput input, SyslogSettings current, out SyslogSettings settings, out string? error)
    {
        settings = current.Clone();
        error = null;

        if (input.RetentionDays is < SyslogSettings.MinRetentionDays or > SyslogSettings.MaxRetentionDays)
        {
            error = $"Keep messages for {SyslogSettings.MinRetentionDays} to {SyslogSettings.MaxRetentionDays} days.";
            return false;
        }

        if (input.MaxMessages is < SyslogSettings.MinMessages or > SyslogSettings.MaxMessagesLimit)
        {
            error = $"The message limit must be between {SyslogSettings.MinMessages:N0} and {SyslogSettings.MaxMessagesLimit:N0}.";
            return false;
        }

        settings.RetentionDays = input.RetentionDays;
        settings.MaxMessages = input.MaxMessages;
        settings = settings.Normalized();
        return true;
    }

    /// <summary>CSS class (the existing log badge colours) for a severity: emergency to critical and error read red, warning
    /// amber, notice and info blue, debug grey.</summary>
    public static string SeverityClass(int severity) => severity switch
    {
        <= 2 => "critical",
        3 => "error",
        4 => "warning",
        <= 6 => "information",
        _ => "debug"
    };

    /// <summary>
    /// The Senders tab's lines. One per ADDRESS - unless the messages from an address carry two or more different host
    /// names, which means it is a relay (or Docker's gateway): then one per host name, because behind a shared address the
    /// name is the only thing that tells the devices apart. What the address sent without any name stays as a line of its
    /// own, so no message goes uncounted. Newest first.
    /// </summary>
    public static IReadOnlyList<SyslogSender> BuildSenders(IReadOnlyList<SyslogSenderSummary> summaries)
    {
        var result = new List<SyslogSender>();
        foreach (var group in summaries.GroupBy(summary => summary.SourceAddress, StringComparer.OrdinalIgnoreCase))
        {
            var names = group
                .Where(summary => summary.Hostname is not null)
                .Select(summary => summary.Hostname!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (names.Length <= 1)
            {
                result.Add(new SyslogSender(
                    group.First().SourceAddress,
                    names.FirstOrDefault(),
                    SharedAddress: false,
                    group.Sum(summary => summary.Messages),
                    group.Sum(summary => summary.Errors),
                    group.Max(summary => summary.LastReceivedUtc)));
                continue;
            }

            result.AddRange(group.Select(summary => new SyslogSender(
                summary.SourceAddress, summary.Hostname, SharedAddress: true, summary.Messages, summary.Errors, summary.LastReceivedUtc)));
        }

        return result.OrderByDescending(sender => sender.LastReceivedUtc).ToArray();
    }

    /// <summary>
    /// The host in the tree a sender belongs to, best match first: the host whose address IS the sender's address, then one
    /// whose address or name is the host name the sender puts in its messages, then one whose address is a fully qualified
    /// name whose first label is that host name (<c>sw-core.fritz.box</c> for a device that says <c>sw-core</c>). An address
    /// that is itself an IP is never matched by its first octet. Behind a SHARED address the address says nothing about the
    /// device, so only its host name is looked at. Null when nothing fits - a device nobody has added yet. The match also
    /// says which NAME made it (null when the address did): that is what a sensor for the host has to look for.
    /// </summary>
    public static SyslogHostMatch? FindHost(SyslogSender sender, IReadOnlyCollection<HostElement> hosts)
    {
        var equal = StringComparison.OrdinalIgnoreCase;

        if (!sender.SharedAddress || sender.Hostname is null)
        {
            var byAddress = hosts.FirstOrDefault(host => !string.IsNullOrWhiteSpace(host.Address) && string.Equals(host.Address, sender.SourceAddress, equal));
            if (byAddress is not null)
            {
                return new SyslogHostMatch(byAddress, null);
            }
        }

        if (sender.Hostname is not { } name)
        {
            return null;
        }

        var byName = hosts.FirstOrDefault(host =>
            (!string.IsNullOrWhiteSpace(host.Address) && string.Equals(host.Address, name, equal)) ||
            string.Equals(host.Name, name, equal));
        if (byName is not null)
        {
            return new SyslogHostMatch(byName, name);
        }

        var byLabel = hosts.FirstOrDefault(host => FirstLabelOfName(host.Address) is { } label && string.Equals(label, name, equal));
        return byLabel is null ? null : new SyslogHostMatch(byLabel, name);
    }

    private static string? FirstLabelOfName(string? address)
    {
        if (string.IsNullOrWhiteSpace(address) || IPAddress.TryParse(address, out _))
        {
            return null;
        }

        var dot = address.IndexOf('.');
        return dot > 0 ? address[..dot] : null;
    }

    /// <summary>
    /// The route values that open the Syslog tab of Events on the messages a syslog sensor is looking at - the same sender(s), time
    /// window, phrase and minimum severity - so an alert's "Latest error: ..." is one click from the log around it. Null for
    /// any other sensor type. (Route values rather than a URL: the page builds the link, so a path prefix - the cloud
    /// console's - is honoured.)
    /// </summary>
    public static Dictionary<string, string>? BuildLogRoute(string? sensorTypeKey, string? target, MonitoringSettings settings)
    {
        if (!SyslogSensorTypes.IsSyslog(sensorTypeKey))
        {
            return null;
        }

        var context = new SensorExecutionContext(sensorTypeKey!, target ?? string.Empty, settings);
        var route = new Dictionary<string, string>();

        var sources = SyslogSensorSupport.ResolveSources(context);
        if (sources.Count > 0)
        {
            route["source"] = string.Join(", ", sources);
        }

        route["window"] = ((int)SyslogSensorSupport.ResolveWindow(context).TotalMinutes).ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (string.Equals(sensorTypeKey!.Trim(), SyslogSensorTypes.Match, StringComparison.OrdinalIgnoreCase))
        {
            if (MonitoringSettings.TryReadParameter(settings, "syslog.pattern", out var pattern))
            {
                route["q"] = pattern.Trim();
                if (MonitoringSettings.TryReadParameter(settings, "syslog.mode", out var mode) &&
                    string.Equals(mode, "regex", StringComparison.OrdinalIgnoreCase))
                {
                    route["regex"] = "true";
                }
            }

            if (MonitoringSettings.TryReadParameter(settings, "syslog.minSeverity", out var minimum) &&
                !string.Equals(minimum, "any", StringComparison.OrdinalIgnoreCase) &&
                SyslogSeverity.TryParse(minimum, out var severity))
            {
                route["severity"] = severity.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        return route;
    }

    /// <summary>The names senders are known by - every address and host name once, newest sender first - for the sender
    /// filter's suggestions.</summary>
    public static IReadOnlyList<string> KnownSenderNames(IReadOnlyList<SyslogSender> senders) =>
        senders
            .SelectMany(sender => new[] { sender.SourceAddress, sender.Hostname })
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(200)
            .ToArray();

    /// <summary>
    /// Whether the Events page offers its Syslog tab: to someone who may read the log, while a collector runs (syslog or
    /// SNMP traps - both write the central log) or the log holds something - what was collected stays readable after the
    /// collectors are switched off. Until then the page is exactly what it was.
    /// </summary>
    public static bool ShowSyslogTab(bool canRead, bool collectorActive, bool logHasMessages) =>
        canRead && (collectorActive || logHasMessages);

    /// <summary>One message as the page's script receives it - everything it shows, nothing it should not trust: the text
    /// travels as a JSON string and goes onto the page as text.</summary>
    public static object ToRow(SyslogMessage message) => new
    {
        id = message.Id,
        ts = message.ReceivedUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
        time = message.ReceivedUtc.ToDisplay().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
        sev = message.Severity,
        sevName = SyslogSeverity.ShortName(message.Severity),
        sevClass = SeverityClass(message.Severity),
        source = message.SourceAddress,
        host = message.Hostname,
        app = message.AppName,
        procId = message.ProcId,
        msgId = message.MsgId,
        facility = SyslogFacility.Name(message.Facility),
        deviceTime = message.DeviceTime,
        text = message.Message
    };

    /// <summary>The collectors in one line - state pill, label, sentence - for the Syslog tab and the System pages alike, so
    /// they cannot tell two stories. Worst first: an unusable log, then nothing running, then a collector that could not start.</summary>
    public static SyslogReceiverHead DescribeCollectors(IReadOnlyList<PluginPlacementView> collectors, bool storageAvailable = true, string? storageError = null)
    {
        if (!storageAvailable)
        {
            return new SyslogReceiverHead("error", "Log unusable", "The log database cannot be used: " + storageError);
        }

        var enabled = collectors.Where(collector => collector.Assignment.Enabled).ToList();
        if (enabled.Count == 0)
        {
            return new SyslogReceiverHead("paused", "No collector", "No collector runs - nothing new is collected. Messages that are already stored can still be read.");
        }

        if (enabled.FirstOrDefault(collector => collector.State?.State == "error") is { } failed)
        {
            return new SyslogReceiverHead("error", "Problem", $"{failed.Location?.Name ?? "A collector"}: {failed.Detail}");
        }

        var running = enabled.Where(collector => collector.IsRunning).ToList();
        return running.Count > 0
            ? new SyslogReceiverHead("ok", "Receiving", "Collecting on " + string.Join(", ", running.Select(collector => collector.Location?.Name ?? "a deleted probe").Distinct()) + ".")
            : new SyslogReceiverHead("warning", "Starting", "The collector has not reported yet - this page shows its state again in a few seconds.");
    }

    /// <summary>"3 min ago" - how long since a sender last spoke.</summary>
    public static string Ago(DateTimeOffset when, DateTimeOffset now)
    {
        var age = now - when;
        return age < TimeSpan.Zero ? "moments ago" : SyslogSensorSupport.FormatAge(age) + " ago";
    }
}
