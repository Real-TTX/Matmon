using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Matmon.Core.Domain;

/// <summary>
/// The machine the probe runs on, measured from the inside: CPU, memory, disks, uptime (and load on Linux).
///
/// Every other health sensor reaches its target from the outside - WinRM, SSH, SNMP, an API - and so needs
/// network access, a listener on the target and credentials. An AGENT already sits on the machine it
/// monitors, so for its own host none of that is needed: this reads the OS directly (Windows: GetSystemTimes /
/// GlobalMemoryStatusEx, Linux: /proc) and needs no target and no credential. Run by the primary it measures
/// the primary's own box (in Docker: the container's view of the host), by a Docker probe that container.
///
/// Channel keys match the channel families (cpu / memoryUsedPercent / diskUsedPercent), so a multi-graph
/// "CPU usage" compares agents directly with Windows-, Proxmox- and Synology-health sensors.
/// </summary>
public sealed class LocalHealthSensorExecutor : ISensorExecutor
{
    public static SensorDefinition Definition { get; } = new()
    {
        Key = "local-health",
        DisplayName = "System Health (local)",
        Description = "CPU, memory, disks and uptime of the machine this probe runs on - measured locally, no WinRM, SSH or credentials. On an agent that is the monitored machine itself.",
        ChannelMode = SensorChannelMode.Dynamic,
        Parameters = []
    };

    /// <summary>CPU is a rate: two readings this far apart. Long enough to not be one scheduler tick, short
    /// enough to keep a poll cheap.</summary>
    private static readonly TimeSpan CpuSampleInterval = TimeSpan.FromSeconds(1);

    /// <summary>Anything smaller is a pseudo or boot filesystem, not a disk anyone fills up.</summary>
    private const long MinimumDiskBytes = 1L << 30;

    public string SensorTypeKey => Definition.Key;

    public async ValueTask<SensorExecutionResult> ExecuteAsync(SensorExecutionContext context, CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var channels = new List<SensorChannelValue>();

            if (await ReadCpuPercentAsync(cancellationToken) is { } cpu)
            {
                channels.Add(Percent("cpu", "CPU", cpu, isDefault: true));
            }

            if (ReadMemory() is { } memory && memory.TotalBytes > 0)
            {
                var used = memory.TotalBytes - memory.AvailableBytes;
                channels.Add(Percent("memoryUsedPercent", "Memory used", used * 100d / memory.TotalBytes));
                channels.Add(Number("memoryUsedGb", "Memory used", Gigabytes(used), "GB"));
                channels.Add(Number("memoryTotalGb", "Memory total", Gigabytes(memory.TotalBytes), "GB"));
            }

            AddDisks(channels);

            if (ReadUptime() is { } uptime)
            {
                channels.Add(Number("uptimeHours", "Uptime", Math.Round(uptime.TotalHours, 1), "h"));
            }

            if (OperatingSystem.IsLinux() && ReadLinuxLoad() is { } load)
            {
                channels.Add(Number("load1", "Load (1 min)", load, null));
            }

            watch.Stop();
            if (channels.Count == 0)
            {
                return SensorExecutionResult.Critical(watch.Elapsed, $"No local readings on {RuntimeInformation.OSDescription}");
            }

            var cpuText = channels.FirstOrDefault(channel => channel.Key == "cpu")?.Value is { } c ? string.Create(CultureInfo.InvariantCulture, $"CPU {c:0.#}%") : "CPU n/a";
            var memoryText = channels.FirstOrDefault(channel => channel.Key == "memoryUsedPercent")?.Value is { } m ? string.Create(CultureInfo.InvariantCulture, $"memory {m:0.#}%") : "memory n/a";
            var diskText = channels.FirstOrDefault(channel => channel.Key == "diskUsedPercent")?.Value is { } d ? string.Create(CultureInfo.InvariantCulture, $"disk {d:0.#}%") : "disk n/a";
            var message = string.Create(CultureInfo.InvariantCulture, $"{Environment.MachineName}: {cpuText}, {memoryText}, {diskText}");
            var defaultValue = channels.FirstOrDefault(channel => channel.IsDefault)?.Value;

            var result = SensorExecutionResult.Healthy(watch.Elapsed, message, defaultValue, "cpu", channels);
            return SensorThresholdEvaluator.ApplyChannelThresholds(context.Settings, result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            watch.Stop();
            return SensorExecutionResult.Critical(watch.Elapsed, ex.Message);
        }
    }

