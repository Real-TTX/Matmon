using Matmon.Core.Domain;
using Matmon.Host.Services;

namespace Matmon.Tests;

/// <summary>The on-demand run store is the queue that makes "run now / test" on a remote-probe sensor execute
/// ON the probe: the primary enqueues a job, the probe pulls it once, runs it and posts the result back, which
/// wakes the waiting request. These pin the claim-once and wake/timeout behaviour that correctness depends on.</summary>
public class OnDemandRunStoreTests
{
    private static OnDemandRunJob CreateSensorJob(OnDemandRunStore store, string probeId = "probe-milla") =>
        store.Create(probeId, sensorId: Guid.NewGuid(), "ping", "127.0.0.1", new MonitoringSettings(),
            recordObservation: true, ProbeRunJobKind.Sensor);

    [Fact]
    public void A_created_job_is_pending_and_retrievable()
    {
        var store = new OnDemandRunStore();

        var job = CreateSensorJob(store);

        Assert.Equal(OnDemandRunStatus.Pending, job.Status);
        Assert.Same(job, store.TryGet(job.Id));
    }

    [Fact]
    public void TakePending_claims_a_job_once_so_two_polls_cannot_double_run_it()
    {
        var store = new OnDemandRunStore();
        var job = CreateSensorJob(store);

        var first = store.TakePending("probe-milla");
        var second = store.TakePending("probe-milla");

        Assert.Single(first);
        Assert.Equal(job.Id, first[0].Id);
        Assert.Equal(OnDemandRunStatus.Running, first[0].Status);
        Assert.Empty(second);
    }

    [Fact]
    public void TakePending_only_returns_the_matching_probes_jobs()
    {
        var store = new OnDemandRunStore();
        CreateSensorJob(store, "probe-milla");
        CreateSensorJob(store, "probe-other");

        var forMilla = store.TakePending("probe-milla");

        Assert.Single(forMilla);
        Assert.Equal("probe-milla", forMilla[0].ProbeId);
    }

    [Fact]
    public async Task Complete_wakes_a_waiter_with_the_result()
    {
        var store = new OnDemandRunStore();
        var job = CreateSensorJob(store);
        store.TakePending("probe-milla");
        var expected = SensorExecutionResult.Critical(TimeSpan.FromMilliseconds(5), "boom");

        var waiter = store.WaitForCompletionAsync(job.Id, TimeSpan.FromSeconds(5), CancellationToken.None);
        store.Complete(job.Id, expected, oids: null, error: null);
        var completed = await waiter;

        Assert.NotNull(completed);
        Assert.Same(expected, completed!.Result);
        Assert.Equal(OnDemandRunStatus.Complete, completed.Status);
    }

    [Fact]
    public void Complete_is_idempotent_so_a_duplicate_post_cannot_record_twice()
    {
        var store = new OnDemandRunStore();
        var job = CreateSensorJob(store);
        store.TakePending("probe-milla");
        var result = SensorExecutionResult.Critical(TimeSpan.Zero, "once");

        Assert.True(store.Complete(job.Id, result, oids: null, error: null));
        Assert.False(store.Complete(job.Id, result, oids: null, error: null));
    }

    [Fact]
    public async Task WaitForCompletion_returns_null_on_timeout_when_the_probe_never_reports()
    {
        var store = new OnDemandRunStore();
        var job = CreateSensorJob(store);
        store.TakePending("probe-milla");

        var completed = await store.WaitForCompletionAsync(job.Id, TimeSpan.FromMilliseconds(50), CancellationToken.None);

        Assert.Null(completed);
    }

    [Fact]
    public async Task An_unknown_job_id_never_hangs_a_waiter()
    {
        var store = new OnDemandRunStore();

        var completed = await store.WaitForCompletionAsync(Guid.NewGuid(), TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Null(completed);
    }
}
