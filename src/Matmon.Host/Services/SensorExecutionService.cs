using Matmon.Core.Domain;

namespace Matmon.Host.Services;

public sealed class SensorExecutionService : ISensorExecutionService
{
    private readonly IMonitoringWorkspaceStore _workspaceStore;
    private readonly IReadOnlyDictionary<string, ISensorExecutor> _executors;
    private readonly MonitoringInheritanceResolver _resolver = new();

    public SensorExecutionService(
        IMonitoringWorkspaceStore workspaceStore,
        IEnumerable<ISensorExecutor> executors)
    {
        _workspaceStore = workspaceStore;
        _executors = executors.ToDictionary(executor => executor.SensorTypeKey, StringComparer.OrdinalIgnoreCase);
    }

    public async ValueTask<SensorExecutionResult> ExecuteNowAsync(
        Guid sensorId,
        MonitoringSettings? overrideSettings = null,
        CancellationToken cancellationToken = default)
    {
        var sensor = _workspaceStore.FindElement(sensorId) as SensorElement
            ?? throw new InvalidOperationException("Selected element is not a sensor.");

        var lineage = BuildLineage(sensor);
        var snapshot = _workspaceStore.Workspace;
        var templateMap = snapshot.Templates.ToDictionary(template => template.Id);
        var effectiveSettings = _resolver.Resolve(lineage, templateMap);

        if (overrideSettings is not null)
        {
            ApplyOverrideSettings(effectiveSettings, overrideSettings);
        }

        if (sensor.IsPaused)
        {
            var pausedResult = SensorExecutionResult.Paused("Sensor is paused.");
            _workspaceStore.RecordSensorObservation(sensor.Id, pausedResult, DateTimeOffset.UtcNow, effectiveSettings);
            return pausedResult;
        }

        if (!_executors.TryGetValue(sensor.SensorTypeKey, out var executor))
        {
            throw new InvalidOperationException($"No executor is registered for sensor type '{sensor.SensorTypeKey}'.");
        }

        ApplySensorCredentialDefaults(effectiveSettings, sensor.SensorTypeKey, snapshot.SensorDefinitions);

        if (effectiveSettings.Enabled == false)
        {
            var disabledResult = SensorExecutionResult.Disabled("Sensor is disabled.");
            _workspaceStore.RecordSensorObservation(sensor.Id, disabledResult, DateTimeOffset.UtcNow, effectiveSettings);
            return disabledResult;
        }

        var target = SensorTargetResolver.Resolve(sensor, lineage);

        return await ExecuteCoreAsync(
            executor,
            sensor.SensorTypeKey,
            target,
            effectiveSettings,
            sensor.Id,
            recordObservation: true,
            cancellationToken);
    }

    public ValueTask<SensorExecutionResult> ExecuteTransientAsync(
        string sensorTypeKey,
        string target,
        MonitoringSettings settings,
        CancellationToken cancellationToken = default)
    {
        ApplySensorCredentialDefaults(settings, sensorTypeKey, _workspaceStore.Workspace.SensorDefinitions);
        return ExecuteCoreAsync(
            ResolveExecutor(sensorTypeKey),
            sensorTypeKey,
            target,
            settings.Clone(),
            sensorId: null,
            recordObservation: false,
            cancellationToken);
    }

    private IReadOnlyList<MonitoringElement> BuildLineage(MonitoringElement element)
    {
        var lineage = new List<MonitoringElement>();
        var current = element;

        while (true)
        {
            lineage.Add(current);

            if (current.ParentId is not Guid parentId)
            {
                break;
            }

            current = _workspaceStore.FindElement(parentId)
                ?? throw new InvalidOperationException($"Parent element '{parentId}' could not be found.");
        }

        lineage.Reverse();
        return lineage;
    }

    private static void ApplyOverrideSettings(MonitoringSettings target, MonitoringSettings source)
    {
        target.Enabled = source.Enabled ?? target.Enabled;
        target.PollingInterval = source.PollingInterval ?? target.PollingInterval;
        target.Timeout = source.Timeout ?? target.Timeout;
        target.RetryCount = source.RetryCount ?? target.RetryCount;
        target.EventRetentionDays = source.EventRetentionDays ?? target.EventRetentionDays;
        target.ObservationRetentionDays = source.ObservationRetentionDays ?? target.ObservationRetentionDays;
        target.StatisticsRetentionDays = source.StatisticsRetentionDays ?? target.StatisticsRetentionDays;
        target.StatisticsBucketMinutes = source.StatisticsBucketMinutes ?? target.StatisticsBucketMinutes;
        target.DefaultChannelKey = source.DefaultChannelKey ?? target.DefaultChannelKey;

        foreach (var threshold in source.Thresholds)
        {
            target.Thresholds[threshold.Key] = threshold.Value;
        }

        foreach (var parameter in source.Parameters)
        {
            target.Parameters[parameter.Key] = parameter.Value;
        }
    }

    private ISensorExecutor ResolveExecutor(string sensorTypeKey)
    {
        if (_executors.TryGetValue(sensorTypeKey, out var executor))
        {
            return executor;
        }

        throw new InvalidOperationException($"No executor is registered for sensor type '{sensorTypeKey}'.");
    }

    private static void ApplySensorCredentialDefaults(
        MonitoringSettings settings,
        string sensorTypeKey,
        IReadOnlyList<SensorDefinition> sensorDefinitions)
    {
        var definition = sensorDefinitions.FirstOrDefault(candidate =>
            string.Equals(candidate.Key, sensorTypeKey, StringComparison.OrdinalIgnoreCase));
        if (definition is null)
        {
            return;
        }

        MonitoringSettings.ApplyCredentialValuesForKinds(settings, definition.CredentialKinds);
    }

    private async ValueTask<SensorExecutionResult> ExecuteCoreAsync(
        ISensorExecutor executor,
        string sensorTypeKey,
        string target,
        MonitoringSettings settings,
        Guid? sensorId,
        bool recordObservation,
        CancellationToken cancellationToken)
    {
        var context = new SensorExecutionContext(sensorTypeKey, target, settings);
        var result = await executor.ExecuteAsync(context, cancellationToken);
        result = ApplyDefaultChannelSelection(settings, result);

        if (recordObservation && sensorId is Guid id)
        {
            _workspaceStore.RecordSensorObservation(id, result, DateTimeOffset.UtcNow, settings);
        }

        return result;
    }

    private static SensorExecutionResult ApplyDefaultChannelSelection(
        MonitoringSettings settings,
        SensorExecutionResult result)
    {
        if (string.IsNullOrWhiteSpace(settings.DefaultChannelKey) || result.Channels.Count == 0)
        {
            return result;
        }

        var selectedChannel = result.Channels.FirstOrDefault(channel =>
            string.Equals(channel.Key, settings.DefaultChannelKey, StringComparison.OrdinalIgnoreCase));
        if (selectedChannel is null)
        {
            return result;
        }

        var selectedValue = selectedChannel.Value ?? result.Value;
        if (!selectedValue.HasValue)
        {
            return result;
        }

        return result with
        {
            DefaultChannelKey = selectedChannel.Key,
            Value = selectedValue,
            Channels = result.Channels
                .Select(channel => channel with
                {
                    IsDefault = string.Equals(channel.Key, selectedChannel.Key, StringComparison.OrdinalIgnoreCase)
                })
                .ToArray()
        };
    }
}
