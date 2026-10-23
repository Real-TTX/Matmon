using System.Diagnostics;
using System.Net;
using Matmon.Core.Domain;
using Matmon.Core.Syslog;
using Matmon.Probe.Syslog;

namespace Matmon.Host.Services;

/// <summary>What both syslog sensors share: who they ask about, how far back, and how a window or an age reads.</summary>
internal static class SyslogSensorSupport
{
    public const string SourceKey = "syslog.source";
    public const string WindowKey = "syslog.window";
    public const string ExcludeKey = "syslog.exclude";
    public const int DefaultWindowMinutes = 60;
    public const int MaxWindowMinutes = 10_080;

    /// <summary>How long a host name may take to resolve. The sensor runs every minute; a slow resolver must not hold it up.</summary>
    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(2);

    public static SensorParameterDefinition SourceParameter { get; } = new()
    {
        Key = SourceKey,
        Label = "Sender",
        Kind = SensorParameterKind.Text,
        Description = "IP address or host name of the device, as it sends it - several can be separated by commas. Empty: the address of the host this sensor sits under.",
        Placeholder = "e.g. 192.168.1.2 or sw-core"
    };

    public static SensorParameterDefinition WindowParameter { get; } = new()
    {
        Key = WindowKey,
        Label = "Window (minutes)",
        Kind = SensorParameterKind.Integer,
        Description = "How far back to look. A message counts for this long, so a one-off error keeps the sensor in warning for this long.",
        DefaultValue = DefaultWindowMinutes.ToString(),
        Min = 1,
        Max = MaxWindowMinutes,
        Step = "1"
    };

    public static SensorParameterDefinition ExcludeParameter { get; } = new()
    {
        Key = ExcludeKey,
        Label = "Ignore messages containing",
        Kind = SensorParameterKind.Multiline,
        Description = "One phrase per line. A message containing any of them is left out - for noise such as spanning-tree notices."
    };

    /// <summary>The senders to ask about: the "Sender" parameter, else the target (the host's address).</summary>
    public static IReadOnlyList<string> ResolveSources(SensorExecutionContext context)
    {
        var text = MonitoringSettings.TryReadParameter(context.Settings, SourceKey, out var configured)
            ? configured
            : context.Target;
        return SplitList(text);
    }

    /// <summary>
    /// <see cref="ResolveSources"/> plus, for every entry that is a NAME rather than an address, the addresses it resolves to.
    /// A host in the tree is often entered by name (<c>sw-core.fritz.box</c>) while the log says where it came FROM - the
    /// device's IP - and may not carry a host name at all, so matching the name alone would find nothing. The name stays
    /// in the list too: a device that does put its host name in the message is matched by that as well. Best effort: a name
    /// that does not resolve (or resolves too slowly) simply adds nothing.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ResolveSourcesAsync(SensorExecutionContext context, CancellationToken cancellationToken)
    {
        var names = ResolveSources(context);
        var sources = new List<string>(names);
        foreach (var name in names)
        {
            if (IPAddress.TryParse(name, out _))
            {
                continue;
            }

            foreach (var address in await TryResolveAsync(name, cancellationToken).ConfigureAwait(false))
            {
                var text = SyslogIngest.FormatAddress(address);
                if (!sources.Contains(text, StringComparer.OrdinalIgnoreCase))
                {
                    sources.Add(text);
                }
            }
        }

        return sources;
    }

