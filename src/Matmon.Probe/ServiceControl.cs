using System.Diagnostics;
using System.Runtime.Versioning;
using System.ServiceProcess;
using Microsoft.Extensions.Hosting.Systemd;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Win32;

namespace Matmon.Probe;

/// <summary>
/// The only platform-specific part of the auto-update: which service this process is, how to stop and start
/// it, and how to launch the updater so that stopping the service does not take the updater with it.
/// </summary>
public static class ServiceControl
{
    /// <summary>
    /// The auto-update only runs under a service manager - an interactive agent (a terminal, a debugger) has
    /// nothing that would start the new build after the swap.
    /// </summary>
    public static bool IsRunningAsService() =>
        (OperatingSystem.IsWindows() && WindowsServiceHelpers.IsWindowsService()) ||
        (OperatingSystem.IsLinux() && SystemdHelpers.IsSystemdService()) ||
        (OperatingSystem.IsMacOS() && LaunchdLabel() is not null);

    /// <summary>The variable the agent's launchd plist sets to its job label (see the agent's AgentServiceFiles).</summary>
    public const string LaunchdLabelVariable = "MATMON_LAUNCHD_LABEL";

    /// <summary>The launchd job this process runs as, or null outside launchd. From our own variable in the plist - a
    /// shell can inherit XPC_SERVICE_NAME from the terminal app's job, which says nothing about this process.</summary>
    public static string? LaunchdLabel()
    {
        var label = Environment.GetEnvironmentVariable(LaunchdLabelVariable)?.Trim();
        return string.IsNullOrEmpty(label) || label.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')) ? null : label;
    }

    /// <summary>Where a LaunchDaemon's plist lives - needed to load the job again after it was stopped.</summary>
    public static string LaunchdPlistPath(string label) => $"/Library/LaunchDaemons/{label}.plist";

    /// <summary>
    /// The name the service was INSTALLED under - not a constant, because an admin may have registered it by
    /// hand under any name. Windows: the service whose image path is this executable; Linux: the unit whose
    /// cgroup this process runs in.
    /// </summary>
    public static string? ResolveServiceName()
    {
        if (OperatingSystem.IsWindows())
        {
            return ResolveWindowsServiceName(Environment.ProcessPath);
        }

        if (OperatingSystem.IsMacOS())
        {
            return LaunchdLabel();
        }

        if (OperatingSystem.IsLinux())
        {
            try
            {
                // "0::/system.slice/matmon-agent.service"
                return File.ReadAllLines("/proc/self/cgroup")
                    .Select(line => line.Split('/').LastOrDefault())
                    .FirstOrDefault(segment => segment?.EndsWith(".service", StringComparison.Ordinal) == true);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    public static void Stop(string serviceName, TimeSpan timeout)
    {
        if (OperatingSystem.IsWindows())
        {
            using var controller = new ServiceController(serviceName);
            if (controller.Status != ServiceControllerStatus.Stopped)
            {
                if (controller.Status != ServiceControllerStatus.StopPending)
                {
                    controller.Stop();
                }

                controller.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
            }

            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            // bootout = stop AND unload: with KeepAlive, a plain kill would only make launchd start the old build again.
            // Not loaded (already stopped) is fine.
            Launchctl(timeout, allowFailure: true, "bootout", $"system/{serviceName}");
            return;
        }

        Systemctl("stop", serviceName, timeout);
    }

    public static void Start(string serviceName, TimeSpan timeout)
    {
        if (OperatingSystem.IsWindows())
        {
            using var controller = new ServiceController(serviceName);
            if (controller.Status != ServiceControllerStatus.Running)
            {
                if (controller.Status != ServiceControllerStatus.StartPending)
                {
                    controller.Start();
                }

                controller.WaitForStatus(ServiceControllerStatus.Running, timeout);
            }

            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            // Loaded already ("service already loaded") -> kickstart starts it if it is not running.
            if (!Launchctl(timeout, allowFailure: true, "bootstrap", "system", LaunchdPlistPath(serviceName)))
            {
                Launchctl(timeout, allowFailure: false, "kickstart", $"system/{serviceName}");
            }

            return;
        }

        Systemctl("start", serviceName, timeout);
    }

    /// <summary>
    /// Starts the updater OUTSIDE this service's lifetime. On Windows a child process simply survives the
    /// service being stopped. Under systemd it would not: stopping a unit kills its whole cgroup, children
    /// included - so the updater runs as its own transient unit via systemd-run.
    /// </summary>
    public static void LaunchDetached(string executable, IReadOnlyList<string> arguments)
    {
        ProcessStartInfo start;
        if (OperatingSystem.IsLinux())
        {
            start = new ProcessStartInfo("systemd-run")
            {
                ArgumentList = { "--unit", $"matmon-update-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}", "--collect", "--quiet", executable }
            };
        }
        else
        {
            start = new ProcessStartInfo(executable);
        }

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("the updater did not start");

        if (OperatingSystem.IsLinux())
        {
            // systemd-run returns as soon as the transient unit exists; a non-zero exit means it does not.
            process.WaitForExit(TimeSpan.FromSeconds(30));
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"systemd-run exited with {process.ExitCode}");
            }
        }
    }

    [SupportedOSPlatform("windows")]
    public static string? ResolveWindowsServiceName(string? processPath)
    {
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return null;
        }

        using var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        if (services is null)
        {
            return null;
        }

        foreach (var name in services.GetSubKeyNames())
        {
            using var service = services.OpenSubKey(name);
            if (service?.GetValue("ImagePath") is not string imagePath)
            {
                continue;
            }

            // ImagePath is a command line: optionally quoted executable, then arguments.
            var trimmed = imagePath.Trim();
            var executable = trimmed.StartsWith('"')
                ? trimmed[1..Math.Max(1, trimmed.IndexOf('"', 1))]
                : trimmed.Split(' ', 2)[0];
            if (string.Equals(Environment.ExpandEnvironmentVariables(executable), processPath, StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>Runs launchctl; true when it exited with 0. Throws on failure unless <paramref name="allowFailure"/>.</summary>
    public static bool Launchctl(TimeSpan timeout, bool allowFailure, params string[] arguments)
    {
        var start = new ProcessStartInfo("launchctl") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("launchctl did not start");
        var ok = process.WaitForExit(timeout) && process.ExitCode == 0;
        if (!ok && !allowFailure)
        {
            throw new InvalidOperationException($"launchctl {string.Join(' ', arguments)} failed: {process.StandardError.ReadToEnd().Trim()}");
        }

        return ok;
    }

    private static void Systemctl(string verb, string unit, TimeSpan timeout)
    {
        using var process = Process.Start(new ProcessStartInfo("systemctl") { ArgumentList = { verb, unit }, UseShellExecute = false })
            ?? throw new InvalidOperationException("systemctl did not start");
        if (!process.WaitForExit(timeout) || process.ExitCode != 0)
        {
            throw new InvalidOperationException($"systemctl {verb} {unit} failed");
        }
    }
}
