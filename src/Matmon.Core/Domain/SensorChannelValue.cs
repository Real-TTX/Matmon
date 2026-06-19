namespace Matmon.Core.Domain;

public sealed record SensorChannelValue
{
    public string Key { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;

    public double? Value { get; init; }

    public string? Unit { get; init; }

    public SensorMeasurementKind MeasurementKind { get; init; } = SensorMeasurementKind.Unknown;

    public SensorState? State { get; init; }

    public string? Message { get; init; }

    public bool IsDefault { get; init; }

    public bool IsVirtual { get; init; }

    /// <summary>
    /// Whether this channel is recorded into long-term statistics by default.
    /// Executors set this to <c>false</c> for channels that aren't worth trending
    /// (e.g. boolean up/valid flags that just mirror the sensor state). The user
    /// can override it per channel in the sensor's Channels &amp; thresholds editor.
    /// </summary>
    public bool LogByDefault { get; init; } = true;
}
