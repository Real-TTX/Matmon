namespace Matmon.Core.Syslog;

/// <summary>The keys of the two syslog sensor types. They live in the core because the rule about where a sensor runs
/// (<see cref="Matmon.Core.Domain.SensorPlacement"/>) is in the core, while the executors that need the log store are
/// in the host.</summary>
public static class SyslogSensorTypes
{
    /// <summary>Counts what a device logged lately - errors, warnings, everything - and notices when it goes quiet.</summary>
    public const string Messages = "syslog-messages";

    /// <summary>Counts the messages of a device that match a phrase or a regular expression.</summary>
    public const string Match = "syslog-match";

    /// <summary>Whether a sensor type is one of the syslog sensors.</summary>
    public static bool IsSyslog(string? sensorTypeKey) =>
        string.Equals(sensorTypeKey?.Trim(), Messages, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(sensorTypeKey?.Trim(), Match, StringComparison.OrdinalIgnoreCase);
}
