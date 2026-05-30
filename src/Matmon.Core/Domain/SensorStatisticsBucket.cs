namespace Matmon.Core.Domain;

public sealed class SensorStatisticsBucket
{
    public Guid SensorId { get; set; }

    public DateTimeOffset BucketStartUtc { get; set; }

    public int BucketMinutes { get; set; }

    public string DefaultChannelKey { get; set; } = string.Empty;

    public SensorState State { get; set; } = SensorState.Unknown;

    public int SampleCount { get; set; }

    public double? Average { get; set; }

    public double? Minimum { get; set; }

    public double? Maximum { get; set; }

    public double? LastValue { get; set; }

    public string? Unit { get; set; }

    public string? Message { get; set; }
}
