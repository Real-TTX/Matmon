namespace Matmon.Core.Domain;

/// <summary>
/// The "Retries" setting (<see cref="MonitoringSettings.RetryCount"/>): a check that comes back Critical is run again, up
/// to that many times with a short pause, before the result counts - one lost packet or a busy device then does not
/// turn a sensor red. Used by the primary's polling and by a probe's scheduled checks alike (the one rule in one place);
/// a test from an editor runs once, because it should answer quickly. The setting used to be stored and shown and do
/// nothing at all.
/// </summary>
public static class SensorRetry
{
    public const int MaxRetries = 5;

    public static readonly TimeSpan DefaultPause = TimeSpan.FromSeconds(2);

    // Sensors whose answer does not change by asking again at once: they read state Matmon itself keeps (the probe
    // registry, the syslog database, the cloud's update signal, this machine's counters) or carry state between runs
    // (Mail Health sends a probe mail per run).
    private static readonly HashSet<string> NotRetried = new(StringComparer.OrdinalIgnoreCase)
    {
        "probe-heartbeat", "probe-health", "local-health", "mail-health", "matmon-update", "syslog-messages", "syslog-match", "push"
    };

    public static int RetriesFor(string? sensorTypeKey, MonitoringSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (sensorTypeKey is not null && NotRetried.Contains(sensorTypeKey))
        {
            return 0;
        }

        return Math.Clamp(settings.RetryCount ?? 0, 0, MaxRetries);
    }

    public static async ValueTask<SensorExecutionResult> ExecuteAsync(
        ISensorExecutor executor,
        SensorExecutionContext context,
        CancellationToken cancellationToken,
        TimeSpan? pause = null)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(context);

        var retries = RetriesFor(context.SensorTypeKey, context.Settings);
        var result = await executor.ExecuteAsync(context, cancellationToken);
        var attempts = 1;
        while (attempts <= retries && result.State == SensorState.Critical)
        {
            await Task.Delay(pause ?? DefaultPause, cancellationToken);
            result = await executor.ExecuteAsync(context, cancellationToken);
            attempts++;
        }

        // Still failing after retrying: say so, or "timed out" reads like one unlucky attempt.
        return attempts > 1 && result.State == SensorState.Critical
            ? result with { Message = $"{result.Message} (failed {attempts} times in a row)".Trim() }
            : result;
    }
}
