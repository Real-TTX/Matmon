namespace Matmon.Core.Domain;

/// <summary>
/// Ambient display timezone for rendering timestamps. Everything is stored UTC; UI converts for display only.
/// The effective zone per call is: the current request's user override (when the host wires
/// <see cref="PerRequestZoneProvider"/>) → the <see cref="SystemDefault"/> → UTC. Framework-free: the host supplies
/// the per-request resolver (reads the signed-in user), so this stays usable from Core + background code too.
/// Replace <c>DateTimeOffset.ToLocalTime()</c> with <see cref="ToDisplay(DateTimeOffset)"/>.
/// </summary>
public static class DisplayTimeZone
{
    /// <summary>System-wide default zone (admin-configured); UTC until set at startup.</summary>
    public static TimeZoneInfo SystemDefault { get; set; } = TimeZoneInfo.Utc;

    /// <summary>Host-supplied resolver for the current request's user zone (null when not in a user request).</summary>
    public static Func<TimeZoneInfo?>? PerRequestZoneProvider { get; set; }

    /// <summary>The zone to render in right now: per-request user override, else the system default.</summary>
    public static TimeZoneInfo Current => PerRequestZoneProvider?.Invoke() ?? SystemDefault;

    /// <summary>Resolve an IANA/Windows id to a <see cref="TimeZoneInfo"/>, or null if unknown/blank.</summary>
    public static TimeZoneInfo? Resolve(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id.Trim());
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }
    }
}

/// <summary>Timestamp display conversions into <see cref="DisplayTimeZone.Current"/> - drop-in for <c>ToLocalTime()</c>.</summary>
public static class DisplayTimeExtensions
{
    public static DateTimeOffset ToDisplay(this DateTimeOffset utc) =>
        TimeZoneInfo.ConvertTime(utc, DisplayTimeZone.Current);

    public static DateTimeOffset? ToDisplay(this DateTimeOffset? utc) =>
        utc is { } value ? value.ToDisplay() : null;

    public static DateTime ToDisplay(this DateTime utc) =>
        TimeZoneInfo.ConvertTime(utc.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(utc, DateTimeKind.Utc) : utc, DisplayTimeZone.Current);
}
