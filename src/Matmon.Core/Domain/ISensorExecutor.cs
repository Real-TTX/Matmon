namespace Matmon.Core.Domain;

public interface ISensorExecutor
{
    string SensorTypeKey { get; }

    ValueTask<SensorDiscoveryCheckResult> DiscoverAsync(
        SensorDiscoveryContext context,
        CancellationToken cancellationToken = default)
    {
        _ = context;
        _ = cancellationToken;
        return ValueTask.FromResult(SensorDiscoveryCheckResult.NotAvailable);
    }

    ValueTask<SensorExecutionResult> ExecuteAsync(
        SensorExecutionContext context,
        CancellationToken cancellationToken = default);
}

public sealed record SensorDiscoveryContext(
    string Host,
    bool PingAlive,
    double? PingMs,
    IReadOnlyList<int> OpenTcpPorts,
    bool SnmpResponded,
    string? SnmpSummary,
    string SnmpCommunity,
    string SnmpVersion,
    int SnmpPort,
    TimeSpan Timeout);

public sealed record SensorDiscoveryCheckResult(
    bool IsAvailable,
    IReadOnlyList<SensorDiscoverySuggestion> Suggestions)
{
    public static SensorDiscoveryCheckResult NotAvailable { get; } = new(false, []);

    public static SensorDiscoveryCheckResult Available(params SensorDiscoverySuggestion[] suggestions)
    {
        return new SensorDiscoveryCheckResult(true, suggestions);
    }
}

public sealed record SensorDiscoverySuggestion(
    string SensorTypeKey,
    string Name,
    string Target,
    MonitoringSettings Settings,
    string Reason,
    int Confidence);
