using System.Diagnostics;
using Matmon.Core;
using Matmon.Core.Domain;

namespace Matmon.Host.Services;

public sealed class ProbeHealthSensorExecutor : ISensorExecutor
{
    public static SensorDefinition Definition { get; } = new()
    {
        Key = "probe-health",
        DisplayName = "Probe Health",
        Description = "Checks probe connectivity and local storage headroom.",
        ChannelMode = SensorChannelMode.Fixed,
        Parameters =
        [
            new SensorParameterDefinition
            {
                Key = "storage.warningFreePercent",
                Label = "Storage warning free %",
                Kind = SensorParameterKind.Integer,
                DefaultValue = "15",
                Min = 1,
                Max = 100
            },
            new SensorParameterDefinition
            {
                Key = "storage.criticalFreePercent",
                Label = "Storage error free %",
                Kind = SensorParameterKind.Integer,
                DefaultValue = "8",
                Min = 1,
                Max = 100
            },
            new SensorParameterDefinition
            {
                Key = "connection.criticalWhenDisconnected",
                Label = "Error when slave is disconnected",
                Kind = SensorParameterKind.Boolean,
                DefaultValue = "true"
            }
        ]
    };

    private readonly MatmonRuntimeOptions _runtimeOptions;
    private readonly SlaveProbeRuntimeState _slaveRuntimeState;
    private readonly StorageOverviewProvider _storageOverviewProvider;

    public ProbeHealthSensorExecutor(
        MatmonRuntimeOptions runtimeOptions,
        SlaveProbeRuntimeState slaveRuntimeState,
        StorageOverviewProvider storageOverviewProvider)
    {
        _runtimeOptions = runtimeOptions;
        _slaveRuntimeState = slaveRuntimeState;
        _storageOverviewProvider = storageOverviewProvider;
    }

    public string SensorTypeKey => Definition.Key;

    public ValueTask<SensorExecutionResult> ExecuteAsync(
        SensorExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        var watch = Stopwatch.StartNew();
        try
        {
            var storage = _storageOverviewProvider.GetOverview();
            var slaveRuntime = _slaveRuntimeState.Snapshot();
            var isMaster = _runtimeOptions.Mode == AppMode.Master;
            var connected = isMaster || slaveRuntime.IsConnected;
            var warningFreePercent = ReadPercentParameter(context.Settings, "storage.warningFreePercent", 15);
            var criticalFreePercent = ReadPercentParameter(context.Settings, "storage.criticalFreePercent", 8);
            var criticalWhenDisconnected = !MonitoringSettings.TryReadParameterBool(
                    context.Settings,
                    "connection.criticalWhenDisconnected",
                    out var configuredCriticalWhenDisconnected) ||
                configuredCriticalWhenDisconnected;

            var channels = BuildChannels(
                storage,
                connected,
                isMaster,
                criticalWhenDisconnected,
                warningFreePercent,
                criticalFreePercent);
            var state = channels
                .Select(channel => channel.State ?? SensorState.Healthy)
                .Aggregate(SensorState.Healthy, MaxState);

            if (!string.IsNullOrWhiteSpace(storage.ErrorMessage) && state == SensorState.Healthy)
            {
                state = SensorState.Warning;
            }

            var defaultChannel = storage.DriveFreePercent.HasValue ? "storageFreePercent" : "dataUsedMb";
            var defaultValue = channels.FirstOrDefault(channel => channel.Key == defaultChannel)?.Value;
            var message = BuildMessage(storage, isMaster, connected, slaveRuntime, state);

            watch.Stop();

            var result = state switch
            {
                SensorState.Critical => SensorExecutionResult.Critical(watch.Elapsed, message, defaultValue, defaultChannel, channels),
                SensorState.Warning => SensorExecutionResult.Warning(watch.Elapsed, message, defaultValue, defaultChannel, channels),
                _ => SensorExecutionResult.Healthy(watch.Elapsed, message, defaultValue, defaultChannel, channels)
            };

            return ValueTask.FromResult(SensorThresholdEvaluator.ApplyChannelThresholds(context.Settings, result));
        }
        catch (Exception ex)
        {
            watch.Stop();
            return ValueTask.FromResult(SensorExecutionResult.Critical(watch.Elapsed, ex.Message));
        }
    }

