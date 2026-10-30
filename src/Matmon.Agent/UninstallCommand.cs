using System.Diagnostics;
using System.Runtime.Versioning;
using Matmon.Probe;
using Microsoft.Win32;

namespace Matmon.Agent;

/// <summary>
/// <c>matmon-agent uninstall [--keep-config] [--gui]</c> - takes off everything <see cref="SetupCommand"/> /
/// <see cref="InstallCommand"/> put on the machine:
/// <list type="bullet">
/// <item>Windows: the service, the tray (running copies, sign-in entry, Start menu entry), the "Installed apps" entry and
/// <c>%ProgramFiles%\Matmon Agent</c>; the configuration folder <c>%ProgramData%\Matmon Agent</c> too unless
/// <c>--keep-config</c>. Not elevated, it asks for administrator rights itself (Windows "Installed apps" starts it that way).</item>
/// <item>Linux: stops and disables the systemd unit, removes it, the binary and <c>/etc/matmon-agent</c> + <c>/var/lib/matmon-agent</c>.</item>
/// <item>macOS: unloads the LaunchDaemon, removes it, the binary, its configuration folder and its logs.</item>
/// </list>
/// The agent stays listed on the instance (it only stops reporting) - it is removed there under Probes > Agents.
/// <c>--gui</c> (the tray and "Installed apps"): asks before and says the outcome in a message box instead of the console.
/// </summary>
public static class UninstallCommand
{
    public const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\MatmonAgent";
    private const string ConfirmedFlag = "--confirmed";

    public static int Run(string[] args, string configPath)
    {
        var gui = args.Contains("--gui");
        var keepConfig = args.Contains("--keep-config");

        if (OperatingSystem.IsWindows())
        {
            if (gui && !args.Contains(ConfirmedFlag) &&
                !Ask($"Remove the Matmon Agent from this computer?\n\nIts service, tray icon and program folder are removed{(keepConfig ? string.Empty : ", and its configuration")}. " +
                     "The agent stays listed on your Matmon instance until you delete it there (Probes > Agents)."))
            {
                return 0;
            }

            if (!Environment.IsPrivilegedProcess)
            {
                return RelaunchElevated(args, gui);
            }
        }
        else if (!Environment.IsPrivilegedProcess)
        {
            Console.Error.WriteLine("Run as root: sudo matmon-agent uninstall");
            return 2;
        }

        try
        {
            var notes = OperatingSystem.IsWindows()
                ? UninstallWindows(configPath, keepConfig)
                : OperatingSystem.IsMacOS()
                    ? UninstallMac(configPath, keepConfig)
                    : UninstallLinux(configPath, keepConfig);
            var message = "The Matmon Agent was removed from this computer." +
                          (notes.Count == 0 ? string.Empty : " " + string.Join(" ", notes)) +
                          " Delete it on your Matmon instance under Probes > Agents.";
            Say(gui, message, error: false);
            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Say(gui, $"The Matmon Agent could not be removed completely: {ex.Message}", error: true);
            return 1;
        }
    }

    [SupportedOSPlatform("windows")]
    private static List<string> UninstallWindows(string configPath, bool keepConfig)
    {
        var notes = new List<string>();
        var installed = SetupCommand.InstalledPath;
        var installDirectory = Path.GetDirectoryName(installed)!;

        foreach (var service in new[] { SetupCommand.InstalledServiceName(), SetupCommand.ServiceName }.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!ServiceExists(service))
            {
                continue;
            }

            try
            {
                ServiceControl.Stop(service, TimeSpan.FromSeconds(60));
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException)
            {
                // Deleting still marks it for removal; it goes once it has stopped.
            }

            SetupCommand.Sc("delete", $"\"{service}\"");
        }

        // Every other running copy from the program folder - the trays of every signed-in user (they hold the file open).
        foreach (var process in Process.GetProcessesByName("matmon-agent"))
        {
            using (process)
            {
                try
                {
                    if (process.Id != Environment.ProcessId &&
                        string.Equals(process.MainModule?.FileName, installed, StringComparison.OrdinalIgnoreCase))
                    {
                        process.Kill();
                        process.WaitForExit(5000);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
            }
        }

        using (var run = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: true))
        {
            run?.DeleteValue(SetupCommand.TrayRunValue, throwOnMissingValue: false);
        }

        WindowsShortcut.Delete(SetupCommand.StartMenuShortcutPath);
        Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);

        var configDirectory = Path.GetDirectoryName(configPath)!;
        if (!keepConfig && Directory.Exists(configDirectory))
        {
            DeleteDirectory(configDirectory);
        }
        else if (keepConfig)
        {
            notes.Add($"Its configuration stays in {configDirectory}.");
        }

        // The program folder last. When this very process runs from it (the tray's Uninstall, "Installed apps"), the folder
        // cannot go while it runs - a hidden cmd removes it a moment after this process has ended.
        if (Directory.Exists(installDirectory))
        {
            var self = Environment.ProcessPath;
            var runsFromIt = self is not null && Path.GetFullPath(self).StartsWith(Path.GetFullPath(installDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            if (runsFromIt || !TryDeleteDirectory(installDirectory))
            {
                using var _ = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                    $"/c ping -n 4 127.0.0.1 >nul & rmdir /s /q \"{installDirectory}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetTempPath()
                });
            }
        }

