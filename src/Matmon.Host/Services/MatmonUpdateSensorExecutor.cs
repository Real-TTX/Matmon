using System.Diagnostics;
using Matmon.Core.Domain;

namespace Matmon.Host.Services;

/// <summary>
/// Reports whether a newer Matmon build is available. The signal comes from Matmon.Cloud, which on every
/// heartbeat compares the latest released version (its executor sidecar shares the instance image) against the
/// version this instance reports and returns the verdict; <see cref="CloudConnectionService"/> caches it in
/// <see cref="CloudUpdateState"/>. This sensor surfaces that in the monitoring tree so "update available" can
/// flow into the normal alert/notification pipeline (e.g. e-mail me when a new build is out) - not just the
/// sidebar badge. Warning when an update is out, Healthy when up to date. It is meaningful on a cloud-linked
/// Primary; with no link (or before the first heartbeat) it simply reports "up to date / unknown".
/// </summary>
public sealed class MatmonUpdateSensorExecutor : ISensorExecutor
{
    public static SensorDefinition Definition { get; } = new()
    {
        Key = "matmon-update",
        DisplayName = "Matmon Update",
        Description = "Warns when a newer Matmon build is available (as reported by Matmon.Cloud on heartbeat).",
        ChannelMode = SensorChannelMode.Fixed,
        Parameters = []
    };

    private readonly CloudUpdateState _updateState;

    public MatmonUpdateSensorExecutor(CloudUpdateState updateState)
    {
        _updateState = updateState;
    }

    public string SensorTypeKey => Definition.Key;

    public ValueTask<SensorExecutionResult> ExecuteAsync(
        SensorExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        var watch = Stopwatch.StartNew();
        var current = MatmonVersion.Current;
        var latest = _updateState.LatestVersion;
        var updateAvailable = _updateState.UpdateAvailable;

        var channels = new List<SensorChannelValue>
        {
            new()
            {
                Key = "updateAvailable",
                Label = "Update available",
                Value = updateAvailable ? 1 : 0,
                IsDefault = true,
                State = updateAvailable ? SensorState.Warning : SensorState.Healthy,
                Message = updateAvailable ? "update available" : "up to date",
                // A steady 0/1 flag would just spam the statistics; keep the history off by default.
                LogByDefault = false
            }
        };
        watch.Stop();

        var message = updateAvailable
            ? $"Update available: {latest ?? "newer build"} (running {current})"
            : latest is { } known
                ? $"Up to date ({current}; latest {known})"
                : $"Up to date ({current})";

        var result = updateAvailable
            ? SensorExecutionResult.Warning(watch.Elapsed, message, 1, "updateAvailable", channels)
            : SensorExecutionResult.Healthy(watch.Elapsed, message, 0, "updateAvailable", channels);

        return ValueTask.FromResult(SensorThresholdEvaluator.ApplyChannelThresholds(context.Settings, result));
    }
}
