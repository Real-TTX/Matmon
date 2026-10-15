namespace Matmon.Core.Syslog;

/// <summary>Which wire format a message arrived in. <see cref="Raw"/> is anything that is neither - kept, not dropped.</summary>
public enum SyslogFormat
{
    Raw = 0,
    Rfc3164 = 1,
    Rfc5424 = 2
}

/// <summary>
/// Severity as RFC 5424 numbers it: 0 = emergency ... 7 = debug. A LOWER number is MORE severe, which is why
/// every filter in Matmon says "at most this number" (<see cref="SyslogQuery.MaxSeverity"/>), not "at least".
/// </summary>
public static class SyslogSeverity
{
    public const int Emergency = 0;
    public const int Alert = 1;
    public const int Critical = 2;
    public const int Error = 3;
    public const int Warning = 4;
    public const int Notice = 5;
    public const int Informational = 6;
    public const int Debug = 7;

    /// <summary>Emergency, alert, critical and error: what a person wants to be told about.</summary>
    public const int ErrorOrWorse = Error;

    private static readonly string[] Names = ["emergency", "alert", "critical", "error", "warning", "notice", "informational", "debug"];
    private static readonly string[] ShortNames = ["emerg", "alert", "crit", "err", "warning", "notice", "info", "debug"];

    public static string Name(int severity) => severity is >= 0 and <= 7 ? Names[severity] : "unknown";

    public static string ShortName(int severity) => severity is >= 0 and <= 7 ? ShortNames[severity] : "?";

    /// <summary>Accepts a number (0-7), a name ("error") or the short form devices print ("err", "crit", "info").</summary>
    public static bool TryParse(string? text, out int severity)
    {
        severity = 0;
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        if (int.TryParse(value, out var number))
        {
            severity = number;
            return number is >= 0 and <= 7;
        }

        for (var index = 0; index < Names.Length; index++)
        {
            if (value.Equals(Names[index], StringComparison.OrdinalIgnoreCase) ||
                value.Equals(ShortNames[index], StringComparison.OrdinalIgnoreCase))
            {
                severity = index;
                return true;
            }
        }

        // Spellings that are common in device configuration but not in the RFC.
        switch (value.ToLowerInvariant())
        {
            case "emergencies": severity = Emergency; return true;
            case "alerts": severity = Alert; return true;
            case "critical": severity = Critical; return true;
            case "errors": severity = Error; return true;
            case "warn" or "warnings": severity = Warning; return true;
            case "notification" or "notifications": severity = Notice; return true;
            case "information" or "informational": severity = Informational; return true;
            case "debugging": severity = Debug; return true;
            default: return false;
        }
    }
}

public static class SyslogFacility
{
    private static readonly string[] Names =
    [
        "kern", "user", "mail", "daemon", "auth", "syslog", "lpr", "news",
        "uucp", "cron", "authpriv", "ftp", "ntp", "security", "console", "clock",
        "local0", "local1", "local2", "local3", "local4", "local5", "local6", "local7"
    ];

    public static string Name(int facility) => facility is >= 0 and <= 23 ? Names[facility] : "unknown";
}

/// <summary>
/// One received syslog message. The TIME that matters is <see cref="ReceivedUtc"/>: a switch that has just
/// rebooted often has a wrong clock until NTP catches up (that is half the reason to collect logs centrally), so
/// the device's own timestamp is kept only as the text it sent (<see cref="DeviceTime"/>) and never trusted for
/// ordering, windows or retention.
/// </summary>
public sealed record SyslogMessage
{
    /// <summary>Assigned by the store; increases with arrival order, so it doubles as the paging cursor.</summary>
    public long Id { get; init; }

    public DateTimeOffset ReceivedUtc { get; init; }

    /// <summary>The sender's IP address (no port) - what the network says, as opposed to <see cref="Hostname"/>.</summary>
    public string SourceAddress { get; init; } = string.Empty;

    public int Facility { get; init; } = 1;

    public int Severity { get; init; } = SyslogSeverity.Notice;

    /// <summary>The device's timestamp exactly as it sent it (null when it sent none).</summary>
    public string? DeviceTime { get; init; }

    /// <summary>The HOSTNAME field of the message; many devices send none.</summary>
    public string? Hostname { get; init; }

    /// <summary>The program / tag (<c>sshd</c>, <c>kernel</c>, ...).</summary>
    public string? AppName { get; init; }

    public string? ProcId { get; init; }

    /// <summary>RFC 5424 MSGID, or the Cisco-style mnemonic (<c>LINK-3-UPDOWN</c>) when the message has one.</summary>
    public string? MsgId { get; init; }

    public string Message { get; init; } = string.Empty;

    public SyslogFormat Format { get; init; }
}

/// <summary>What to look for. Every part is optional and they combine with AND.</summary>
public sealed record SyslogQuery
{
    public DateTimeOffset? FromUtc { get; init; }

    public DateTimeOffset? ToUtc { get; init; }

    /// <summary>Sender IPs or host names (case-insensitive, exact). A message matches when ANY of them equals its
    /// source address OR its HOSTNAME field.</summary>
    public IReadOnlyList<string> Sources { get; init; } = [];

    /// <summary>Only messages at least this severe: 0..7, and a LOWER number is MORE severe.</summary>
    public int? MaxSeverity { get; init; }

    public int? Facility { get; init; }

    /// <summary>Case-insensitive substring of the message.</summary>
    public string? Contains { get; init; }

    /// <summary>A regular expression the message must match (case-insensitive). Wins over <see cref="Contains"/>.</summary>
    public string? Regex { get; init; }

    /// <summary>Messages containing any of these (case-insensitive) are left out - the noisy ones.</summary>
    public IReadOnlyList<string> Exclude { get; init; } = [];

    /// <summary>Paging cursor: only messages older than this id (newest-first results).</summary>
    public long? BeforeId { get; init; }
}

/// <summary>Counts per severity for a window - what the "Syslog Messages" sensor turns into channels.</summary>
public sealed record SyslogCounts(long Total, IReadOnlyList<long> BySeverity)
{
    public static SyslogCounts Empty { get; } = new(0, new long[8]);

    /// <summary>Emergency + alert + critical + error.</summary>
    public long Errors => Sum(0, SyslogSeverity.ErrorOrWorse);

    public long Warnings => BySeverity.Count > SyslogSeverity.Warning ? BySeverity[SyslogSeverity.Warning] : 0;

    private long Sum(int from, int to)
    {
        long total = 0;
        for (var index = from; index <= to && index < BySeverity.Count; index++)
        {
            total += BySeverity[index];
        }

        return total;
    }
}

/// <summary>
/// What one (address, host name) pair has sent: the building block of the Senders tab. The pair, not the address alone,
/// because the address is only where the packet came FROM - behind a syslog relay, or Docker's gateway, many devices
/// share one, and then the host name in their messages is the only thing that tells them apart. A message with no host
/// name has a null <see cref="Hostname"/>.
/// </summary>
public sealed record SyslogSenderSummary(
    string SourceAddress,
    string? Hostname,
    long Messages,
    long Errors,
    DateTimeOffset LastReceivedUtc);