        return notes;
    }

    [UnsupportedOSPlatform("windows")]
    private static List<string> UninstallLinux(string configPath, bool keepConfig)
    {
        Run("systemctl", "stop", "matmon-agent");
        Run("systemctl", "disable", "matmon-agent");
        TryDeleteFile(AgentServiceFiles.SystemdUnitPath);
        Run("systemctl", "daemon-reload");
        TryDeleteFile(AgentServiceFiles.UnixInstallPath);
        return RemoveUnixConfig(configPath, keepConfig);
    }

    [UnsupportedOSPlatform("windows")]
    private static List<string> UninstallMac(string configPath, bool keepConfig)
    {
        ServiceControl.Launchctl(TimeSpan.FromSeconds(60), allowFailure: true, "bootout", $"system/{AgentServiceFiles.LaunchdLabel}");
        TryDeleteFile(AgentServiceFiles.LaunchdPlistPath);
        TryDeleteFile(AgentServiceFiles.UnixInstallPath);
        var notes = RemoveUnixConfig(configPath, keepConfig);
        if (!keepConfig && Directory.Exists(AgentServiceFiles.MacLogDirectory))
        {
            DeleteDirectory(AgentServiceFiles.MacLogDirectory);
        }

        return notes;
    }

    private static List<string> RemoveUnixConfig(string configPath, bool keepConfig)
    {
        var configDirectory = Path.GetDirectoryName(configPath)!;
        if (keepConfig)
        {
            return [$"Its configuration stays in {configDirectory}."];
        }

        foreach (var directory in new[] { configDirectory, AgentConfigFile.ResolveStateDirectory(configPath) }.Distinct())
        {
            if (Directory.Exists(directory))
            {
                DeleteDirectory(directory);
            }
        }

        return [];
    }

    /// <summary>
    /// The "Installed apps" entry, written by setup and refreshed by the service at every start (so an agent installed before
    /// it existed gets one after its next update): name, version, icon and the command that removes it.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static void EnsureUninstallEntry()
    {
        try
        {
            var installed = SetupCommand.InstalledPath;
            if (!File.Exists(installed))
            {
                return;
            }

            using var key = Registry.LocalMachine.CreateSubKey(UninstallKey);
            key.SetValue("DisplayName", "Matmon Agent");
            key.SetValue("DisplayVersion", MatmonVersion.Current);
            key.SetValue("Publisher", "Matmon");
            key.SetValue("DisplayIcon", $"{installed},0");
            key.SetValue("InstallLocation", Path.GetDirectoryName(installed)!);
            key.SetValue("UninstallString", $"\"{installed}\" uninstall --gui");
            key.SetValue("QuietUninstallString", $"\"{installed}\" uninstall");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("EstimatedSize", (int)(new FileInfo(installed).Length / 1024), RegistryValueKind.DWord);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }
    }

    [SupportedOSPlatform("windows")]
    private static int RelaunchElevated(string[] args, bool gui)
    {
        try
        {
            var arguments = string.Join(' ', args.Append(ConfirmedFlag).Distinct().Select(argument => argument.Contains(' ') ? $"\"{argument}\"" : argument));
            using var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            });
            process?.WaitForExit();
            return process?.ExitCode ?? 1;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Say(gui, "Removing the agent needs administrator rights - the Windows prompt was cancelled.", error: true);
            return 1;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool ServiceExists(string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
        return key is not null;
    }

    private static bool TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void DeleteDirectory(string path)
    {
        if (!TryDeleteDirectory(path))
        {
            throw new IOException($"{path} could not be removed - something still uses it.");
        }
    }

    private static void TryDeleteFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void Run(string program, params string[] arguments)
    {
        var start = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start);
            process?.WaitForExit(60_000);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No systemctl (a container, a hand-made install): nothing of it to undo.
        }
    }

    private static bool Ask(string question)
    {
#if WINDOWS
        return System.Windows.Forms.MessageBox.Show(question, "Uninstall Matmon Agent", System.Windows.Forms.MessageBoxButtons.YesNo,
            System.Windows.Forms.MessageBoxIcon.Question, System.Windows.Forms.MessageBoxDefaultButton.Button2) == System.Windows.Forms.DialogResult.Yes;
#else
        _ = question;
        return true;
#endif
    }

    private static void Say(bool gui, string message, bool error)
    {
        (error ? Console.Error : Console.Out).WriteLine(message);
#if WINDOWS
        if (gui)
        {
            System.Windows.Forms.MessageBox.Show(message, "Matmon Agent", System.Windows.Forms.MessageBoxButtons.OK,
                error ? System.Windows.Forms.MessageBoxIcon.Warning : System.Windows.Forms.MessageBoxIcon.Information);
        }
#else
        _ = gui;
#endif
    }
}
