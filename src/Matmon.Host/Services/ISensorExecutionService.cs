using Matmon.Core.Domain;

namespace Matmon.Host.Services;

public interface ISensorExecutionService
{
    ValueTask<SensorExecutionResult> ExecuteNowAsync(
        Guid sensorId,
        MonitoringSettings? overrideSettings = null,
        CancellationToken cancellationToken = default);

    ValueTask<SensorExecutionResult> ExecuteTransientAsync(
        string sensorTypeKey,
        string target,
        MonitoringSettings settings,
        CancellationToken cancellationToken = default);
}
