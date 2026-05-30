namespace Matmon.Core.Domain;

public sealed record SensorParameterOption
{
    public string Value { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;
}
