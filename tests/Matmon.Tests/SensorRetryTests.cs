using Matmon.Core.Domain;

namespace Matmon.Tests;

/// <summary>
/// "Retries" used to be stored and shown and do nothing. A Critical check is now run again up to RetryCount times
/// before it counts - and only a Critical one, and not for sensors whose answer cannot change by asking again.
/// </summary>
public sealed class SensorRetryTests
{
    private sealed class ScriptedExecutor(string key, params SensorState[] states) : ISensorExecutor
    {
        public int Calls { get; private set; }

        public string SensorTypeKey => key;

        public ValueTask<SensorDiscoveryCheckResult> DiscoverAsync(SensorDiscoveryContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(SensorDiscoveryCheckResult.NotAvailable);

        public ValueTask<SensorExecutionResult> ExecuteAsync(SensorExecutionContext context, CancellationToken cancellationToken = default)
        {
            var state = states[Math.Min(Calls, states.Length - 1)];
            Calls++;
            return ValueTask.FromResult(state == SensorState.Critical
                ? SensorExecutionResult.Critical(TimeSpan.Zero, "timed out")
                : state == SensorState.Warning
                    ? SensorExecutionResult.Warning(TimeSpan.Zero, "slow")
                    : SensorExecutionResult.Healthy(TimeSpan.Zero, "ok"));
        }
    }

    private static SensorExecutionContext Context(string key, int? retries) =>
        new(key, "10.0.0.1", new MonitoringSettings { RetryCount = retries });

    [Fact]
    public async Task A_failure_that_passes_on_a_retry_counts_as_the_pass()
    {
        var executor = new ScriptedExecutor("ping", SensorState.Critical, SensorState.Critical, SensorState.Healthy);

        var result = await SensorRetry.ExecuteAsync(executor, Context("ping", 3), default, TimeSpan.Zero);

        Assert.Equal(SensorState.Healthy, result.State);
        Assert.Equal(3, executor.Calls);
    }

    [Fact]
    public async Task A_failure_that_stays_is_reported_with_how_often_it_failed()
    {
        var executor = new ScriptedExecutor("ping", SensorState.Critical);

        var result = await SensorRetry.ExecuteAsync(executor, Context("ping", 2), default, TimeSpan.Zero);

        Assert.Equal(SensorState.Critical, result.State);
        Assert.Equal(3, executor.Calls);
        Assert.Equal("timed out (failed 3 times in a row)", result.Message);
    }

    [Fact]
    public async Task Without_retries_or_for_a_warning_the_check_runs_once()
    {
        var none = new ScriptedExecutor("ping", SensorState.Critical);
        var warning = new ScriptedExecutor("ping", SensorState.Warning);

        Assert.Equal(SensorState.Critical, (await SensorRetry.ExecuteAsync(none, Context("ping", null), default, TimeSpan.Zero)).State);
        Assert.Equal(SensorState.Warning, (await SensorRetry.ExecuteAsync(warning, Context("ping", 3), default, TimeSpan.Zero)).State);
        Assert.Equal(1, none.Calls);
        Assert.Equal(1, warning.Calls);
    }

    [Theory]
    [InlineData("mail-health")]
    [InlineData("syslog-match")]
    [InlineData("probe-heartbeat")]
    public async Task Sensors_whose_answer_cannot_change_by_asking_again_are_not_retried(string key)
    {
        var executor = new ScriptedExecutor(key, SensorState.Critical, SensorState.Healthy);

        var result = await SensorRetry.ExecuteAsync(executor, Context(key, 3), default, TimeSpan.Zero);

        Assert.Equal(SensorState.Critical, result.State);
        Assert.Equal(1, executor.Calls);
    }

    [Fact]
    public void Retries_are_capped()
    {
        Assert.Equal(SensorRetry.MaxRetries, SensorRetry.RetriesFor("ping", new MonitoringSettings { RetryCount = 50 }));
        Assert.Equal(0, SensorRetry.RetriesFor("ping", new MonitoringSettings { RetryCount = -1 }));
    }
}
