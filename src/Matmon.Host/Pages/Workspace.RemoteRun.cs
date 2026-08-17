using Matmon.Core.Domain;
using Matmon.Host.Services;

namespace Matmon.Host.Pages;

// On-demand run routing: any run of a sensor that belongs to a REMOTE probe must execute on that probe,
// not in-process on the primary. These helpers decide local vs remote and enqueue a pull-based run job
// (mirroring the discovery-job flow) via IOnDemandRunStore. Network discovery already routes correctly
// and is untouched.
public sealed partial class WorkspaceModel
{
    private enum RemoteRunOutcome
    {
        // The sensor lives on the local primary - run it in-process as before.
        Local,
        // Routed to a remote probe and the probe reported back within the wait window.
        Completed,
        // Routed to a remote probe but it hadn't reported back yet (will land on the next sync).
        Queued
    }

    private readonly record struct SavedSensorRunRouting(
        RemoteRunOutcome Outcome,
        SensorExecutionResult? Result,
        string ProbeName);

    /// <summary>
    /// Routes an on-demand run of a SAVED sensor. Local sensors return <see cref="RemoteRunOutcome.Local"/>
    /// so the caller runs the existing in-process fast path; remote sensors are enqueued as a run job and,
    /// when <paramref name="wait"/> is set, awaited briefly for a synchronous-feeling result.
    /// </summary>
    private async Task<SavedSensorRunRouting> RouteSavedSensorRunAsync(
        Guid sensorId,
        bool wait,
        CancellationToken cancellationToken)
    {
        if (!_assignmentProvider.TryBuildProbeReadyRun(sensorId, out var owningProbeId, out var sensorTypeKey, out var target, out var settings)
            || owningProbeId is null)
        {
            return new SavedSensorRunRouting(RemoteRunOutcome.Local, null, string.Empty);
        }

        var probeName = _workspaceStore.FindProbeByProbeId(owningProbeId)?.Name ?? owningProbeId;
        var job = _onDemandRunStore.Create(
            owningProbeId,
            sensorId,
            sensorTypeKey,
            target,
            settings,
            recordObservation: true,
            ProbeRunJobKind.Sensor);

        if (!wait)
        {
            return new SavedSensorRunRouting(RemoteRunOutcome.Queued, null, probeName);
        }

        var completed = await _onDemandRunStore.WaitForCompletionAsync(job.Id, TimeSpan.FromSeconds(12), cancellationToken);
        return completed?.Result is { } result
            ? new SavedSensorRunRouting(RemoteRunOutcome.Completed, result, probeName)
            : new SavedSensorRunRouting(RemoteRunOutcome.Queued, null, probeName);
    }

    /// <summary>Message for a run that was queued on a remote probe (result lands on the next ~5s sync).</summary>
    private static string BuildQueuedOnProbeMessage(string sensorName, string probeName) =>
        $"Queued '{sensorName}' on probe '{probeName}' - the result will appear shortly (the probe syncs every ~5s).";
}