    // ---- CPU ----------------------------------------------------------------------------------------------

    private static async Task<double?> ReadCpuPercentAsync(CancellationToken cancellationToken)
    {
        var first = ReadCpuTimes();
        if (first is null)
        {
            return null;
        }

        await Task.Delay(CpuSampleInterval, cancellationToken);
        var second = ReadCpuTimes();
        return second is null ? null : CpuPercent(first.Value, second.Value);
    }

    /// <summary>Busy share between two (idle, total) readings, 0..100. Null when time did not advance.</summary>
    public static double? CpuPercent((ulong Idle, ulong Total) before, (ulong Idle, ulong Total) after)
    {
        if (after.Total <= before.Total || after.Idle < before.Idle)
        {
            return null;
        }

        var total = after.Total - before.Total;
        var idle = Math.Min(after.Idle - before.Idle, total);
        return Math.Round((total - idle) * 100d / total, 1);
    }

    private static (ulong Idle, ulong Total)? ReadCpuTimes()
    {
        if (OperatingSystem.IsWindows())
        {
            return ReadWindowsCpuTimes();
        }

        if (OperatingSystem.IsLinux() && File.Exists("/proc/stat"))
        {
            return ParseProcStat(File.ReadLines("/proc/stat").FirstOrDefault());
        }

        return null;
    }

    /// <summary>
    /// The aggregate line of /proc/stat ("cpu user nice system idle iowait irq softirq steal ..."), as
    /// (idle, total) in jiffies. iowait counts as idle - the CPU was free, the disk was not. guest/guest_nice
    /// are already inside user/nice and are left out so they are not counted twice.
    /// </summary>
    public static (ulong Idle, ulong Total)? ParseProcStat(string? line)
    {
        if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("cpu ", StringComparison.Ordinal))
        {
            return null;
        }

        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Take(8)
            .Select(field => ulong.TryParse(field, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : (ulong?)null)
            .ToArray();
        if (fields.Length < 4 || fields.Any(field => field is null))
        {
            return null;
        }

        var idle = fields[3]!.Value + (fields.Length > 4 ? fields[4]!.Value : 0);
        var total = fields.Aggregate(0UL, (sum, field) => sum + field!.Value);
        return (idle, total);
    }

    [SupportedOSPlatform("windows")]
    private static (ulong Idle, ulong Total)? ReadWindowsCpuTimes()
    {
        // Kernel time INCLUDES idle time, so kernel + user is the total.
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return null;
        }

