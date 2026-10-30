using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using Microsoft.Win32;
using static Matmon.Host.Services.SelfUpdate.WindowsProbeSettings;

namespace Matmon.Host.Services.SelfUpdate;

/// <summary>What an installation is set up with.</summary>
/// <remarks><paramref name="TrayIcon"/>: start the tray icon (the setup program, <c>/tray</c>) at every sign-in - only
/// possible when the setup program is installed alongside (not for <c>install-service</c>).</remarks>
/// <paramref name="Probe"/>: install a REMOTE PROBE (Secondary mode) that reports to that primary instead of an instance.
public sealed record WindowsInstallOptions(string ProgramFolder, string DataFolder, int Port, bool OpenFirewall, bool TrayIcon = false,
    WindowsProbeConnection? Probe = null);

/// <summary>What a Windows probe connects with - the same three values a Docker probe gets as Matmon__* variables.</summary>
public sealed record WindowsProbeConnection(string PrimaryUrl, string ProbeId, string ProbeToken, string? ProbeName = null)
{
    /// <summary>What is wrong with it, in words - or null.</summary>
    public string? Problem()
    {
        if (!Uri.TryCreate(PrimaryUrl?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host))
        {
            return "The primary's address must be a full http:// or https:// address, e.g. https://matmon.example.";
        }

        if (string.IsNullOrWhiteSpace(ProbeId) || string.IsNullOrWhiteSpace(ProbeToken))
        {
            return "The probe id and the probe token are both needed - copy them from the probe's Install page on the primary.";
        }

        return null;
    }
}

/// <summary>Which Matmon this machine runs: ONE Matmon service per machine, either the instance or a remote probe.</summary>
public enum WindowsInstallationRole
{
    Instance,
    Probe
}

/// <summary>What is installed on this machine right now (from the "Apps" entry, else from the service itself).
/// <paramref name="PrimaryUrl"/> is a probe's primary - scheme and host only, it is shown to every user.</summary>
public sealed record WindowsInstallationInfo(string ProgramFolder, string DataFolder, int Port, string? Version,
    WindowsInstallationRole Role = WindowsInstallationRole.Instance, string? PrimaryUrl = null);

/// <summary>
/// The pure half of a Windows PROBE installation (what goes into the service's environment and the settings file, which role
/// change is refused) - apart from <see cref="WindowsInstallation"/> because it needs no Windows and is tested.
/// </summary>
public static class WindowsProbeSettings
{
    /// <summary>The variable that points the service at the probe's settings file (read by Program.cs as Matmon:SettingsFile).</summary>
    public const string SettingsFileVariable = "Matmon__SettingsFile";

    /// <summary>A probe's connection lives in the DATA folder, not in the service's registry environment: the service key
    /// (HKLM\SYSTEM\CurrentControlSet\Services\Matmon) can be read by every local user, the data folder only by SYSTEM
    /// and the administrators - and the token is the probe's credential.</summary>
    public const string ProbeSettingsFileName = "probe-settings.json";

    public static string ProbeSettingsPath(string dataFolder) => Path.Combine(dataFolder, ProbeSettingsFileName);

    /// <summary>The service's registry environment: the port and the data folder, for a probe also where its settings are.
    /// Never the token.</summary>
    public static string[] ServiceEnvironment(WindowsInstallOptions options)
    {
        var environment = new List<string>
        {
            $"ASPNETCORE_URLS=http://+:{options.Port}",
            $"Matmon__WorkspacePath={Path.Combine(options.DataFolder, "workspace.json")}"
        };
        if (options.Probe is not null)
        {
            environment.Add($"{SettingsFileVariable}={ProbeSettingsPath(options.DataFolder)}");
        }

        return [.. environment];
    }

