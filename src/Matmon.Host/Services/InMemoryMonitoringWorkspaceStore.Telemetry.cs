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
            PruneSensorHistory(sensorId, timestampUtc, settings);
            UpdateSensorStatistics(sensorId, result, timestampUtc, settings);
            PruneEvents(timestampUtc, settings);
            PruneStatistics(sensorId, timestampUtc, settings);
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

    private void PruneEvents(DateTimeOffset now, MonitoringSettings? settings)
    {
        var retentionDays = ResolveRetentionDays(settings?.EventRetentionDays, DefaultEventRetentionDays);
        if (retentionDays <= 0)
        {
            return;
        }

        var cutoff = now - TimeSpan.FromDays(retentionDays);
        _telemetry.PruneEvents(cutoff);
    }

    private void PruneSensorHistory(Guid sensorId, DateTimeOffset now, MonitoringSettings? settings)
    {
        var retentionDays = ResolveRetentionDays(settings?.ObservationRetentionDays, DefaultObservationRetentionDays);
        if (retentionDays <= 0)
        {
            return;
        }

        var cutoff = now - TimeSpan.FromDays(retentionDays);
        _telemetry.PruneObservations(sensorId, cutoff);
    }

    private void PruneStatistics(Guid sensorId, DateTimeOffset now, MonitoringSettings? settings)
    {
        var retentionDays = ResolveRetentionDays(settings?.StatisticsRetentionDays, DefaultStatisticsRetentionDays);
        if (retentionDays <= 0)
        {
            return;
        }

        var cutoff = now - TimeSpan.FromDays(retentionDays);
        _telemetry.PruneStatistics(sensorId, cutoff);
    }

    private void UpdateSensorStatistics(Guid sensorId, SensorExecutionResult result, DateTimeOffset timestampUtc, MonitoringSettings? settings)
    {
        if (!TryGetStatisticSample(result, out var sampleValue, out var channelKey, out var unit))
        {
            return;
        }

        var bucketMinutes = ResolveRetentionDays(settings?.StatisticsBucketMinutes, DefaultStatisticsBucketMinutes);
        if (bucketMinutes <= 0)
        {
            return;
        }

        var bucketStartUtc = FloorToBucket(timestampUtc, bucketMinutes);
        var bucket = _telemetry.GetStatisticsBucket(sensorId, bucketMinutes, bucketStartUtc)
            ?? new SensorStatisticsBucket
            {
                SensorId = sensorId,
                BucketStartUtc = bucketStartUtc,
                BucketMinutes = bucketMinutes,
                DefaultChannelKey = channelKey,
                Unit = unit
            };

        bucket.DefaultChannelKey = channelKey;
        bucket.Unit = unit ?? bucket.Unit;
        bucket.SampleCount++;
        bucket.Average = bucket.Average is double average
            ? ((average * (bucket.SampleCount - 1)) + sampleValue) / bucket.SampleCount
            : sampleValue;
        bucket.Minimum = bucket.Minimum is double minimum ? Math.Min(minimum, sampleValue) : sampleValue;
        bucket.Maximum = bucket.Maximum is double maximum ? Math.Max(maximum, sampleValue) : sampleValue;
        bucket.LastValue = sampleValue;
        bucket.State = result.State;
        bucket.Message = result.Message;
        _telemetry.UpsertStatisticsBucket(bucket);
    }

    private static bool TryGetStatisticSample(
        SensorExecutionResult result,
        out double value,
        out string channelKey,
        out string? unit)
    {
        var defaultChannel = result.Channels.FirstOrDefault(channel =>
            channel.IsDefault ||
            (!string.IsNullOrWhiteSpace(result.DefaultChannelKey) &&
             string.Equals(channel.Key, result.DefaultChannelKey, StringComparison.OrdinalIgnoreCase)));

        if (defaultChannel is null && result.Channels.Count > 0)
        {
            defaultChannel = result.Channels[0];
        }

        if (defaultChannel?.Value is double channelValue)
        {
            value = channelValue;
            channelKey = string.IsNullOrWhiteSpace(defaultChannel.Key) ? result.DefaultChannelKey ?? "default" : defaultChannel.Key;
            unit = defaultChannel.Unit;
            return true;
        }

        if (result.Value.HasValue)
        {
            value = result.Value.Value;
            channelKey = string.IsNullOrWhiteSpace(result.DefaultChannelKey) ? "default" : result.DefaultChannelKey;
            unit = defaultChannel?.Unit;
            return true;
        }

        if (result.State == SensorState.Critical)
        {
            value = 0d;
            channelKey = string.IsNullOrWhiteSpace(result.DefaultChannelKey) ? "default" : result.DefaultChannelKey;
            unit = defaultChannel?.Unit;
            return true;
        }

        value = default;
        channelKey = string.Empty;
        unit = null;
        return false;
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
