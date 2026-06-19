using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Matmon.Core.Domain;
using Matmon.Core.Sample;
using Matmon.Core.Telemetry;
using Matmon.Host.Ui;
using Microsoft.AspNetCore.DataProtection;

namespace Matmon.Host.Services;

public sealed partial class InMemoryMonitoringWorkspaceStore
{
    private readonly MonitoringInheritanceResolver _telemetryInheritanceResolver = new();

    public void RecordSensorObservation(
        Guid sensorId,
        SensorExecutionResult result,
        DateTimeOffset timestampUtc,
        MonitoringSettings? settings = null,
        string? executedByProbeId = null,
        string? executedByProbeName = null)
    {
        lock (_gate)
        {
            EnsureDefaultAlertCollection();

            var previousObservation = _telemetry.GetLatestObservation(sensorId);

            var observation = new SensorObservation
            {
                SensorId = sensorId,
                TimestampUtc = timestampUtc,
                State = result.State,
                Value = result.Value,
                DefaultChannelKey = result.DefaultChannelKey,
                Channels = result.Channels.Select(channel => channel with { }).ToList(),
                ExecutedByProbeId = string.IsNullOrWhiteSpace(executedByProbeId) ? null : executedByProbeId.Trim(),
                ExecutedByProbeName = string.IsNullOrWhiteSpace(executedByProbeName) ? null : executedByProbeName.Trim(),
                Duration = result.Duration,
                Message = result.Message
            };

            _telemetry.AppendObservation(observation);

            if (ShouldRecordStateChangeEvent(previousObservation, result))
            {
                AddEvent(new MonitoringEvent
                {
                    TimestampUtc = timestampUtc,
                    Kind = MonitoringEventKind.StateChanged,
                    ElementId = sensorId,
                    ElementKind = MonitoringElementKind.Sensor,
                    ElementName = GetElementName(sensorId),
                    ElementPath = GetElementPath(sensorId),
                    State = result.State,
                    Message = AppendExecutionProbe(
                        BuildStateChangeMessage(previousObservation?.State, result.State, result.Message),
                        executedByProbeName,
                        executedByProbeId)
                });
            }

            SyncSensorAlertFromObservation(sensorId, result, timestampUtc);
            // Statistics roll-up and telemetry retention run out-of-band in
            // RunTelemetryMaintenance (driven by StatisticsRollupService) so the
            // polling hot path only appends the raw observation and event.
            QueueSave(SavePriority.Telemetry);
        }
    }

    public IReadOnlyList<SensorObservation> GetSensorHistory()
    {
        return _telemetry.GetAllObservations();
    }

    public IReadOnlyList<SensorObservation> GetSensorHistory(Guid sensorId, TimeSpan? window = null, int? maxCount = null)
    {
        if (maxCount is <= 0)
        {
            return Array.Empty<SensorObservation>();
        }

        var cutoffUtc = window is { } requestedWindow && requestedWindow > TimeSpan.Zero
            ? DateTimeOffset.UtcNow - requestedWindow
            : DateTimeOffset.MinValue;
        return _telemetry.GetObservations(sensorId, cutoffUtc, maxCount);
    }

    public IReadOnlyDictionary<Guid, SensorObservation> GetLatestSensorObservations()
    {
        return _telemetry.GetLatestObservations();
    }

    public IReadOnlyDictionary<Guid, int> GetSensorObservationCounts()
    {
        return _telemetry.GetObservationCountsBySensor();
    }

    public IReadOnlyDictionary<Guid, SensorObservation[]> GetRecentSensorHistoryBySensor(TimeSpan window, int maxPerSensor)
    {
        var cutoffUtc = window > TimeSpan.Zero
            ? DateTimeOffset.UtcNow - window
            : DateTimeOffset.MinValue;
        return _telemetry.GetRecentObservationsBySensor(cutoffUtc, maxPerSensor);
    }

    public IReadOnlyList<MonitoringEvent> GetEvents(int take = 500)
    {
        return _telemetry.GetEvents(take);
    }

    public IReadOnlyList<SensorStatisticsBucket> GetSensorStatistics(Guid sensorId)
    {
        return _telemetry.GetStatistics(sensorId);
    }

    public StorageTelemetryOverview GetStorageTelemetryOverview()
    {
        var counts = _telemetry.GetCounts();
        return new StorageTelemetryOverview(counts.Observations, counts.Events, counts.Statistics);
    }

    public StorageCleanupResult CleanupStorage(StorageCleanupScope scope, int olderThanDays)
    {
        if (!Enum.IsDefined(scope))
        {
            throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown cleanup scope.");
        }

        if (olderThanDays < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(olderThanDays), olderThanDays, "Cleanup age must be zero or greater.");
        }

        DateTimeOffset? olderThanUtc = olderThanDays == 0
            ? null
            : DateTimeOffset.UtcNow - TimeSpan.FromDays(olderThanDays);

        var historyRemoved = ShouldCleanupHistory(scope) ? _telemetry.DeleteObservations(olderThanUtc) : 0;
        var eventsRemoved = ShouldCleanupEvents(scope) ? _telemetry.DeleteEvents(olderThanUtc) : 0;
        var statisticsRemoved = ShouldCleanupStatistics(scope) ? _telemetry.DeleteStatistics(olderThanUtc) : 0;