    /// <summary>The probe's settings file - the "Matmon" section a Docker probe gets as Matmon__* variables.</summary>
    public static string ProbeSettingsJson(WindowsProbeConnection probe)
    {
        var section = new System.Text.Json.Nodes.JsonObject
        {
            ["Mode"] = "Secondary",
            ["PrimaryUrl"] = probe.PrimaryUrl.Trim().TrimEnd('/'),
            ["ProbeId"] = probe.ProbeId.Trim(),
            ["ProbeToken"] = probe.ProbeToken.Trim()
        };
        if (!string.IsNullOrWhiteSpace(probe.ProbeName))
        {
            section["ProbeName"] = probe.ProbeName.Trim();
        }

        return new System.Text.Json.Nodes.JsonObject { ["Matmon"] = section }
            .ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Reads a probe's connection back (an update keeps it); null when there is none or it cannot be read
    /// (the file is readable by administrators only - the setup runs elevated).</summary>
    public static WindowsProbeConnection? ReadProbeSettings(string dataFolder)
    {
        try
        {
            var path = ProbeSettingsPath(dataFolder);
            return File.Exists(path) ? ParseProbeSettings(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static WindowsProbeConnection? ParseProbeSettings(string json)
    {
        try
        {
            var section = System.Text.Json.Nodes.JsonNode.Parse(json)?["Matmon"];
            string? Value(string name) => section?[name]?.GetValue<string>();
            return Value("PrimaryUrl") is { Length: > 0 } url && Value("ProbeId") is { Length: > 0 } id && Value("ProbeToken") is { Length: > 0 } token
                ? new WindowsProbeConnection(url, id, token, Value("ProbeName"))
                : null;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// One Matmon service per machine, in ONE role: a setup that would turn an instance into a probe (or back) is refused -
    /// the instance's data would sit unused beside a probe, and a probe has no users to sign in with. Uninstall first
    /// (the data is kept unless asked), then install the other role.
    /// </summary>
    public static string? RoleChangeProblem(WindowsInstallationInfo? installed, bool probe)
    {
        if (installed is null || (installed.Role == WindowsInstallationRole.Probe) == probe)
        {
            return null;
        }

        return probe
            ? "This computer runs a Matmon instance. A computer runs one Matmon - uninstall the instance first (its data is kept), then install the probe. A Matmon Agent can run beside the instance instead."
            : "This computer runs a Matmon probe. A computer runs one Matmon - uninstall the probe first, then install the instance.";
    }

    /// <summary>The primary's address as it may be shown to every user: scheme, host and port only.</summary>
    public static string? DisplayAddress(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? $"{uri.Scheme}://{uri.Authority}" : null; // Authority leaves user info out
}

/// <summary>
/// Installing and removing the Windows instance - ONE implementation for the setup program (Matmon.Setup links this file)
/// and for <c>Matmon.Host.exe install-service</c>. Deliberately free of everything else in the Host (no Probe, no ASP.NET):
/// the setup is a small WinForms program and must not drag the sensor stack along.
///
/// The layout the self-update relies on: the program in its own folder (default <c>%ProgramFiles%\Matmon</c>, replaced as a
/// whole on every update), the data in <c>%ProgramData%\Matmon\data</c> (never touched by an update, an ACL of its own),
/// the setup program in <c>%ProgramData%\Matmon\setup</c> (outside the program folder, so an update does not take it away) -
/// it is the uninstaller AND the tray icon (<c>/tray</c>, started at every sign-in through HKLM Run),
/// a service <c>Matmon</c> whose registry environment carries the port and the data folder, and an entry in Windows'
/// "Installed apps" pointing at the uninstaller.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsInstallation
{
    public const string ServiceName = "Matmon";
    public const string FirewallRuleName = "Matmon";
    public const string ExecutableName = "Matmon.Host.exe";
    public const string SetupProgramName = "Matmon.exe";
    private const string UninstallKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Matmon";
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Matmon";
    private static readonly TimeSpan ServiceTimeout = TimeSpan.FromSeconds(90);

    public static string DefaultProgramFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Matmon");

    public static string DefaultDataFolder => Path.Combine(MatmonDataRoot, "data");

    /// <summary>Where the installed program lives that is the tray icon, the uninstaller and the elevated service helper in
    /// one: outside the program folder, because a self-update swaps that whole folder and a running tray would block it.
    /// It is called <c>Matmon.exe</c> (it used to be "Matmon Setup.exe" in <c>setup\</c>, which read as if an installer
    /// ran at every sign-in - <see cref="LegacySetupProgramPath"/>, moved by <see cref="RefreshSetupProgram"/>).</summary>
    public static string SetupFolder => Path.Combine(MatmonDataRoot, "app");

    public static string SetupProgramPath => Path.Combine(SetupFolder, SetupProgramName);

    public static string LegacySetupFolder => Path.Combine(MatmonDataRoot, "setup");

    public static string LegacySetupProgramPath => Path.Combine(LegacySetupFolder, "Matmon Setup.exe");

    /// <summary>Whether the tray icon is set to start at sign-in.</summary>
    public static bool TrayIconRegistered()
    {
        using var run = Registry.LocalMachine.OpenSubKey(RunKeyPath);
        return run?.GetValue(RunValueName) is string;
    }

    private static string MatmonDataRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Matmon");

    /// <summary>The installation on this machine, or null when there is none.</summary>
    public static WindowsInstallationInfo? Find()
    {
        using var entry = Registry.LocalMachine.OpenSubKey(UninstallKeyPath);
        if (entry?.GetValue("InstallLocation") is string location && !string.IsNullOrWhiteSpace(location))
        {
            return new WindowsInstallationInfo(location,
                entry.GetValue("MatmonDataFolder") as string ?? DefaultDataFolder,
                entry.GetValue("MatmonPort") is int port ? port : 8099,
                entry.GetValue("DisplayVersion") as string,
                string.Equals(entry.GetValue("MatmonRole") as string, "probe", StringComparison.OrdinalIgnoreCase)
                    ? WindowsInstallationRole.Probe
                    : WindowsInstallationRole.Instance,
                entry.GetValue("MatmonPrimaryUrl") as string);
        }

        // Installed with install-service before the setup existed: the service knows where it lives.
        using var service = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{ServiceName}");
        if (service?.GetValue("ImagePath") is not string imagePath)
        {
            return null;
        }

        var executable = imagePath.Trim().Trim('"');
        var environment = service.GetValue("Environment") as string[] ?? [];
        var workspace = Read(environment, "Matmon__WorkspacePath=");
        var urls = Read(environment, "ASPNETCORE_URLS=");
        var portText = urls?[(urls.LastIndexOf(':') + 1)..].TrimEnd('/');
        return new WindowsInstallationInfo(Path.GetDirectoryName(executable) ?? DefaultProgramFolder,
            workspace is null ? DefaultDataFolder : Path.GetDirectoryName(workspace) ?? DefaultDataFolder,
            int.TryParse(portText, out var servicePort) ? servicePort : 8099, null,
            Read(environment, SettingsFileVariable + "=") is null ? WindowsInstallationRole.Instance : WindowsInstallationRole.Probe);
    }


    /// <summary>
    /// Installs (or updates in place) from <paramref name="sourceFolder"/> - an unpacked package. <paramref name="uninstaller"/>
    /// is the bare setup program; without it (install-service) the "Installed apps" entry uninstalls through the console.
    /// </summary>
    public static void Install(string sourceFolder, WindowsInstallOptions options, string version, byte[]? uninstaller, Action<string> progress)
    {
        var executable = Path.Combine(options.ProgramFolder, ExecutableName);
        var exists = ServiceExists();
        if (exists)
        {
            progress("Stopping the running Matmon service ...");
            StopService();
        }

        if (!SamePath(sourceFolder, options.ProgramFolder))
        {
            progress($"Copying the program to {options.ProgramFolder} ...");
            WindowsFolderLocks.EndProcessesRunningFrom(options.ProgramFolder, progress);
            RetryFolder(() => DeleteDirectory(options.ProgramFolder), options.ProgramFolder);
            CopyDirectory(sourceFolder, options.ProgramFolder);
        }

        progress($"Preparing the data folder {options.DataFolder} ...");
        Directory.CreateDirectory(options.DataFolder);
        SecureFolder(options.DataFolder);

        var settingsPath = ProbeSettingsPath(options.DataFolder);
        if (options.Probe is { } probe)
        {
            // Into the data folder (SYSTEM + administrators only, inherited from the folder) - never the registry.
            progress("Saving the probe's connection to its primary ...");
            var temporary = settingsPath + ".tmp";
            File.WriteAllText(temporary, ProbeSettingsJson(probe));
            File.Move(temporary, settingsPath, overwrite: true);
        }
        else if (File.Exists(settingsPath))
        {
            File.Delete(settingsPath);
        }

        var displayName = options.Probe is null ? "Matmon" : "Matmon probe";
        var description = options.Probe is null
            ? "Matmon network monitoring - the web interface and the sensors of this instance."
            : "Matmon remote probe - runs sensors on this network for its Matmon primary.";
        progress(exists ? "Updating the Windows service ..." : "Creating the Windows service ...");
        if (exists)
        {
            Run("sc.exe", $"config {ServiceName} binPath= \"\\\"{executable}\\\"\" start= delayed-auto DisplayName= \"{displayName}\"");
        }
        else
        {
            Run("sc.exe", $"create {ServiceName} binPath= \"\\\"{executable}\\\"\" start= delayed-auto DisplayName= \"{displayName}\"");
            Run("sc.exe", $"failure {ServiceName} reset= 86400 actions= restart/10000/restart/60000/restart/60000");
        }

        Run("sc.exe", $"description {ServiceName} \"{description}\"");

        using (var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{ServiceName}", writable: true)
                   ?? throw new InvalidOperationException("the service's registry key is missing"))
        {
            key.SetValue("Environment", ServiceEnvironment(options), RegistryValueKind.MultiString);
        }

        // One rule, named, so a changed port or an unticked box replaces it instead of piling rules up.
        TryRun("netsh", $"advfirewall firewall delete rule name=\"{FirewallRuleName}\"");
        if (options.OpenFirewall)
        {
            progress($"Allowing TCP {options.Port} in the Windows firewall ...");
            Run("netsh", $"advfirewall firewall add rule name=\"{FirewallRuleName}\" dir=in action=allow protocol=TCP localport={options.Port} program=\"{executable}\"");
        }

        progress("Registering Matmon in Installed apps ...");
        string uninstallCommand;
        if (uninstaller is not null)
        {
            Directory.CreateDirectory(SetupFolder);
            // Every user runs the tray from here, so Users may read and run it - but not change it.
            SecureFolder(SetupFolder, usersMayRun: true);
            StopTrayIcons();
            Retry(() => File.WriteAllBytes(SetupProgramPath, uninstaller));
            // An installation from before the rename had it as "setup\Matmon Setup.exe" (its tray was ended above).
            DeleteDirectoryQuietly(LegacySetupFolder);
            uninstallCommand = $"\"{SetupProgramPath}\" /uninstall";
        }
        else
        {
            uninstallCommand = $"\"{executable}\" uninstall-service";
        }

        Register(options, version, uninstallCommand);
        SetTrayIcon(options.TrayIcon && uninstaller is not null);

        progress("Starting Matmon ...");
        StartService();
        progress(WaitForHealth(options.Port, TimeSpan.FromSeconds(90))
            ? options.Probe is null
                ? $"Matmon {version} is running: http://localhost:{options.Port}"
                : $"The Matmon probe {version} is running and reports to {DisplayAddress(options.Probe.PrimaryUrl)}."
            : $"The service was started, but Matmon does not answer on port {options.Port} yet - look at the Windows event log if that stays so.");
    }

    /// <summary>Removes the service, the firewall rule, the program and the "Installed apps" entry - and the data only when asked.</summary>
    public static void Uninstall(bool removeData, Action<string> progress)
    {
        var installed = Find();
        if (ServiceExists())
        {
            progress("Stopping the Matmon service ...");
            StopService();
            progress("Removing the Windows service ...");
            Run("sc.exe", $"delete {ServiceName}");
        }

        TryRun("netsh", $"advfirewall firewall delete rule name=\"{FirewallRuleName}\"");
        SetTrayIcon(false);
        StopTrayIcons();

        if (installed is not null)
        {
            progress($"Removing the program from {installed.ProgramFolder} ...");
            WindowsFolderLocks.EndProcessesRunningFrom(installed.ProgramFolder, progress);
            RetryFolder(() => DeleteDirectory(installed.ProgramFolder), installed.ProgramFolder);
            foreach (var leftover in LeftoversOf(installed.ProgramFolder))
            {
                DeleteDirectoryQuietly(leftover);
            }

            if (removeData)
            {
                progress($"Deleting the data in {installed.DataFolder} ...");
                Retry(() => DeleteDirectory(installed.DataFolder));
            }
        }

        DeleteDirectoryQuietly(SetupFolder);
        DeleteDirectoryQuietly(LegacySetupFolder);
        if (Directory.Exists(MatmonDataRoot) && !Directory.EnumerateFileSystemEntries(MatmonDataRoot).Any())
        {
            DeleteDirectoryQuietly(MatmonDataRoot);
        }

        Registry.LocalMachine.DeleteSubKeyTree(UninstallKeyPath, throwOnMissingSubKey: false);
        progress(removeData || installed is null
            ? "Matmon has been removed."
            : $"Matmon has been removed. The data was kept in {installed.DataFolder}.");
    }

    /// <summary>Where the program package carries the bare setup program of its build (the uninstaller and tray icon).</summary>
    public static string PackagedSetupPath(string programFolder) => Path.Combine(programFolder, "setup", "MatmonSetup.exe");

    private static int _setupRefreshed;

    /// <summary>
    /// After a self-update, puts the new build's setup program in place of the installed one - the self-update swaps only
    /// the program folder, and the tray icon and the uninstaller live outside it, so they would otherwise stay on the build
    /// that was first installed for good. The running copy is renamed aside (Windows allows renaming a running exe, not
    /// overwriting it); a running tray notices the new file and restarts itself onto it. Once per process; called by the
    /// running service. Only where the setup installed one - a bare install-service has no tray to keep current.
    /// </summary>
    public static void RefreshSetupProgram(string programFolder)
    {
        if (Interlocked.Exchange(ref _setupRefreshed, 1) == 1)
        {
            return;
        }

        try
        {
            var source = PackagedSetupPath(programFolder);
            if (!File.Exists(source))
            {
                return;
            }

            if (File.Exists(LegacySetupProgramPath))
            {
                MoveFromLegacyLocation(source);
            }

            if (!File.Exists(SetupProgramPath))
            {
                return; // a bare install-service: no tray to keep current
            }

            DeleteLeftovers(SetupFolder, SetupProgramName);

            // The tray program FIRST: the Start menu entry below is cosmetic and must never be what keeps an old tray in place.
            LastTrayRefresh = ReplaceRunning(source, SetupProgramPath)
                ? $"Tray program replaced by this build's ({DateTime.Now:g}) - a running tray restarts onto it within a few seconds."
                : "Tray program is this build's.";

            // An installation from before the Start menu entry existed gets it here.
            if (TrayIconRegistered())
            {
                EnsureStartMenuShortcut();
            }
        }
        catch (Exception ex)
        {
            // The tray and the uninstaller keep working on the old build; the next look tries again (every failure, not
            // only file errors - an unexpected one used to leave the old tray in place for good, said nowhere).
            LastTrayRefresh = $"Tray program could not be replaced: {ex.Message}";
            Interlocked.Exchange(ref _setupRefreshed, 0);
        }
    }

    /// <summary>What the last <see cref="RefreshSetupProgram"/> did - shown on System > Updates; null before it ran.</summary>
    public static string? LastTrayRefresh { get; private set; }

    /// <summary>
    /// Moves an installation from "setup\Matmon Setup.exe" to "app\Matmon.exe": the new file, the sign-in entry, the
    /// Start menu entry and the uninstall command point at the new place. The OLD file is replaced by the new build too,
    /// so a tray still running from there restarts onto it (it watches its own file) and that new build hands over to
    /// app\Matmon.exe at once; the old folder goes once nothing runs from it any more.
    /// </summary>
    private static void MoveFromLegacyLocation(string source)
    {
        Directory.CreateDirectory(SetupFolder);
        SecureFolder(SetupFolder, usersMayRun: true);
        if (!File.Exists(SetupProgramPath))
        {
            File.Copy(source, SetupProgramPath);
        }

        if (TrayIconRegistered())
        {
            SetTrayIcon(true); // rewrites the Run value and replaces the Start menu entry
        }

        using (var entry = Registry.LocalMachine.OpenSubKey(UninstallKeyPath, writable: true))
        {
            if (entry?.GetValue("UninstallString") is string uninstall && uninstall.Contains(LegacySetupProgramPath, StringComparison.OrdinalIgnoreCase))
            {
                entry.SetValue("UninstallString", $"\"{SetupProgramPath}\" /uninstall");
                entry.SetValue("QuietUninstallString", $"\"{SetupProgramPath}\" /uninstall /silent");
            }
        }

        DeleteLeftovers(LegacySetupFolder, Path.GetFileName(LegacySetupProgramPath));
        if (TrayRunsFrom(LegacySetupProgramPath))
        {
            ReplaceRunning(source, LegacySetupProgramPath);
        }
        else
        {
            DeleteDirectoryQuietly(LegacySetupFolder);
        }
    }

    private static bool TrayRunsFrom(string path)
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path)))
        {
            using (process)
            {
                try
                {
                    if (SamePath(process.MainModule?.FileName ?? "", path))
                    {
                        return true;
                    }
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                }
            }
        }

        return false;
    }

    /// <summary>Puts <paramref name="source"/> in place of <paramref name="target"/>, which may be running: the running copy is
    /// renamed aside (Windows allows renaming a running exe, not overwriting it) and deleted on a later start.</summary>
    private static bool ReplaceRunning(string source, string target)
    {
        if (SameContent(source, target))
        {
            return false;
        }

        var aside = $"{target}.old-{DateTime.UtcNow:yyyyMMddHHmmss}";
        File.Move(target, aside);
        try
        {
            File.Copy(source, target);
            // A copy keeps the SOURCE's write time; the running tray watches time + length to notice it was replaced.
            File.SetLastWriteTimeUtc(target, DateTime.UtcNow);
        }
        catch
        {
            File.Move(aside, target);
            throw;
        }

        return true;
    }

    private static void DeleteLeftovers(string folder, string fileName)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        foreach (var leftover in Directory.GetFiles(folder, fileName + ".old-*"))
        {
            try
            {
                File.Delete(leftover);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still held by a tray that has not restarted yet - next time.
            }
        }
    }

    private static bool SameContent(string first, string second)
    {
        if (new FileInfo(first).Length != new FileInfo(second).Length)
        {
            return false;
        }

        using var a = File.OpenRead(first);
        using var b = File.Open(second, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return System.Security.Cryptography.SHA256.HashData(a).AsSpan().SequenceEqual(System.Security.Cryptography.SHA256.HashData(b));
    }

    /// <summary>After a self-update the "Installed apps" entry should show the new version. Called by the running service.</summary>
    public static void RefreshRegisteredVersion(string version)
    {
        try
        {
            using var entry = Registry.LocalMachine.OpenSubKey(UninstallKeyPath, writable: true);
            if (entry is not null && !string.Equals(entry.GetValue("DisplayVersion") as string, version, StringComparison.Ordinal))
            {
                entry.SetValue("DisplayVersion", version);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // Not worth failing anything over - the entry is cosmetic.
        }
    }

    private static void Register(WindowsInstallOptions options, string version, string uninstallCommand)
    {
        using var entry = Registry.LocalMachine.CreateSubKey(UninstallKeyPath, writable: true);
        entry.SetValue("DisplayName", options.Probe is null ? "Matmon" : "Matmon probe");
        entry.SetValue("DisplayVersion", version);
        entry.SetValue("Publisher", "Matmon");
        entry.SetValue("DisplayIcon", $"{Path.Combine(options.ProgramFolder, ExecutableName)},0");
        entry.SetValue("InstallLocation", options.ProgramFolder);
        entry.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        entry.SetValue("UninstallString", uninstallCommand);
        entry.SetValue("QuietUninstallString", uninstallCommand.EndsWith("/uninstall", StringComparison.Ordinal) ? uninstallCommand + " /silent" : uninstallCommand);
        entry.SetValue("URLInfoAbout", "https://matmon.eu");
        entry.SetValue("NoModify", 1, RegistryValueKind.DWord);
        entry.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        entry.SetValue("EstimatedSize", (int)(FolderSize(options.ProgramFolder) / 1024), RegistryValueKind.DWord);
        entry.SetValue("MatmonDataFolder", options.DataFolder);
        entry.SetValue("MatmonPort", options.Port, RegistryValueKind.DWord);
        // Read by the tray (every user may read this key): the role and the primary's address - never the token.
        entry.SetValue("MatmonRole", options.Probe is null ? "instance" : "probe");
        if (options.Probe is not null && DisplayAddress(options.Probe.PrimaryUrl) is { } primary)
        {
            entry.SetValue("MatmonPrimaryUrl", primary);
        }
        else
        {
            entry.DeleteValue("MatmonPrimaryUrl", throwOnMissingValue: false);
        }
    }

    /// <summary>SYSTEM (the service) and Administrators only: under ProgramData a folder would inherit "Users may read"
    /// (and "Users may create files"), and the data folder holds workspace.json AND the DataProtection keys that decrypt its
    /// secrets. Owned by Administrators, so a user who created the folder first cannot change it back.
    /// <paramref name="usersMayRun"/> adds read + execute for Users (the setup folder: every user's tray runs from there).</summary>
    public static void SecureFolder(string directory, bool usersMayRun = false)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                inherit, PropagationFlags.None, AccessControlType.Allow));
        }

        if (usersMayRun)
        {
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        }

        new DirectoryInfo(directory).SetAccessControl(security);
    }