    private static async Task<IPAddress[]> TryResolveAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ResolveTimeout);
            return await Dns.GetHostAddressesAsync(name, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return [];
        }
    }

    public static TimeSpan ResolveWindow(SensorExecutionContext context)
    {
        var minutes = MonitoringSettings.TryReadParameterInt(context.Settings, WindowKey, out var configured) ? configured : DefaultWindowMinutes;
        return TimeSpan.FromMinutes(Math.Clamp(minutes, 1, MaxWindowMinutes));
    }

    public static IReadOnlyList<string> ResolveExclude(SensorExecutionContext context) =>
        MonitoringSettings.TryReadParameter(context.Settings, ExcludeKey, out var text)
            ? text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(20).ToArray()
            : [];

    /// <summary>A list typed into one field: commas, semicolons, spaces or new lines between the entries.</summary>
    public static IReadOnlyList<string> SplitList(string? text) =>
        (text ?? string.Empty)
            .Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static string FormatWindow(TimeSpan window) =>
        window.TotalMinutes < 120 ? $"{(int)window.TotalMinutes} min"
        : window.TotalHours < 48 ? $"{window.TotalHours:0.#} h"
        : $"{window.TotalDays:0.#} days";

    public static string FormatAge(TimeSpan age) =>
        age.TotalSeconds < 90 ? "moments"
        : age.TotalMinutes < 120 ? $"{(int)age.TotalMinutes} min"
        : age.TotalHours < 48 ? $"{age.TotalHours:0.#} h"
        : $"{age.TotalDays:0.#} days";

    public static string Shorten(string text, int max)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= max ? line : line[..max].TrimEnd() + "…";
    }

    /// <summary>The state to report when no collector runs (syslog or SNMP traps - both write the central log): not "healthy
    /// with zero errors" - nothing is being collected, so nothing can be said.</summary>
    public static SensorExecutionResult? ReceiverOff(IMonitoringWorkspaceStore workspace) =>
        workspace.GetPluginAssignments().Any(assignment => assignment.Enabled &&
            (string.Equals(assignment.PluginId, Matmon.Probe.Plugins.SyslogCollectorPlugin.Id, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(assignment.PluginId, Matmon.Probe.Plugins.SnmpTrapCollectorPlugin.Id, StringComparison.OrdinalIgnoreCase)))
            ? null
            : SensorExecutionResult.Unknown("No syslog collector runs - switch one on under System > Plugins (on the main probe or a probe near the device), otherwise no messages are collected.");

    public static SensorExecutionResult NoSender() =>
        SensorExecutionResult.Critical(TimeSpan.Zero, "No sender to look for: fill in Sender, or put the sensor under a host that has an address.");
}

/// <summary>
/// "Syslog Messages": what a device has logged lately, counted by severity, and how long ago it last spoke. Switches lose
/// their own log when they reboot; this is the log that survives, turned into numbers a threshold can watch - three errors
/// in the last hour, no message for six hours. The state is whatever the channel thresholds say (by default one error is a
/// warning, ten a critical); with no message on record at all the state is unknown, not green - a device that was never
/// told to send syslog here must not look healthy.
/// </summary>
public sealed class SyslogMessagesSensorExecutor : ISensorExecutor
{
    private readonly ISyslogStore _store;
    private readonly IMonitoringWorkspaceStore _workspace;

    public SyslogMessagesSensorExecutor(ISyslogStore store, IMonitoringWorkspaceStore workspace)
    {
        _store = store;
        _workspace = workspace;
    }

    public static SensorDefinition Definition { get; } = new()
    {
        Key = SyslogSensorTypes.Messages,
        DisplayName = "Syslog Messages",
        Description = "Counts the messages a device sent to Matmon's syslog server - errors, warnings, everything - in the last minutes, and notices when it goes quiet.",
        ChannelMode = SensorChannelMode.Fixed,
        Parameters =
        [
            SyslogSensorSupport.SourceParameter,
            SyslogSensorSupport.WindowParameter,
            SyslogSensorSupport.ExcludeParameter
        ]
    };

    public string SensorTypeKey => Definition.Key;

