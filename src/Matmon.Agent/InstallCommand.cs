using System.Diagnostics;
using Matmon.Probe;

namespace Matmon.Agent;

/// <summary>
/// <c>matmon-agent install [--url … --code …] [--force]</c> - the one command that turns a downloaded agent into a
/// running service, on every platform, as administrator / root:
/// <list type="bullet">
/// <item>Windows: the elevated <see cref="SetupCommand"/> (service, tray, autostart).</item>
/// <item>Linux: enrol, copy to <c>/usr/local/bin/matmon-agent</c>, write the systemd unit, enable + (re)start it.</item>
/// <item>macOS: enrol, copy to <c>/usr/local/bin/matmon-agent</c>, write the LaunchDaemon plist, (re)load it.</item>
/// </list>
/// Without <c>--url</c>/<c>--code</c> it uses what a "ready to install" download carries (<see cref="AgentInstallerPayload"/>),
/// so <c>sudo ./matmon-agent install</c> is the whole install. Enrolment comes FIRST everywhere: a wrong or used code must
/// not leave a half-installed service behind. The installed copy never carries the code.
/// </summary>
public static class InstallCommand
{
    public static async Task<int> RunAsync(string[] args, string configPath)
    {
        var embedded = AgentInstallerPayload.ReadFile(Environment.ProcessPath);
        var url = EnrollCommand.ReadOption(args, "--url") ?? embedded?.Url;
        var code = EnrollCommand.ReadOption(args, "--code") ?? embedded?.Code;
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(code))
        {
            Console.Error.WriteLine("Usage: matmon-agent install --url <instance url> --code <enrolment code> [--force]");
            Console.Error.WriteLine("Or download the agent from the Agents page of your instance - that download carries both.");
            return 2;
        }

        if (OperatingSystem.IsWindows())
        {
            // The same path the tray's Set up takes - pass the values on explicitly.
            return await SetupCommand.RunAsync(["setup", "--url", url, "--code", code, .. args.Where(a => a is "--force" or "--allow-http")], configPath);
        }

        if (!Environment.IsPrivilegedProcess)
        {
            Console.Error.WriteLine("Run as root: sudo ./matmon-agent install");
            return 2;
        }

        var enrolled = await EnrollCommand.EnrollAsync(url, code, args.Contains("--force"), args.Contains("--allow-http"), configPath);
        if (!enrolled.Success)
        {
            Console.Error.WriteLine(enrolled.Message);
            if (embedded is not null && enrolled.ExitCode == 5)
            {
                Console.Error.WriteLine("This download's code was already used or has expired - create a new one under Probes > Agents and download again.");
            }

            return enrolled.ExitCode;
        }

        try
        {
            var self = Environment.ProcessPath ?? throw new InvalidOperationException("the agent does not know its own executable");
            if (OperatingSystem.IsMacOS())
            {
                InstallMac(self);
            }
            else
            {
                InstallLinux(self);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Enrolled, but the service could not be installed: {ex.Message}");
            return 1;
        }

        Console.WriteLine($"{enrolled.Message.Split(". Identity")[0]}. The agent runs as a service ({AgentServiceFiles.UnixInstallPath}).");
        return 0;
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void InstallLinux(string self)
    {
        var service = ServiceControl.ResolveServiceName() is { } running && running == "matmon-agent.service" ? running : "matmon-agent";
        Run("systemctl", allowFailure: true, "stop", service);
        PlaceSelf(self, AgentServiceFiles.UnixInstallPath);
        File.WriteAllText(AgentServiceFiles.SystemdUnitPath, AgentServiceFiles.SystemdUnit());
        Run("systemctl", allowFailure: false, "daemon-reload");
        Run("systemctl", allowFailure: false, "enable", "matmon-agent");
        Run("systemctl", allowFailure: false, "restart", "matmon-agent");
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void InstallMac(string self)
    {
        var timeout = TimeSpan.FromSeconds(60);
        ServiceControl.Launchctl(timeout, allowFailure: true, "bootout", $"system/{AgentServiceFiles.LaunchdLabel}");
        PlaceSelf(self, AgentServiceFiles.UnixInstallPath);
        Directory.CreateDirectory(AgentServiceFiles.MacLogDirectory);
        File.WriteAllText(AgentServiceFiles.LaunchdPlistPath, AgentServiceFiles.LaunchdPlist());
        // launchd refuses a plist that is writable by anyone but root.
        File.SetUnixFileMode(AgentServiceFiles.LaunchdPlistPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        ServiceControl.Launchctl(timeout, allowFailure: false, "bootstrap", "system", AgentServiceFiles.LaunchdPlistPath);
    }

    /// <summary>
    /// Puts this executable at <paramref name="target"/> as the PLAIN package (without a download's code), through the
    /// updater's set-aside swap so a running copy there (a tray, a service) cannot block it. Nothing to do when the agent
    /// already runs from there.
    /// </summary>
    public static void PlaceSelf(string self, string target)
    {
        if (string.Equals(Path.GetFullPath(self), Path.GetFullPath(target), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var staged = $"{target}.new-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
        try
        {
            AgentInstallerPayload.CopyPlain(self, staged);
            ApplyUpdateCommand.Replace(staged, target, move: true);
        }
        finally
        {
            AgentUpdateFiles.TryDelete(staged);
        }
    }

    private static void Run(string program, bool allowFailure, params string[] arguments)
    {
        var start = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{program} did not start");
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 && !allowFailure)
        {
            throw new InvalidOperationException($"{program} {string.Join(' ', arguments)} failed: {error.Trim()}");
        }
    }
}