    private static void SetTrayIcon(bool enabled)
    {
        using (var run = Registry.LocalMachine.CreateSubKey(RunKeyPath, writable: true))
        {
            if (enabled)
            {
                run.SetValue(RunValueName, $"\"{SetupProgramPath}\" /tray");
            }
            else if (run.GetValue(RunValueName) is not null)
            {
                run.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
        }

        if (enabled)
        {
            EnsureStartMenuShortcut(replace: true);
        }
        else
        {
            TryDelete(StartMenuShortcutPath);
        }
    }

    /// <summary>The Start menu entry "Matmon" (all users): starts the tray icon again after it was closed - or, when one runs
    /// already, opens Matmon in the browser.</summary>
    public static string StartMenuShortcutPath => Matmon.Probe.WindowsShortcut.StartMenuPath("Matmon");

    /// <summary>Creates the Start menu entry when it is missing (an installation from before it existed gets it from the
    /// running service after its next update). Cosmetic: a failure is swallowed.</summary>
    public static void EnsureStartMenuShortcut(bool replace = false)
    {
        if ((File.Exists(StartMenuShortcutPath) && !replace) || !File.Exists(SetupProgramPath))
        {
            return;
        }

        // No Start menu entry on a failure - the tray still starts at sign-in.
        Matmon.Probe.WindowsShortcut.Create(StartMenuShortcutPath, SetupProgramPath, "/tray", $"{SetupProgramPath},0", "Matmon", SetupFolder);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind - harmless.
        }
    }

