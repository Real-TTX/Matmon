using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Matmon.Core;
using Matmon.Core.Domain;
using Matmon.Host.Services;
using Matmon.Probe;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

/// <summary>
/// A discovery job queued for a probe or an agent must be picked up whatever its sensors do. The worker used to run the
/// sensor pass, the run jobs and the discovery jobs inside ONE try, in that order: a check that ended in a cancellation
/// the probe had not asked for aborted the pass, skipped the discovery poll - and, the sensor being due again, did so on
/// every tick, so the scan stayed "pending" for good. A slow sensor pass also held a queued scan back, and a running scan
/// stopped every sensor. Driven against a stand-in primary.
/// </summary>
public sealed class ProbeDiscoveryWorkerTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task A_check_that_ends_in_a_stray_cancellation_does_not_starve_discovery()
    {
        var primary = new StandInPrimary("stray-cancel");
        await using var worker = await StartAsync(primary, new ThrowingExecutor("stray-cancel"));

        Assert.True(await primary.DiscoveryCompleted.Task.WaitAsync(TimeSpan.FromSeconds(30)));
        // ... and the failed check is reported as a failed check, not lost with the whole pass.
        var observation = await primary.Observation.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Contains("\"critical\"", observation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_slow_sensor_pass_does_not_hold_a_queued_scan_back()
    {
        var primary = new StandInPrimary("slow");
        var slow = new SlowExecutor("slow", TimeSpan.FromSeconds(60));
        await using var worker = await StartAsync(primary, slow);

        await slow.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        // The sensor is still busy for a minute; the scan must not wait for it.
        Assert.True(await primary.DiscoveryCompleted.Task.WaitAsync(TimeSpan.FromSeconds(20)));
    }

    private static async Task<RunningWorker> StartAsync(StandInPrimary primary, ISensorExecutor executor)
    {
        var options = new ProbeRuntimeOptions
        {
            Mode = AppMode.Secondary,
            PrimaryUrl = "http://primary.test/",
            ProbeId = "agent-1",
            ProbeToken = "token",
            OfflineBufferRetentionDays = 0
        };
        var worker = new SlaveSensorWorker(
            new StandInClientFactory(primary),
            [executor],
            options,
            new SlaveProbeRuntimeState(),
            new NetworkDiscoveryService([]),
            NullLogger<SlaveSensorWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        return new RunningWorker(worker);
    }

    private sealed class RunningWorker(SlaveSensorWorker worker) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await worker.StopAsync(stop.Token);
            }
            catch (OperationCanceledException)
            {
            }

            worker.Dispose();
        }
    }

    private sealed class ThrowingExecutor(string key) : ISensorExecutor
    {
        public string SensorTypeKey => key;

        // What a timeout that escapes an executor looks like: a cancellation nobody on the probe asked for.
        public ValueTask<SensorExecutionResult> ExecuteAsync(SensorExecutionContext context, CancellationToken cancellationToken = default) =>
            throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");
    }

    private sealed class SlowExecutor(string key, TimeSpan delay) : ISensorExecutor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string SensorTypeKey => key;

        public async ValueTask<SensorExecutionResult> ExecuteAsync(SensorExecutionContext context, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(delay, cancellationToken);
            return SensorExecutionResult.Healthy(delay, "slow but fine");
        }
    }

    private sealed class StandInClientFactory(StandInPrimary primary) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(primary, disposeHandler: false);
    }

    /// <summary>Answers the four probe endpoints the worker uses; hands out ONE discovery job (a closed port on loopback,
    /// no ping - finishes at once, needs no rights) and records when it was reported complete.</summary>
    private sealed class StandInPrimary(string sensorType) : HttpMessageHandler
    {
        private readonly Guid _sensorId = Guid.NewGuid();
        private readonly Guid _jobId = Guid.NewGuid();
        private int _jobHandedOut;

        public TaskCompletionSource<bool> DiscoveryCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<string> Observation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);

            if (path.EndsWith("/assignments", StringComparison.Ordinal))
            {
                return Ok(new ProbeSensorAssignmentsResponse(
                    "agent-1",
                    "Agent 1",
                    DateTimeOffset.UtcNow,
                    [new ProbeSensorAssignment(_sensorId, "Sensor", "Agent 1 / Sensor", sensorType, "localhost", false, new MonitoringSettings(), null)]));
            }

            if (path.EndsWith("/observations", StringComparison.Ordinal))
            {
                Observation.TrySetResult(body);
                return Ok(new { });
            }

            if (path.EndsWith("/run-jobs", StringComparison.Ordinal))
            {
                return Ok(new { jobs = Array.Empty<object>() });
            }

            if (path.EndsWith("/discovery-jobs", StringComparison.Ordinal))
            {
                var jobs = Interlocked.Exchange(ref _jobHandedOut, 1) == 0
                    ? new[]
                    {
                        new ProbeDiscoveryJobAssignment(
                            _jobId,
                            "127.0.0.1",
                            DiscoveryDefaults.Options with
                            {
                                UsePing = false,
                                PingFirst = false,
                                UseTcpPorts = true,
                                TcpPorts = [1],
                                UseSnmp = false,
                                UseReverseDns = false,
                                TimeoutMs = 300
                            })
                    }
                    : [];
                return Ok(new ProbeDiscoveryJobAssignmentsResponse(jobs));
            }

            if (path.EndsWith("/discovery-jobs/results", StringComparison.Ordinal))
            {
                var batch = JsonSerializer.Deserialize<ProbeDiscoveryJobResultBatch>(body, Json);
                if (batch?.Results.Any(result => result.JobId == _jobId && result.IsComplete && result.ErrorMessage is null) == true)
                {
                    DiscoveryCompleted.TrySetResult(true);
                }

                return Ok(new ProbeDiscoveryJobResultPostResponse(1, false));
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Ok<T>(T value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value, Json), Encoding.UTF8, "application/json")
        };
    }
}
