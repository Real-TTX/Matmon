using Matmon.Core.Domain;

namespace Matmon.Core.Telemetry;

/// <summary>
/// Storage layer for the unbounded, append-heavy telemetry data (sensor
/// observations, monitoring events and statistics buckets). This is the seam
/// that lets that data live outside the in-memory JSON workspace document so it
/// scales to hundreds of megabytes. Orchestration (deciding when to record an
/// event/alert/statistic) stays in the workspace store; this contract only
/// owns persistence and querying.
/// </summary>
public interface ITelemetryRepository
{
    // --- Sensor observations -------------------------------------------------

    void AppendObservation(SensorObservation observation);

    /// <summary>Most recent observation per sensor, keyed by sensor id.</summary>
    IReadOnlyDictionary<Guid, SensorObservation> GetLatestObservations();

    /// <summary>Most recent observation for a single sensor, or null if it has none.</summary>
    SensorObservation? GetLatestObservation(Guid sensorId);

    /// <summary>
    /// Actual stored raw-observation count per sensor (the "log length"). A cheap GROUP BY over the
    /// sensor index — used to spot sensors with disproportionately long logs (e.g. over-fast polling).
    /// </summary>
    IReadOnlyDictionary<Guid, int> GetObservationCountsBySensor();

    /// <summary>
    /// Observations for one sensor at or after <paramref name="fromUtc"/>,
    /// ordered ascending, optionally limited to the last <paramref name="maxCount"/>.
    /// </summary>
    IReadOnlyList<SensorObservation> GetObservations(Guid sensorId, DateTimeOffset fromUtc, int? maxCount);

    /// <summary>
    /// For every sensor that has data, its observations at or after
    /// <paramref name="fromUtc"/> (ascending, last <paramref name="maxPerSensor"/>),
    /// guaranteeing the single most recent observation is always included.
    /// </summary>
    IReadOnlyDictionary<Guid, SensorObservation[]> GetRecentObservationsBySensor(DateTimeOffset fromUtc, int maxPerSensor);

    IReadOnlyList<SensorObservation> GetAllObservations();

    /// <summary>Removes observations for one sensor older than the cutoff. Returns rows removed.</summary>
    int PruneObservations(Guid sensorId, DateTimeOffset cutoffUtc);

    // --- Monitoring events ---------------------------------------------------

    void AppendEvent(MonitoringEvent monitoringEvent);

    /// <summary>The most recent <paramref name="take"/> events, newest first.</summary>
    IReadOnlyList<MonitoringEvent> GetEvents(int take);

    IReadOnlyList<MonitoringEvent> GetAllEvents();

    int PruneEvents(DateTimeOffset cutoffUtc);

    // --- Statistics buckets --------------------------------------------------

    SensorStatisticsBucket? GetStatisticsBucket(Guid sensorId, int bucketMinutes, DateTimeOffset bucketStartUtc);

    void UpsertStatisticsBucket(SensorStatisticsBucket bucket);

    IReadOnlyList<SensorStatisticsBucket> GetStatistics(Guid sensorId);

    IReadOnlyList<SensorStatisticsBucket> GetAllStatistics();

    int PruneStatistics(Guid sensorId, DateTimeOffset cutoffUtc);

    // --- Bulk maintenance, migration and backup ------------------------------

    TelemetryCounts GetCounts();

    /// <summary>Deletes observations older than the cutoff, or all when null. Returns rows removed.</summary>
    int DeleteObservations(DateTimeOffset? olderThanUtc);

    int DeleteEvents(DateTimeOffset? olderThanUtc);

    int DeleteStatistics(DateTimeOffset? olderThanUtc);

    /// <summary>Replaces the entire observation set (used by migration and backup restore).</summary>
    void ReplaceAllObservations(IEnumerable<SensorObservation> observations);

    void ReplaceAllEvents(IEnumerable<MonitoringEvent> events);

    void ReplaceAllStatistics(IEnumerable<SensorStatisticsBucket> buckets);
}

public readonly record struct TelemetryCounts(long Observations, long Events, long Statistics)
{
    public long Total => Observations + Events + Statistics;
}
