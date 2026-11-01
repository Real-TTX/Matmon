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
    TimeSpan Timeout)
{
    /// <summary>The scan runs ON the machine it looks at - an agent discovering its own host. Sensors that need a
    /// remote way in elsewhere (WinRM, SSH) are suggested anyway then: on the agent they run locally, without a
    /// listener and without a credential (<see cref="LocalTarget"/>).</summary>
    public bool OnThisMachine { get; init; }

    /// <summary>On this machine and it is Windows: the Windows sensors run locally in Windows PowerShell.</summary>
    public bool LocalWindows => OnThisMachine && OperatingSystem.IsWindows();

    /// <summary>On this machine and it is Linux: the Linux SSH sensors run their script locally.</summary>
    public bool LocalLinux => OnThisMachine && OperatingSystem.IsLinux();

    /// <summary>The target a suggestion made on this machine carries: always the machine itself, so the sensor runs
    /// locally whatever address the host was added by.</summary>
    public const string ThisMachine = "localhost";
}

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
