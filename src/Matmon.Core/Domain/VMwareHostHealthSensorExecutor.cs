namespace Matmon.Core.Domain;

/// <summary>
/// Per-ESXi-host VMware health via the vSphere SOAP API: CPU and memory usage plus the VM
/// online/offline counts on that host. A <c>vmware.host</c> name selects the host; otherwise the
/// first visible host is used. Reuses <see cref="VMwareHealthSensorExecutor"/>'s SOAP session.
/// </summary>
public sealed class VMwareHostHealthSensorExecutor : ISensorExecutor
{
    public static SensorDefinition Definition { get; } = new SensorDefinition
    {
        Key = "vmware-host-health",
        DisplayName = "VMware Host Health",
        Description = "Per-ESXi-host vSphere health via the SOAP API: CPU and memory usage plus VM online/offline counts. Uses the first visible host when none is given.",
        ChannelMode = SensorChannelMode.Fixed,
        Parameters = BuildParameters()
    };

    public string SensorTypeKey => Definition.Key;

    private static IReadOnlyList<SensorParameterDefinition> BuildParameters()
    {
        var hostParameter = new SensorParameterDefinition
        {
            Key = "vmware.host",
            Label = "Host",
            Kind = SensorParameterKind.Text,
            Description = "ESXi host name to monitor. Leave empty to use the first visible host.",
            Placeholder = "esxi-01"
        };

        return new[] { hostParameter }.Concat(VMwareHealthSensorExecutor.BuildCommonParameters()).ToArray();
    }

    public ValueTask<SensorDiscoveryCheckResult> DiscoverAsync(
        SensorDiscoveryContext context,
        CancellationToken cancellationToken = default)
    {
        _ = context;
        _ = cancellationToken;
        return ValueTask.FromResult(SensorDiscoveryCheckResult.NotAvailable);
    }

    public async ValueTask<SensorExecutionResult> ExecuteAsync(
        SensorExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (!VMwareHealthSensorExecutor.TryResolveConnection(context, out var connection, out var error))
        {
            return SensorExecutionResult.Critical(TimeSpan.Zero, error);
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await using var session = await VSphereSession.ConnectAsync(connection, cancellationToken);

            var hosts = await session.QueryAsync("HostSystem",
            [
                "name",
                "runtime.connectionState",
                "runtime.powerState",
                "summary.quickStats.overallCpuUsage",
                "summary.quickStats.overallMemoryUsage",
                "summary.hardware.cpuMhz",
                "summary.hardware.numCpuCores",
                "summary.hardware.memorySize"
            ], cancellationToken);

            if (hosts.Count == 0)
            {
                return SensorExecutionResult.Critical(watch.Elapsed, "no hosts visible");
            }

            MonitoringSettings.TryReadParameter(context.Settings, "vmware.host", out var configuredHost);
            var host = !string.IsNullOrWhiteSpace(configuredHost)
                ? hosts.FirstOrDefault(candidate => string.Equals(candidate.Get("name"), configuredHost.Trim(), StringComparison.OrdinalIgnoreCase)) ?? hosts[0]
                : hosts[0];
            var hostName = host.Get("name") ?? "host";

            var vms = await session.QueryAsync("VirtualMachine",
                ["runtime.powerState", "runtime.host"], cancellationToken);
            var onHost = vms.Where(vm => string.Equals(vm.Get("runtime.host"), host.MoRef, StringComparison.OrdinalIgnoreCase)).ToArray();
            var vmOnline = onHost.Count(vm => string.Equals(vm.Get("runtime.powerState"), "poweredOn", StringComparison.OrdinalIgnoreCase));
            var vmOffline = onHost.Length - vmOnline;

            var cpuUsageMhz = host.GetDouble("summary.quickStats.overallCpuUsage") ?? 0;
            var cpuMhzPerCore = host.GetDouble("summary.hardware.cpuMhz") ?? 0;
            var cpuCores = host.GetDouble("summary.hardware.numCpuCores") ?? 0;
            var totalCpuMhz = cpuMhzPerCore * cpuCores;
            var cpuPercent = totalCpuMhz > 0 ? Math.Clamp(cpuUsageMhz / totalCpuMhz * 100.0, 0, 100) : 0;

            var memUsageBytes = (host.GetDouble("summary.quickStats.overallMemoryUsage") ?? 0) * 1024 * 1024; // MB -> bytes
            var memTotalBytes = host.GetDouble("summary.hardware.memorySize") ?? 0;
            var memPercent = memTotalBytes > 0 ? Math.Clamp(memUsageBytes / memTotalBytes * 100.0, 0, 100) : 0;

            var connected = string.Equals(host.Get("runtime.connectionState"), "connected", StringComparison.OrdinalIgnoreCase);

            var channels = new List<SensorChannelValue>
            {
                new() { Key = "cpu", Label = "CPU", Value = Math.Round(cpuPercent, 1), Unit = "%", MeasurementKind = SensorMeasurementKind.Percent, IsDefault = true },
                new() { Key = "memory", Label = "Memory", Value = Math.Round(memPercent, 1), Unit = "%", MeasurementKind = SensorMeasurementKind.Percent },
                new() { Key = "vmOnline", Label = "VMs online", Value = vmOnline, MeasurementKind = SensorMeasurementKind.Count },
                new() { Key = "vmOffline", Label = "VMs offline", Value = vmOffline, MeasurementKind = SensorMeasurementKind.Count },
                new() { Key = "connected", Label = "Connected", Value = connected ? 1 : 0, MeasurementKind = SensorMeasurementKind.Boolean, LogByDefault = false }
            };

            var state = connected ? SensorState.Healthy : SensorState.Critical;
            var message = connected
                ? $"{hostName} - CPU {cpuPercent:0.#}%, memory {memPercent:0.#}%, {vmOnline}/{onHost.Length} VMs running"
                : $"{hostName} is {host.Get("runtime.connectionState") ?? "disconnected"}";

            var result = connected
                ? SensorExecutionResult.Healthy(watch.Elapsed, message, Math.Round(cpuPercent, 1), "cpu", channels)
                : SensorExecutionResult.Critical(watch.Elapsed, message, Math.Round(cpuPercent, 1), "cpu", channels);

            return SensorThresholdEvaluator.ApplyChannelThresholds(context.Settings, result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return SensorExecutionResult.Unknown("execution cancelled");
        }
        catch (Exception ex)
        {
            return SensorExecutionResult.Critical(watch.Elapsed, ex.Message);
        }
    }
}
