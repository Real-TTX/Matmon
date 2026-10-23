using System.Globalization;
using System.Text;

namespace Matmon.Core.Domain;

/// <summary>
/// A Hyper-V host: how many VMs run, which ones are in a critical state, have lost their heartbeat or whose replication
/// is unhealthy, each VM's CPU / memory / uptime, and the host's memory. A wrapper over the PowerShell engine
/// (<see cref="PowerShellRemoteSensorExecutor"/>) running a fixed script - so it works over WinRM and, on an agent that
/// sits on the Hyper-V host itself, locally with no credential. The account needs to be in "Hyper-V Administrators".
/// <para>Critical: a VM in a *Critical state (its storage is gone), replication Critical. Warning: a running VM whose
/// heartbeat is lost (the guest hangs), replication Warning. A VM that is simply off is not a problem - plenty are
/// meant to be.</para>
/// </summary>
public sealed class HyperVSensorExecutor : ISensorExecutor
{
    private static readonly PowerShellRemoteSensorExecutor Inner = new();

    private const string Script = """
$ErrorActionPreference = 'Stop'
$out = [ordered]@{}
try {
    $vms = @(Get-VM)
    $running = @($vms | Where-Object { "$($_.State)" -eq 'Running' })
    $critical = @($vms | Where-Object { "$($_.State)" -like '*Critical*' })
    $lost = @($running | Where-Object { "$($_.Heartbeat)" -match 'Lost|NoContact|Error' })
    $replicated = @($vms | Where-Object { "$($_.ReplicationState)" -ne 'Disabled' -and "$($_.ReplicationHealth)" -ne 'NotApplicable' })
    $out['vmTotal'] = $vms.Count
    $out['vmRunning'] = $running.Count
    $out['vmOff'] = @($vms | Where-Object { "$($_.State)" -eq 'Off' }).Count
    $out['vmSavedOrPaused'] = @($vms | Where-Object { "$($_.State)" -in 'Saved', 'Paused' }).Count
    $out['vmCritical'] = $critical.Count
    $out['heartbeatLost'] = $lost.Count
    $out['replicationWarning'] = @($replicated | Where-Object { "$($_.ReplicationHealth)" -eq 'Warning' }).Count
    $out['replicationCritical'] = @($replicated | Where-Object { "$($_.ReplicationHealth)" -eq 'Critical' }).Count
    $os = Get-CimInstance Win32_OperatingSystem
    if ($os.TotalVisibleMemorySize -gt 0) {
        $out['hostMemoryUsedPercent'] = [math]::Round((1 - $os.FreePhysicalMemory / $os.TotalVisibleMemorySize) * 100, 1)
    }
    $vm = [ordered]@{}
    foreach ($v in $vms) {
        $name = ($v.Name -replace '[^A-Za-z0-9_-]', '-')
        $problem = ("$($v.State)" -like '*Critical*') -or ($lost -contains $v) -or ("$($v.ReplicationHealth)" -in 'Warning', 'Critical')
        $vm[$name] = [ordered]@{
            up = [int]("$($v.State)" -eq 'Running')
            problem = [int]$problem
            cpu = [int]$v.CPUUsage
            memoryGb = [math]::Round($v.MemoryAssigned / 1GB, 1)
            uptimeHours = [math]::Round($v.Uptime.TotalHours, 1)
        }
    }
    $out['vm'] = $vm
    $out['state'] = if ($critical.Count -gt 0 -or $out['replicationCritical'] -gt 0) { 'critical' }
        elseif ($lost.Count -gt 0 -or $out['replicationWarning'] -gt 0) { 'warning' } else { 'ok' }
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    $out['vmTotal'] = -1
    $out['state'] = 'critical'
}
[pscustomobject]$out
""";

    public static SensorDefinition Definition { get; } = new()
    {
        Key = "hyperv-health",
        DisplayName = "Hyper-V Health",
        Description = "A Hyper-V host over WinRM (or locally on an agent): VMs running / off / critical, lost heartbeats, replication health, per-VM CPU, memory and uptime, host memory. Needs a member of 'Hyper-V Administrators'.",
        ChannelMode = SensorChannelMode.Dynamic,
        Parameters = PowerShellRemoteSensorExecutor.Definition.Parameters
            .Where(parameter => parameter.Key.StartsWith("winrm.", StringComparison.OrdinalIgnoreCase))
            .ToArray()
    };

    public string SensorTypeKey => Definition.Key;

    public async ValueTask<SensorExecutionResult> ExecuteAsync(SensorExecutionContext context, CancellationToken cancellationToken = default)
    {
        var settings = context.Settings.Clone();
        settings.Parameters["script"] = Script;
        settings.Parameters["outputFormat"] = "json";
        // vmTotal is the one channel the script writes on both paths (the engine insists on finding the default channel).
        settings.Parameters["defaultChannelKey"] = "vmtotal";
        var result = await Inner.ExecuteAsync(new SensorExecutionContext(PowerShellRemoteSensorExecutor.Definition.Key, context.Target, settings), cancellationToken);
        return Describe(result);
    }

