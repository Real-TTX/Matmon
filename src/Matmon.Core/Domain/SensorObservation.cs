using System.Text.Json.Serialization;

namespace Matmon.Core.Domain;

public sealed class SensorObservation
{
    public Guid SensorId { get; set; }

    public DateTimeOffset TimestampUtc { get; set; }

    public SensorState State { get; set; } = SensorState.Unknown;

    public double? Value { get; set; }

    public string? DefaultChannelKey { get; set; }

    public List<SensorChannelValue> Channels { get; set; } = [];

    public string? ExecutedByProbeId { get; set; }

    public string? ExecutedByProbeName { get; set; }

    [JsonIgnore]
    public double? DefaultValue => Value;

    public TimeSpan Duration { get; set; }

    public string? Message { get; set; }
}