    /// <summary>Ends every running tray icon (any user): it holds the setup program open, which is about to be replaced or removed.</summary>
    private static void StopTrayIcons()
    {
        // The tray under its current name and under the one it had before (a moved installation may still run the old one).
        var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(SetupProgramName))
            .Concat(Process.GetProcessesByName(Path.GetFileNameWithoutExtension(LegacySetupProgramPath)));
        foreach (var process in processes)
        {
            using (process)
            {
                try
                {
                    var file = process.MainModule?.FileName ?? "";
                    if (process.Id != Environment.ProcessId && (SamePath(file, SetupProgramPath) || SamePath(file, LegacySetupProgramPath)))
                    {
                        process.Kill();
                        process.WaitForExit(5000);
                    }
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    // Already gone, or not ours to look at.
                }
            }
        }
    }

    public static bool ServiceExists() =>
        ServiceController.GetServices().Any(service => string.Equals(service.ServiceName, ServiceName, StringComparison.OrdinalIgnoreCase));

    public static void StopService()
    {
        using var controller = new ServiceController(ServiceName);
        if (controller.Status is ServiceControllerStatus.Stopped)
        {
            return;
        }

        if (controller.Status is not ServiceControllerStatus.StopPending)
        {
            controller.Stop();
        }

        controller.WaitForStatus(ServiceControllerStatus.Stopped, ServiceTimeout);
    }

    public static void StartService()
    {
        using var controller = new ServiceController(ServiceName);
        if (controller.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending)
        {
            return;
        }

        controller.Start();
        controller.WaitForStatus(ServiceControllerStatus.Running, ServiceTimeout);
    }

    private static bool WaitForHealth(int port, TimeSpan timeout)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (http.GetAsync($"http://127.0.0.1:{port}/healthz").GetAwaiter().GetResult().IsSuccessStatusCode)
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
            }

            Thread.Sleep(1500);
        }

        return false;
    }

    /// <summary>What a self-update leaves beside the program folder (.updater, .bak-*, .new-*, .failed-*).</summary>
    private static IEnumerable<string> LeftoversOf(string programFolder)
    {
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(programFolder));
        return parent is null || !Directory.Exists(parent)
            ? []
            : Directory.EnumerateDirectories(parent, Path.GetFileName(Path.TrimEndingDirectorySeparator(programFolder)) + ".*");
    }

    public static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)), overwrite: true);
        }
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static void DeleteDirectoryQuietly(string path)
    {
        try
        {
            DeleteDirectory(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // In use (the running uninstaller lives in the setup folder) - harmless leftover.
        }
    }

    // "Service stopped" and "every file released" are not the same instant on Windows.
    /// <summary><see cref="Retry"/> for replacing or removing a folder: when it still fails, the message says WHO keeps it busy.</summary>
    private static void RetryFolder(Action action, string folder)
    {
        try
        {
            Retry(action);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var holders = WindowsFolderLocks.Describe(folder);
            throw new IOException(holders is null
                ? $"{folder} is in use - close any window or program that has it open, then try again. ({ex.Message})"
                : $"{folder} is in use by {holders} - close it, then try again. ({ex.Message})", ex);
        }
    }

    private static void Retry(Action action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 30)
            {
                Thread.Sleep(1000);
            }
        }
    }

    private static long FolderSize(string folder) =>
        Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length) : 0;

    private static bool SamePath(string first, string second) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
            StringComparison.OrdinalIgnoreCase);

    private static string? Read(IEnumerable<string> environment, string prefix) =>
        environment.FirstOrDefault(entry => entry.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..];

    private static void TryRun(string file, string arguments)
    {
        try
        {
            Run(file, arguments);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void Run(string file, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(file, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true
        }) ?? throw new InvalidOperationException($"{file} did not start");
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{file} {arguments.Split(' ')[0]} failed ({process.ExitCode}): {output.Trim()}");
        }
    }
}
