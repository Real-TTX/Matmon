using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Matmon.Core;
using Matmon.Core.Domain;
using Matmon.Probe;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

/// <summary>
/// A batch of results the primary refuses must not jam a probe's buffer. The worker used to send the oldest 500
/// buffered results, and on any answer but success stop and try the very same 500 on the next tick - forever: an agent
/// whose primary (or a proxy in front of it) refused that batch showed "connected" (its heartbeat went through), its
/// results piled up, nothing arrived and nothing said why. Now a refused batch is split until the one result it refuses
/// is dropped, a too-large one goes in halves, and the reason is shown.
/// </summary>
public sealed class ProbeResultFlushTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task One_result_the_primary_refuses_does_not_hold_back_the_others()
    {
        var poison = Guid.NewGuid();
        var good = Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()).ToList();
        var path = Path.Combine(Path.GetTempPath(), $"mm-flush-{Guid.NewGuid():N}.jsonl");
        try
        {
            var seed = new OfflineObservationBuffer(path);
            var now = DateTimeOffset.UtcNow;
            seed.Add(new ProbeSensorObservationReport(good[0], SensorExecutionResult.Healthy(TimeSpan.Zero, "ok"), now.AddMinutes(-10)));
            seed.Add(new ProbeSensorObservationReport(poison, SensorExecutionResult.Healthy(TimeSpan.Zero, "bad"), now.AddMinutes(-9)));
            foreach (var id in good.Skip(1))
            {
                seed.Add(new ProbeSensorObservationReport(id, SensorExecutionResult.Healthy(TimeSpan.Zero, "ok"), now.AddMinutes(-8)));
            }

            var primary = new RefusingPrimary(poison, good.Count + 1);
            var state = new SlaveProbeRuntimeState();
            await using var worker = await StartAsync(primary, path, state);

            await primary.AllGoodArrived.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.All(good, id => Assert.Contains(id, primary.Recorded.Keys));
            Assert.DoesNotContain(poison, primary.Recorded.Keys);
        }
        finally
        {
            foreach (var file in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + "*"))
            {
                File.Delete(file);
            }
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, true)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, true)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, false)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    public void Only_an_answer_that_can_never_change_counts_as_a_refusal(HttpStatusCode status, bool refusal) =>
        Assert.Equal(refusal, SlaveSensorWorker.IsRefusal(status));

    private static async Task<RunningWorker> StartAsync(RefusingPrimary primary, string bufferPath, SlaveProbeRuntimeState state)
    {
        var options = new ProbeRuntimeOptions
        {
            Mode = AppMode.Secondary,
            PrimaryUrl = "http://primary.test/",
            ProbeId = "agent-1",
            ProbeToken = "token",
            OfflineBufferRetentionDays = 7,
            OfflineBufferPath = bufferPath
        };
        var worker = new SlaveSensorWorker(
            new ClientFactory(primary),
            [],
            options,
            state,
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

    private sealed class ClientFactory(RefusingPrimary primary) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(primary, disposeHandler: false);
    }

    /// <summary>No sensors assigned; refuses (400) every result batch that contains the poison result, like a primary
    /// that cannot read one result in it.</summary>
    private sealed class RefusingPrimary(Guid poison, int expectedGood) : HttpMessageHandler
    {
        public ConcurrentDictionary<Guid, bool> Recorded { get; } = new();

        public TaskCompletionSource AllGoodArrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);

            if (path.EndsWith("/assignments", StringComparison.Ordinal))
            {
                return Ok(new ProbeSensorAssignmentsResponse("agent-1", "Agent 1", DateTimeOffset.UtcNow, []));
            }

            if (path.EndsWith("/observations", StringComparison.Ordinal))
            {
                var batch = JsonSerializer.Deserialize<ProbeSensorObservationBatch>(body, Json)!;
                if (batch.Observations.Any(o => o.SensorId == poison))
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("cannot read that") };
                }

                foreach (var observation in batch.Observations)
                {
                    Recorded[observation.SensorId] = true;
                }

                if (Recorded.Count >= expectedGood - 1)
                {
                    AllGoodArrived.TrySetResult();
                }

                return Ok(new { recorded = batch.Observations.Count, repeated = 0, skipped = 0 });
            }

            if (path.EndsWith("/run-jobs", StringComparison.Ordinal))
            {
                return Ok(new { jobs = Array.Empty<object>() });
            }

            if (path.EndsWith("/discovery-jobs", StringComparison.Ordinal))
            {
                return Ok(new ProbeDiscoveryJobAssignmentsResponse([]));
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Ok<T>(T value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value, Json), Encoding.UTF8, "application/json")
        };
    }
}