        return (idle, kernel + user);
    }

    // ---- Memory -------------------------------------------------------------------------------------------

    private static (ulong TotalBytes, ulong AvailableBytes)? ReadMemory()
    {
        if (OperatingSystem.IsWindows())
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            return GlobalMemoryStatusEx(ref status) ? (status.TotalPhys, status.AvailPhys) : null;
        }

        if (OperatingSystem.IsLinux() && File.Exists("/proc/meminfo"))
        {
            return ParseMemInfo(File.ReadAllText("/proc/meminfo"));
        }

        return null;
    }

    /// <summary>
    /// MemTotal and MemAvailable from /proc/meminfo, in bytes. MemAvailable, not MemFree: free leaves out the
    /// page cache the kernel hands back on demand, and a Linux box that has been up a day would always look
    /// "full" by it.
    /// </summary>
    public static (ulong TotalBytes, ulong AvailableBytes)? ParseMemInfo(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        ulong? total = null;
        ulong? available = null;
        foreach (var line in text.Split('\n'))
        {
            var parts = line.Split(':', 2);
            if (parts.Length != 2)
            {
                continue;
            }

            var kilobytes = parts[1].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!ulong.TryParse(kilobytes, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                continue;
            }

            switch (parts[0].Trim())
            {
                case "MemTotal":
                    total = value * 1024;
                    break;
                case "MemAvailable":
                    available = value * 1024;
                    break;
            }
        }

        return total is { } t && available is { } a ? (t, Math.Min(a, t)) : null;
    }

    // ---- Disks, uptime, load ------------------------------------------------------------------------------

    private static void AddDisks(List<SensorChannelValue> channels)
    {
        var systemRoot = OperatingSystem.IsWindows()
            ? Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System)) ?? "C:\\"
            : "/";

        var disks = DriveInfo.GetDrives()
            .Where(drive => SafeReady(drive) && drive.DriveType == DriveType.Fixed && drive.TotalSize >= MinimumDiskBytes)
            .GroupBy(drive => drive.RootDirectory.FullName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(drive => drive.RootDirectory.FullName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var drive in disks)
        {
            var used = drive.TotalSize - drive.AvailableFreeSpace;
            var usedPercent = Math.Round(used * 100d / drive.TotalSize, 1);
            var name = DiskKey(drive.RootDirectory.FullName);
            var label = drive.RootDirectory.FullName.TrimEnd('\\');

            // The system disk doubles as the headline channel - one per machine, so the "Disk usage" family
            // and the default thresholds have one clear signal.
            if (string.Equals(drive.RootDirectory.FullName, systemRoot, StringComparison.OrdinalIgnoreCase))
            {
                channels.Add(Percent("diskUsedPercent", $"System disk ({label}) used", usedPercent));
            }

            channels.Add(Percent($"disk.{name}.usedPercent", $"Disk {label} used", usedPercent));
            channels.Add(Number($"disk.{name}.freeGb", $"Disk {label} free", Gigabytes((ulong)drive.AvailableFreeSpace), "GB"));
        }
    }

    /// <summary>"C:\" → "c", "/" → "root", "/var/lib/docker" → "var_lib_docker" - a channel key must not carry
    /// separators the rest of the pipeline treats as structure.</summary>
    public static string DiskKey(string root)
    {
        var trimmed = root.Trim().TrimEnd('\\', '/').TrimEnd(':');
        if (trimmed.Length == 0)
        {
            return "root";
        }

        var key = new string(trimmed.Select(character => char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '_').ToArray()).Trim('_');
        return key.Length == 0 ? "root" : key;
    }

    private static bool SafeReady(DriveInfo drive)
    {
        try
        {
            return drive.IsReady;
        }
        catch
        {
            return false;
        }
    }

    private static TimeSpan? ReadUptime()
    {
        if (OperatingSystem.IsLinux() && File.Exists("/proc/uptime"))
        {
            var first = File.ReadAllText("/proc/uptime").Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? TimeSpan.FromSeconds(seconds) : null;
        }

        // Windows: milliseconds since boot.
        return TimeSpan.FromMilliseconds(Environment.TickCount64);
    }

    private static double? ReadLinuxLoad()
    {
        if (!File.Exists("/proc/loadavg"))
        {
            return null;
        }

        var first = File.ReadAllText("/proc/loadavg").Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var load) ? load : null;
    }

    // ---- helpers ------------------------------------------------------------------------------------------

    private static SensorChannelValue Percent(string key, string label, double value, bool isDefault = false) => new()
    {
        Key = key,
        Label = label,
        Value = Math.Round(value, 1),
        Unit = "%",
        MeasurementKind = SensorMeasurementKind.Percent,
        IsDefault = isDefault
    };

    private static SensorChannelValue Number(string key, string label, double value, string? unit) => new()
    {
        Key = key,
        Label = label,
        Value = value,
        Unit = unit
    };

    private static double Gigabytes(ulong bytes) => Math.Round(bytes / 1024d / 1024d / 1024d, 2);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idleTime, out ulong kernelTime, out ulong userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }
}