    private static IReadOnlyList<SensorChannelValue> BuildChannels(
        StorageOverview storage,
        bool connected,
        bool isMaster,
        bool criticalWhenDisconnected,
        int warningFreePercent,
        int criticalFreePercent)
    {
        var channels = new List<SensorChannelValue>
        {
            new()
            {
                Key = "connected",
                Label = isMaster ? "Local mode" : "Master connection",
                Value = connected ? 1 : 0,
                State = connected
                    ? SensorState.Healthy
                    : criticalWhenDisconnected ? SensorState.Critical : SensorState.Warning,
                Message = connected ? "ok" : "disconnected"
            },
            new()
            {
                Key = "dataPathAvailable",
                Label = "Data path",
                Value = storage.DataDirectoryExists ? 1 : 0,
                State = storage.DataDirectoryExists ? SensorState.Healthy : SensorState.Critical,
                Message = storage.DataDirectoryExists ? "available" : "missing"
            },
            new()
            {
                Key = "dataUsedMb",
                Label = "Data used",
                Value = storage.DataDirectoryMegabytes,
                Unit = "MB",
                IsDefault = !storage.DriveFreePercent.HasValue
            },
            new()
            {
                Key = "dataFileCount",
                Label = "Files",
                Value = storage.DataFileCount
            }
        };

        if (storage.DriveFreePercent.HasValue)
        {
            var freePercent = storage.DriveFreePercent.Value;
            channels.Add(new SensorChannelValue
            {
                Key = "storageFreePercent",
                Label = "Storage free",
                Value = freePercent,
                Unit = "%",
                IsDefault = true,
                State = ResolveStorageState(freePercent, warningFreePercent, criticalFreePercent),
                Message = $"free {freePercent:0.##}%"
            });
        }

        if (storage.DriveAvailableGigabytes.HasValue)
        {
            channels.Add(new SensorChannelValue
            {
                Key = "driveFreeGb",
                Label = "Drive free",
                Value = storage.DriveAvailableGigabytes.Value,
                Unit = "GB"
            });
        }

        return channels;
    }

    private static SensorState ResolveStorageState(
        double freePercent,
        int warningFreePercent,
        int criticalFreePercent)
    {
        if (freePercent <= criticalFreePercent)
        {
            return SensorState.Critical;
        }

        if (freePercent <= warningFreePercent)
        {
            return SensorState.Warning;
        }

        return SensorState.Healthy;
    }

    private static string BuildMessage(
        StorageOverview storage,
        bool isMaster,
        bool connected,
        SlaveProbeRuntimeSnapshot slaveRuntime,
        SensorState state)
    {
        var modeMessage = isMaster
            ? "local master"
            : connected ? "master connected" : $"master disconnected: {slaveRuntime.StatusMessage}";
        var storageMessage = storage.DriveFreePercent.HasValue
            ? $"storage free {storage.DriveFreePercent.Value:0.##}%"
            : $"data {storage.DataDirectoryMegabytes:0.##} MB";

        if (!string.IsNullOrWhiteSpace(storage.ErrorMessage))
        {
            storageMessage = $"{storageMessage}, {storage.ErrorMessage}";
        }

        return state == SensorState.Healthy
            ? $"{modeMessage}, {storageMessage}"
            : $"{MonitoringStatePresentation.Label(state)} - {modeMessage}, {storageMessage}";
    }

    private static int ReadPercentParameter(MonitoringSettings settings, string key, int fallback)
    {
        if (!MonitoringSettings.TryReadParameterInt(settings, key, out var value))
        {
            return fallback;
        }

        return Math.Clamp(value, 1, 100);
    }

    private static SensorState MaxState(SensorState left, SensorState right)
    {
        if (left == SensorState.Critical || right == SensorState.Critical)
        {
            return SensorState.Critical;
        }

        if (left == SensorState.Warning || right == SensorState.Warning)
        {
            return SensorState.Warning;
        }

        if (left == SensorState.Disabled || right == SensorState.Disabled)
        {
            return SensorState.Disabled;
        }

        if (left == SensorState.Paused || right == SensorState.Paused)
        {
            return SensorState.Paused;
        }

        return left == SensorState.Unknown ? right : left;
    }
}