    public async ValueTask<SensorExecutionResult> ExecuteAsync(SensorExecutionContext context, CancellationToken cancellationToken = default)
    {
        if (SyslogSensorSupport.ReceiverOff(_workspace) is { } off)
        {
            return off;
        }

        var watch = Stopwatch.StartNew();
        var sources = await SyslogSensorSupport.ResolveSourcesAsync(context, cancellationToken).ConfigureAwait(false);
        if (sources.Count == 0)
        {
            return SyslogSensorSupport.NoSender();
        }

        var window = SyslogSensorSupport.ResolveWindow(context);
        var now = DateTimeOffset.UtcNow;
        var query = new SyslogQuery
        {
            Sources = sources,
            FromUtc = now - window,
            Exclude = SyslogSensorSupport.ResolveExclude(context)
        };

        var counts = _store.Count(query);
        var lastReceived = _store.LastReceived(sources);
        var senders = string.Join(", ", SyslogSensorSupport.ResolveSources(context));
        var windowText = SyslogSensorSupport.FormatWindow(window);

        if (lastReceived is null)
        {
            return SensorExecutionResult.Unknown(
                $"Nothing from {senders} on record - is the device set up to send syslog to this server?");
        }

        var channels = new List<SensorChannelValue>
        {
            new() { Key = "errors", Label = "Errors", Value = counts.Errors, MeasurementKind = SensorMeasurementKind.Count, IsDefault = true },
            new() { Key = "warnings", Label = "Warnings", Value = counts.Warnings, MeasurementKind = SensorMeasurementKind.Count },
            new() { Key = "messages", Label = "Messages", Value = counts.Total, MeasurementKind = SensorMeasurementKind.Count },
            new()
            {
                Key = "lastMessageAgeSeconds",
                Label = "Since last message",
                Value = Math.Max(0, (now - lastReceived.Value).TotalSeconds),
                Unit = "s",
                MeasurementKind = SensorMeasurementKind.Duration,
                LogByDefault = false
            }
        };

        string message;
        if (counts.Errors > 0 && _store.Latest(query with { MaxSeverity = SyslogSeverity.ErrorOrWorse }) is { } latestError)
        {
            message = $"{counts.Errors} error(s), {counts.Warnings} warning(s), {counts.Total} message(s) from {senders} in the last {windowText}. " +
                $"Latest error: {SyslogSensorSupport.Shorten(latestError.Message, 200)}";
        }
        else if (counts.Total == 0)
        {
            message = $"No messages from {senders} in the last {windowText}; the last one was {SyslogSensorSupport.FormatAge(now - lastReceived.Value)} ago";
        }
        else
        {
            message = $"{counts.Errors} error(s), {counts.Warnings} warning(s), {counts.Total} message(s) from {senders} in the last {windowText}";
        }

        watch.Stop();
        var result = SensorExecutionResult.Healthy(watch.Elapsed, message, counts.Errors, "errors", channels);
        return SensorThresholdEvaluator.ApplyChannelThresholds(context.Settings, result);
    }
}

/// <summary>
/// "Syslog Match": how many of a device's messages in the last minutes contain a phrase or match a regular expression -
/// "changed state to down", "authentication failure", "%SYS-5-RESTART". The state is the threshold's (by default one match
/// is a warning), and the message carries the latest hit, so the notification says what happened, not just that something did.
/// </summary>
public sealed class SyslogMatchSensorExecutor : ISensorExecutor
{
    private readonly ISyslogStore _store;
    private readonly IMonitoringWorkspaceStore _workspace;

    public SyslogMatchSensorExecutor(ISyslogStore store, IMonitoringWorkspaceStore workspace)
    {
        _store = store;
        _workspace = workspace;
    }

    public static SensorDefinition Definition { get; } = new()
    {
        Key = SyslogSensorTypes.Match,
        DisplayName = "Syslog Match",
        Description = "Counts a device's syslog messages that contain a phrase or match a regular expression - link down, failed logins, restarts - and shows the latest one.",
        ChannelMode = SensorChannelMode.Fixed,
        Parameters =
        [
            new SensorParameterDefinition
            {
                Key = "syslog.pattern",
                Label = "Looking for",
                Kind = SensorParameterKind.Text,
                Required = true,
                Description = "The phrase to find in the message (not case-sensitive), or a regular expression - see Match as.",
                Placeholder = "e.g. changed state to down"
            },
            new SensorParameterDefinition
            {
                Key = "syslog.mode",
                Label = "Match as",
                Kind = SensorParameterKind.ValueList,
                Description = "Text: the message contains the phrase. Regular expression: the message matches the pattern.",
                DefaultValue = "contains",
                Options =
                [
                    new SensorParameterOption { Value = "contains", Label = "Text (contains)" },
                    new SensorParameterOption { Value = "regex", Label = "Regular expression" }
                ]
            },
            new SensorParameterDefinition
            {
                Key = "syslog.minSeverity",
                Label = "Only messages at least this severe",
                Kind = SensorParameterKind.ValueList,
                Description = "Narrow the search to important messages. \"Any\" looks at everything the device sent.",
                DefaultValue = "any",
                Options =
                [
                    new SensorParameterOption { Value = "any", Label = "Any severity" },
                    new SensorParameterOption { Value = "info", Label = "Informational and worse" },
                    new SensorParameterOption { Value = "notice", Label = "Notice and worse" },
                    new SensorParameterOption { Value = "warning", Label = "Warning and worse" },
                    new SensorParameterOption { Value = "error", Label = "Error and worse" },
                    new SensorParameterOption { Value = "critical", Label = "Critical and worse" }
                ]
            },
            SyslogSensorSupport.SourceParameter,
            SyslogSensorSupport.WindowParameter
        ]
    };

