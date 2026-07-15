using Matmon.Core.Domain;

namespace Matmon.Host.Services;

public sealed class SensorExecutionService : ISensorExecutionService
{
    private readonly IMonitoringWorkspaceStore _workspaceStore;
    private readonly IReadOnlyDictionary<string, ISensorExecutor> _executors;

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
        // Resolve everything atomically under the store lock into a detached plan, so the polling
        // hot path never reads the live element tree while a request is mutating it.
        var plan = _workspaceStore.GetSensorExecutionPlan(sensorId)
            ?? throw new InvalidOperationException("Selected element is not a sensor.");

        var effectiveSettings = plan.EffectiveSettings;

        if (overrideSettings is not null)
        {
            ApplyOverrideSettings(effectiveSettings, overrideSettings);
        }

        if (plan.IsPaused)
        {
            var pausedResult = SensorExecutionResult.Paused("Sensor is paused.");
            _workspaceStore.RecordSensorObservation(plan.SensorId, pausedResult, DateTimeOffset.UtcNow, effectiveSettings);
            return pausedResult;
        }

        ApplySensorCredentialDefaults(effectiveSettings, plan.SensorTypeKey, _workspaceStore.GetSensorDefinitions());

        if (effectiveSettings.Enabled == false)
        {
            var disabledResult = SensorExecutionResult.Disabled("Sensor is disabled.");
            _workspaceStore.RecordSensorObservation(plan.SensorId, disabledResult, DateTimeOffset.UtcNow, effectiveSettings);
            return disabledResult;
        }

        var (executor, executionSettings) = ResolveExecutorAndSettings(plan.SensorTypeKey, effectiveSettings);

        return await ExecuteCoreAsync(
            executor,
            plan.SensorTypeKey,
            plan.Target,
            executionSettings,
            plan.SensorId,
            recordObservation: true,
            cancellationToken);
    }

    public ValueTask<SensorExecutionResult> ExecuteTransientAsync(
        string sensorTypeKey,
        string target,
        MonitoringSettings settings,
        CancellationToken cancellationToken = default)
    {
        ApplySensorCredentialDefaults(settings, sensorTypeKey, _workspaceStore.GetSensorDefinitions());
        var (executor, executionSettings) = ResolveExecutorAndSettings(sensorTypeKey, settings.Clone());
        return ExecuteCoreAsync(
            executor,
            sensorTypeKey,
            target,
            executionSettings,
            sensorId: null,
            recordObservation: false,
            cancellationToken);
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

    // Resolves the executor for a type key. Built-in types map directly; a custom (admin-authored) script type
    // is data-defined in the workspace, so it has no registered executor - it is routed to the Local Script
    // engine with the type's script injected into a CLONE of the settings (instance settings stay untouched).
    private (ISensorExecutor Executor, MonitoringSettings Settings) ResolveExecutorAndSettings(string sensorTypeKey, MonitoringSettings settings)
    {
        if (_executors.TryGetValue(sensorTypeKey, out var executor))
        {
            return (executor, settings);
        }

        var definition = _workspaceStore.GetSensorDefinitions().FirstOrDefault(candidate =>
            candidate.IsCustomScript
            && !string.IsNullOrWhiteSpace(candidate.ScriptBody)
            && string.Equals(candidate.Key, sensorTypeKey, StringComparison.OrdinalIgnoreCase));
        if (definition is not null && _executors.TryGetValue(LocalScriptSensorExecutor.Definition.Key, out var scriptEngine))
        {
            var injected = settings.Clone();
            injected.Parameters["script"] = definition.ScriptBody!;
            injected.Parameters["local.shell"] = string.IsNullOrWhiteSpace(definition.ScriptLanguage) ? "pwsh" : definition.ScriptLanguage!;
            injected.Parameters["outputFormat"] = string.IsNullOrWhiteSpace(definition.ScriptOutputFormat) ? "auto" : definition.ScriptOutputFormat!;
            if (!string.IsNullOrWhiteSpace(definition.ScriptRegexPattern))
            {
                injected.Parameters["regexPattern"] = definition.ScriptRegexPattern!;
            }

            return (scriptEngine, injected);
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
        // Feed the previous observation in so stateful sensors (e.g. Mail Health) can correlate runs.
        // O(1) cache lookup; only the polling path has a sensorId (transient/preview passes null).
        var previousObservation = sensorId is Guid previousId
            ? _workspaceStore.GetLatestObservation(previousId)
            : null;
        var context = new SensorExecutionContext(sensorTypeKey, target, settings, sensorId, previousObservation);
        var result = await executor.ExecuteAsync(context, cancellationToken);
        result = SensorExecutionResultHelper.ApplyDefaultChannelSelection(settings, result);

        if (recordObservation && sensorId is Guid id)
        {
            _workspaceStore.RecordSensorObservation(id, result, DateTimeOffset.UtcNow, settings);
        }

        return result;
    }

}