    /// <summary>Gives the engine's flat channels names and the result a message that says which VMs are in trouble.</summary>
    public static SensorExecutionResult Describe(SensorExecutionResult result)
    {
        double? Value(string key) => result.Channels.FirstOrDefault(channel => channel.Key == key)?.Value;
        if (Value("vmtotal") is not { } total || total < 0)
        {
            // The engine's own message carries the error (Get-VM missing, access denied, WinRM down); the -1 placeholder is no reading.
            // With the placeholder present the script itself ran and Get-VM failed - say what usually fixes that.
            return result.Channels.Count == 0 || result.State != SensorState.Critical
                ? result
                : SensorExecutionResult.Critical(result.Duration,
                    $"{result.Message?.Trim().TrimEnd('.')} - the account needs to be a member of 'Hyper-V Administrators' on the host, and the Hyper-V PowerShell module installed.");
        }

        // The engine lower-cases keys; its label ("Vm SQL 01 up") still has the VM name as written.
        var displayNames = result.Channels
            .Where(channel => channel.Key.StartsWith("vm.", StringComparison.Ordinal) && channel.Key.EndsWith(".up", StringComparison.Ordinal))
            .ToDictionary(
                channel => channel.Key[3..^3],
                channel => channel.Label.StartsWith("Vm ", StringComparison.Ordinal) && channel.Label.EndsWith(" up", StringComparison.Ordinal) && channel.Label.Length > 6
                    ? channel.Label[3..^3]
                    : channel.Key[3..^3],
                StringComparer.Ordinal);
        var vmNames = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var labelled = result.Channels.Select(channel =>
        {
            if (!channel.Key.StartsWith("vm.", StringComparison.Ordinal) || channel.Key.LastIndexOf('.') <= 3)
            {
                return channel.Key == "hostmemoryusedpercent"
                    ? channel with { Label = "Host memory used", Unit = "%", MeasurementKind = SensorMeasurementKind.Percent }
                    : channel with { Label = FixedLabel(channel.Key) ?? channel.Label, MeasurementKind = SensorMeasurementKind.Count, LogByDefault = channel.Key is "vmrunning" };
            }

            var key = channel.Key[3..channel.Key.LastIndexOf('.')];
            var name = displayNames.TryGetValue(key, out var shown) ? shown : key;
            var field = channel.Key[(channel.Key.LastIndexOf('.') + 1)..];
            vmNames.Add(key);
            return field switch
            {
                "up" => channel with { Label = $"{name} running", MeasurementKind = SensorMeasurementKind.Boolean, LogByDefault = false },
                "problem" => channel with { Label = $"{name} problem", MeasurementKind = SensorMeasurementKind.Boolean, LogByDefault = false },
                "cpu" => channel with { Label = $"{name} CPU", Unit = "%", MeasurementKind = SensorMeasurementKind.Percent },
                "memorygb" => channel with { Label = $"{name} memory", Unit = "GB" },
                "uptimehours" => channel with { Label = $"{name} uptime", Unit = "h", LogByDefault = false },
                _ => channel
            };
        }).ToList();

        var troubled = vmNames.Where(key => Value($"vm.{key}.problem") is > 0).Select(key => displayNames.TryGetValue(key, out var shown) ? shown : key).ToList();
        var builder = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $"{total:0} VM{(total == 1 ? string.Empty : "s")}: {Value("vmrunning") ?? 0:0} running, {Value("vmoff") ?? 0:0} off"));
        if (Value("vmsavedorpaused") is > 0)
        {
            builder.Append(CultureInfo.InvariantCulture, $", {Value("vmsavedorpaused"):0} saved or paused");
        }

        var problems = new List<string>();
        if (Value("vmcritical") is > 0)
        {
            problems.Add($"{Value("vmcritical"):0} in a critical state");
        }

        if (Value("heartbeatlost") is > 0)
        {
            problems.Add($"{Value("heartbeatlost"):0} without heartbeat");
        }

        if ((Value("replicationcritical") ?? 0) + (Value("replicationwarning") ?? 0) > 0)
        {
            problems.Add($"replication unhealthy on {(Value("replicationcritical") ?? 0) + (Value("replicationwarning") ?? 0):0}");
        }

        if (problems.Count > 0)
        {
            builder.Append(" - ").Append(string.Join(", ", problems));
            if (troubled.Count > 0)
            {
                builder.Append(" (").Append(string.Join(", ", troubled.Take(5))).Append(troubled.Count > 5 ? ", …)" : ")");
            }
        }

        if (Value("hostmemoryusedpercent") is { } memory)
        {
            builder.Append(CultureInfo.InvariantCulture, $"; host memory {memory:0} % used");
        }

        labelled = labelled.Select(channel => channel with { IsDefault = channel.Key == "vmrunning" }).ToList();
        return new SensorExecutionResult(result.State, result.Duration, Value("vmrunning"), builder.ToString())
        {
            DefaultChannelKey = "vmrunning",
            Channels = labelled
        };
    }

    private static string? FixedLabel(string key) => key switch
    {
        "vmtotal" => "VMs",
        "vmrunning" => "VMs running",
        "vmoff" => "VMs off",
        "vmsavedorpaused" => "VMs saved or paused",
        "vmcritical" => "VMs critical",
        "heartbeatlost" => "Heartbeat lost",
        "replicationwarning" => "Replication warning",
        "replicationcritical" => "Replication critical",
        _ => null
    };
}
