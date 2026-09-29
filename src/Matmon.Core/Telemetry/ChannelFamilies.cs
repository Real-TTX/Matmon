using Matmon.Core.Domain;

namespace Matmon.Core.Telemetry;

/// <summary>
/// "The same measurement" across sensor types that name it differently - pure, so the matching rules are
/// tested rather than discovered on a wallboard.
///
/// The question a comparison chart asks is "which of these machines is busy?", and the machines are rarely
/// the same kind: a Windows health sensor reports <c>cpuLoad</c>, Proxmox and VMware <c>cpu</c>, Synology
/// <c>cpuUtilization</c>. A single channel KEY therefore compares nothing across a mixed set; a FAMILY picks
/// each sensor's own channel for the measurement. Stored as the token <c>family:&lt;key&gt;</c> in the same
/// field that otherwise holds an exact channel key, so a tile that names an exact key keeps working unchanged.
/// </summary>
public static class ChannelFamilies
{
    public const string TokenPrefix = "family:";

    /// <summary>
    /// Candidate channel names, best first, compared on the LAST key segment normalised to letters and
    /// digits ("vm.101.cpu" → "cpu", "cpuLoad" → "cpuload"). The unit guard keeps a family from mixing
    /// units: one line in bytes on a chart of percentages is worse than a missing line.
    /// </summary>
    public static IReadOnlyList<ChannelFamily> All { get; } =
    [
        new("cpu", "CPU usage", "%", SensorMeasurementKind.Percent,
            ["cpu", "cpuload", "cpuutilization", "cpuusage", "cpupercent", "cpuusedpercent", "processorload", "processor"]),
        new("memory", "Memory usage", "%", SensorMeasurementKind.Percent,
            ["memoryusedpercent", "memory", "memoryutilization", "memoryusage", "memusedpercent", "mem", "ram", "rampercent"]),
        new("disk", "Disk usage", "%", SensorMeasurementKind.Percent,
            ["diskusedpercent", "storageusedpercent", "diskusage", "volumeusedpercent", "rootfs", "disk", "storage"]),
        new("latency", "Latency", "ms", SensorMeasurementKind.Duration,
            ["latency", "responsetime", "roundtrip", "rtt", "avglatency", "pinglatency"]),
        new("temperature", "Temperature", "°C", SensorMeasurementKind.Temperature,
            ["temperature", "maxtemperature", "cputemperature", "temp"])
    ];

    public static string Token(ChannelFamily family) => TokenPrefix + family.Key;

    /// <summary>The family a stored channel field names, or null for an exact key (or nothing).</summary>
    public static ChannelFamily? Parse(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || !token.Trim().StartsWith(TokenPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var key = token.Trim()[TokenPrefix.Length..];
        return All.FirstOrDefault(family => string.Equals(family.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// This sensor's channel for the family, from one observation's channels - or null when it has none.
    /// Ranked: a TOP-LEVEL key before a per-object one (a Proxmox node's own <c>cpu</c>, not
    /// <c>vm.101.cpu</c> of one of its guests), then the order of the candidate list, then the shorter key.
    /// </summary>
    public static SensorChannelValue? Pick(IEnumerable<SensorChannelValue> channels, ChannelFamily family) =>
        channels
            .Where(channel => !channel.IsVirtual && channel.Value.HasValue && UnitFits(channel, family))
            .Select(channel => (channel, rank: Array.IndexOf(family.Names, Normalize(LastSegment(channel.Key)))))
            .Where(entry => entry.rank >= 0)
            .OrderBy(entry => entry.channel.Key.Contains('.') ? 1 : 0)
            .ThenBy(entry => entry.rank)
            .ThenBy(entry => entry.channel.Key.Length)
            .Select(entry => entry.channel)
            .FirstOrDefault();

    /// <summary>A channel that says nothing about its unit is given the benefit of the doubt (script
    /// sensors often report bare numbers); one that says something else is not.</summary>
    private static bool UnitFits(SensorChannelValue channel, ChannelFamily family)
    {
        if (channel.MeasurementKind != SensorMeasurementKind.Unknown)
        {
            return channel.MeasurementKind == family.MeasurementKind;
        }

        var unit = channel.Unit?.Trim();
        if (string.IsNullOrEmpty(unit))
        {
            return true;
        }

        return family.Key == "temperature"
            ? unit.Replace("°", string.Empty).Equals("C", StringComparison.OrdinalIgnoreCase)
            : unit.Equals(family.Unit, StringComparison.OrdinalIgnoreCase);
    }

    private static string LastSegment(string key) => key[(key.LastIndexOf('.') + 1)..];

    private static string Normalize(string name) =>
        new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}

public sealed record ChannelFamily(
    string Key,
    string Label,
    string Unit,
    SensorMeasurementKind MeasurementKind,
    string[] Names);
