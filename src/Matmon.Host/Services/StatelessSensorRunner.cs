using Matmon.Core.Domain;

namespace Matmon.Host.Services;

/// <summary>Runs a single sensor on demand with NO workspace/persistence — the engine behind the Executor
/// run-mode. Reuses the exact same <see cref="ISensorExecutor"/> implementations + credential mapping as the
/// Primary/Secondary, so cloud sensors share 100% of the executor code. Constructed once (singleton); the
/// executors themselves are resolved per call from the injected set.</summary>
public sealed class StatelessSensorRunner
{
    private readonly IReadOnlyDictionary<string, ISensorExecutor> _executors;

    private readonly IReadOnlyList<SensorDefinition> _catalog;

    public StatelessSensorRunner(IEnumerable<ISensorExecutor> executors)
    {
        _executors = executors.ToDictionary(executor => executor.SensorTypeKey, StringComparer.OrdinalIgnoreCase);
        // Only advertise types this process can actually run (e.g. probe sensors aren't registered here).
        _catalog = SensorDefinitionCatalog.BuiltIns
            .Where(definition => _executors.ContainsKey(definition.Key))
            .ToArray();
    }

    /// <summary>The runnable sensor catalog (types + parameters) — the cloud fetches this to build its UI.</summary>
    public IReadOnlyList<SensorDefinition> Catalog => _catalog;

    public async Task<SensorExecutionResult> ExecuteAsync(ExecuteSensorRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.SensorTypeKey) ||
            !_executors.TryGetValue(request.SensorTypeKey, out var executor))
        {
            return SensorExecutionResult.Critical(TimeSpan.Zero, $"No executor is registered for sensor type '{request.SensorTypeKey}'.");
        }

        var settings = new MonitoringSettings
        {
            Parameters = new Dictionary<string, string>(
                request.Parameters ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase)
        };
        if (request.TimeoutSeconds is int seconds && seconds > 0)
        {
            settings.Timeout = TimeSpan.FromSeconds(seconds);
        }

        if (request.Credential is { } credential)
        {
            var bundle = new MonitoringCredentialBundle
            {
                Name = "cloud",
                Kind = credential.Kind,
                Values = new Dictionary<string, string>(credential.Values, StringComparer.OrdinalIgnoreCase)
            };
            settings.Credentials.Add(bundle);
            settings.SelectedCredentialId = bundle.Id;
        }

        // Map the credential bundle onto the parameter keys the executor reads (same path as Primary/Secondary).
        var definition = Catalog.FirstOrDefault(candidate =>
            string.Equals(candidate.Key, request.SensorTypeKey, StringComparison.OrdinalIgnoreCase));
        if (definition is not null)
        {
            MonitoringSettings.ApplyCredentialValuesForKinds(settings, definition.CredentialKinds);
        }

        try
        {
            var result = await executor.ExecuteAsync(
                new SensorExecutionContext(request.SensorTypeKey, request.Target ?? string.Empty, settings),
                cancellationToken);
            return SensorExecutionResultHelper.ApplyDefaultChannelSelection(settings, result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return SensorExecutionResult.Critical(TimeSpan.Zero, ex.Message);
        }
    }
}
