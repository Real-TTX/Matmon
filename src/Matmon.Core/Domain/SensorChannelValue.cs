namespace Matmon.Core.Domain;

public sealed record SensorChannelValue
{
    public string Key { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;

    public double? Value { get; init; }

    public string? Unit { get; init; }

    public SensorState? State { get; init; }

    public string? Message { get; init; }

    public bool IsDefault { get; init; }
}