        return new StorageCleanupResult(historyRemoved, eventsRemoved, statisticsRemoved);
    }

    private void MigrateDocumentTelemetryIntoRepository()
    {
        _document.SensorHistory ??= [];
        _document.Events ??= [];
        _document.SensorStatistics ??= [];

        var hasDocumentTelemetry = _document.SensorHistory.Count > 0
            || _document.Events.Count > 0
            || _document.SensorStatistics.Count > 0;

        if (hasDocumentTelemetry && _telemetry.GetCounts().Total == 0)
        {
            _telemetry.ReplaceAllObservations(_document.SensorHistory);
            _telemetry.ReplaceAllEvents(_document.Events);
            _telemetry.ReplaceAllStatistics(_document.SensorStatistics);
            _logger.LogInformation(
                "Migrated telemetry from workspace into the telemetry database: {Observations} observations, {Events} events, {Statistics} statistics buckets",
                _document.SensorHistory.Count,
                _document.Events.Count,
                _document.SensorStatistics.Count);
        }

        // Telemetry now lives in the repository; never serialize it back into workspace.json.
        _document.SensorHistory = [];
        _document.Events = [];
        _document.SensorStatistics = [];
    }

    private void AddEvent(MonitoringEvent monitoringEvent)
    {
        _telemetry.AppendEvent(monitoringEvent);
    }

    /// <summary>
    /// Recomputes the recent statistics buckets for every sensor from raw
    /// observations and applies telemetry retention (raw/statistics/events).
    /// Runs out-of-band (see <c>StatisticsRollupService</c>) so percentiles are
    /// accurate and the polling hot path stays light. Retention/granularity is
    /// the sensor's explicit override, else its per-type
    /// <see cref="SensorTelemetryProfiles"/> default.
    /// </summary>
    public TelemetryMaintenanceResult RunTelemetryMaintenance(DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            var templates = _document.Templates.ToDictionary(template => template.Id);
            var sensors = EnumerateElements(_document.RootProbe).OfType<SensorElement>().ToList();

            var bucketsWritten = 0;
            var observationsPruned = 0;
            var statisticsPruned = 0;

            foreach (var sensor in sensors)
            {
                var settings = _telemetryInheritanceResolver.Resolve(BuildLineage(sensor), templates);
                var profile = SensorTelemetryProfiles.Resolve(sensor.SensorTypeKey);

                var bucketMinutes = ResolveRetentionDays(settings.StatisticsBucketMinutes, profile.StatisticsBucketMinutes);
                bucketsWritten += RecomputeRecentBuckets(sensor.Id, nowUtc, bucketMinutes);

                var rawDays = ResolveRetentionDays(settings.ObservationRetentionDays, profile.RawObservationDays);
                if (rawDays > 0)
                {
                    observationsPruned += _telemetry.PruneObservations(sensor.Id, nowUtc - TimeSpan.FromDays(rawDays));
                }

                var statsDays = ResolveRetentionDays(settings.StatisticsRetentionDays, profile.StatisticsRetentionDays);
                if (statsDays > 0)
                {
                    statisticsPruned += _telemetry.PruneStatistics(sensor.Id, nowUtc - TimeSpan.FromDays(statsDays));
                }
            }

            var eventDays = SensorTelemetryProfiles.General.EventRetentionDays;
            var eventsPruned = eventDays > 0
                ? _telemetry.PruneEvents(nowUtc - TimeSpan.FromDays(eventDays))
                : 0;

            return new TelemetryMaintenanceResult(sensors.Count, bucketsWritten, observationsPruned, statisticsPruned, eventsPruned);
        }
    }

    // Recomputes the current open bucket and the previous one (to absorb late,
    // out-of-order observations from secondary probes) from raw observations.
    private int RecomputeRecentBuckets(Guid sensorId, DateTimeOffset nowUtc, int bucketMinutes)
    {
        if (bucketMinutes <= 0)
        {
            return 0;
        }

        var currentStart = FloorToBucket(nowUtc, bucketMinutes);
        var fromUtc = currentStart - TimeSpan.FromMinutes(bucketMinutes);
        var observations = _telemetry.GetObservations(sensorId, fromUtc, null);
        if (observations.Count == 0)
        {
            return 0;
        }

        var written = 0;
        foreach (var group in observations.GroupBy(observation => FloorToBucket(observation.TimestampUtc, bucketMinutes)))
        {
            foreach (var bucket in TelemetryRollup.AggregatePerChannel(sensorId, group.ToList(), group.Key, bucketMinutes))
            {
                _telemetry.UpsertStatisticsBucket(bucket);
                written++;
            }
        }

        return written;
    }

    private static int ResolveRetentionDays(int? configuredValue, int fallback)
    {
        return configuredValue is int configured && configured > 0 ? configured : fallback;
    }

    private static DateTimeOffset FloorToBucket(DateTimeOffset timestampUtc, int bucketMinutes)
    {
        var bucketSpan = TimeSpan.FromMinutes(Math.Max(bucketMinutes, 1));
        var ticks = timestampUtc.UtcTicks - (timestampUtc.UtcTicks % bucketSpan.Ticks);
        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    private bool ShouldRecordStateChangeEvent(SensorObservation? previousObservation, SensorExecutionResult result)
    {
        return previousObservation is null || previousObservation.State != result.State;
    }
}

/// <summary>Summary of one <see cref="InMemoryMonitoringWorkspaceStore.RunTelemetryMaintenance"/> pass.</summary>
public readonly record struct TelemetryMaintenanceResult(
    int Sensors,
    int BucketsWritten,
    int ObservationsPruned,
    int StatisticsPruned,
    int EventsPruned);