    public string SensorTypeKey => Definition.Key;

    public async ValueTask<SensorExecutionResult> ExecuteAsync(SensorExecutionContext context, CancellationToken cancellationToken = default)
    {
        if (!MonitoringSettings.TryReadParameter(context.Settings, "syslog.pattern", out var pattern))
        {
            return SensorExecutionResult.Critical(TimeSpan.Zero, "No phrase to look for - fill in Looking for.");
        }

        pattern = pattern.Trim();
        var asRegex = MonitoringSettings.TryReadParameter(context.Settings, "syslog.mode", out var mode) &&
            string.Equals(mode, "regex", StringComparison.OrdinalIgnoreCase);
        if (asRegex && !SyslogRegex.TryCreate(pattern, out _, out var regexError))
        {
            return SensorExecutionResult.Critical(TimeSpan.Zero, $"The regular expression is not valid: {regexError}");
        }

        if (SyslogSensorSupport.ReceiverOff(_workspace) is { } off)
        {
            return off;
        }

        var watch = Stopwatch.StartNew();
        var sources = await SyslogSensorSupport.ResolveSourcesAsync(context, cancellationToken).ConfigureAwait(false);
        if (sources.Count == 0)
        {
            return SyslogSensorSupport.NoSender();
        }

        var window = SyslogSensorSupport.ResolveWindow(context);
        var now = DateTimeOffset.UtcNow;
        int? maxSeverity = MonitoringSettings.TryReadParameter(context.Settings, "syslog.minSeverity", out var minimum) &&
            !string.Equals(minimum, "any", StringComparison.OrdinalIgnoreCase) &&
            SyslogSeverity.TryParse(minimum, out var parsed)
                ? parsed
                : null;

        var query = new SyslogQuery
        {
            Sources = sources,
            FromUtc = now - window,
            MaxSeverity = maxSeverity,
            Regex = asRegex ? pattern : null,
            Contains = asRegex ? null : pattern
        };

        var matches = _store.Count(query).Total;
        var latest = matches > 0 ? _store.Latest(query) : null;
        var senders = string.Join(", ", SyslogSensorSupport.ResolveSources(context));
        var windowText = SyslogSensorSupport.FormatWindow(window);
        var shownPattern = SyslogSensorSupport.Shorten(pattern, 60);

        var channels = new List<SensorChannelValue>
        {
            new() { Key = "matches", Label = "Matches", Value = matches, MeasurementKind = SensorMeasurementKind.Count, IsDefault = true }
        };
        if (latest is not null)
        {
            channels.Add(new SensorChannelValue
            {
                Key = "lastMatchAgeSeconds",
                Label = "Since latest match",
                Value = Math.Max(0, (now - latest.ReceivedUtc).TotalSeconds),
                Unit = "s",
                MeasurementKind = SensorMeasurementKind.Duration,
                LogByDefault = false
            });
        }

        var message = latest is null
            ? $"No message from {senders} matching \"{shownPattern}\" in the last {windowText}"
            : $"{matches} message(s) from {senders} matching \"{shownPattern}\" in the last {windowText}. " +
              $"Latest ({SyslogSensorSupport.FormatAge(now - latest.ReceivedUtc)} ago): {SyslogSensorSupport.Shorten(latest.Message, 200)}";

        watch.Stop();
        var result = SensorExecutionResult.Healthy(watch.Elapsed, message, matches, "matches", channels);
        return SensorThresholdEvaluator.ApplyChannelThresholds(context.Settings, result);
    }
}
