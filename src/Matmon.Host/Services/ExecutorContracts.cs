using Matmon.Core.Domain;

namespace Matmon.Host.Services;

/// <summary>A single sensor execution requested by Matmon.Cloud over the Executor run-mode API. The cloud
/// sends the type + target + raw parameters (+ optional credential values); the executor runs it once and
/// returns the <see cref="SensorExecutionResult"/>. Nothing is persisted.</summary>
public sealed record ExecuteSensorRequest(
    string SensorTypeKey,
    string? Target,
    Dictionary<string, string>? Parameters,
    int? TimeoutSeconds,
    ExecuteCredential? Credential);

/// <summary>Optional credential values for the execution (kept off <see cref="MonitoringSettings"/> so the
/// [JsonIgnore]'d bundle Values survive the wire - the runner rebuilds the bundle in memory).</summary>
public sealed record ExecuteCredential(MonitoringCredentialKind Kind, Dictionary<string, string> Values);
