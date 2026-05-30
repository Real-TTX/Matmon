using System.Text.Json.Serialization;

namespace Matmon.Core.Domain;

public enum SensorState
{
    Unknown = 0,
    Healthy = 1,
    Warning = 2,
    Critical = 3,
    Disabled = 4,
    Paused = 5
}

public sealed record SensorExecutionResult(
    SensorState State,
    TimeSpan Duration,
    double? Value = null,
    string? Message = null)
{
    public string? DefaultChannelKey { get; init; }

    public IReadOnlyList<SensorChannelValue> Channels { get; init; } = [];

    [JsonIgnore]
    public double? DefaultValue => Value;

    public static SensorExecutionResult Healthy(TimeSpan duration, string? message = null)
        => Create(SensorState.Healthy, duration, message);

    public static SensorExecutionResult Healthy(
        TimeSpan duration,
        string? message,
        double? value,
        string? defaultChannelKey = null,
        IReadOnlyList<SensorChannelValue>? channels = null)
        => Create(SensorState.Healthy, duration, message, value, defaultChannelKey, channels);

    public static SensorExecutionResult Warning(TimeSpan duration, string? message = null)
        => Create(SensorState.Warning, duration, message);

    public static SensorExecutionResult Warning(
        TimeSpan duration,
        string? message,
        double? value,
        string? defaultChannelKey = null,
        IReadOnlyList<SensorChannelValue>? channels = null)
        => Create(SensorState.Warning, duration, message, value, defaultChannelKey, channels);

    public static SensorExecutionResult Critical(TimeSpan duration, string? message = null)
        => Create(SensorState.Critical, duration, message);

    public static SensorExecutionResult Critical(
        TimeSpan duration,
        string? message,
        double? value,
        string? defaultChannelKey = null,
        IReadOnlyList<SensorChannelValue>? channels = null)
        => Create(SensorState.Critical, duration, message, value, defaultChannelKey, channels);

    public static SensorExecutionResult Disabled(string? message = null)
        => Create(SensorState.Disabled, TimeSpan.Zero, message);

    public static SensorExecutionResult Paused(string? message = null)
        => Create(SensorState.Paused, TimeSpan.Zero, message);

    public static SensorExecutionResult Unknown(string? message = null)
        => Create(SensorState.Unknown, TimeSpan.Zero, message);

    private static SensorExecutionResult Create(
        SensorState state,
        TimeSpan duration,
        string? message,
        double? value = null,
        string? defaultChannelKey = null,
        IReadOnlyList<SensorChannelValue>? channels = null)
    {
        return new SensorExecutionResult(state, duration, value, message)
        {
            DefaultChannelKey = defaultChannelKey,
            Channels = channels?.ToArray() ?? []
        };
    }
}
