using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Principal;
using Matmon.Host.Services.SelfUpdate;
using Matmon.Probe;

namespace Matmon.Setup;

/// <summary>
/// The setup's command line: <c>MatmonSetup.exe [/silent] [/dir=&lt;folder&gt;] [/data=&lt;folder&gt;] [/port=8099] [/firewall]
/// [/notray]</c> installs or updates; <c>/uninstall [/silent] [/removedata]</c> removes. <c>/package=&lt;zip&gt;</c> installs from
/// a separate package (a development build of the setup carries none). The installed copy also runs the tray icon
/// (<c>/tray</c>) and controls the service for it (<c>/service=start|stop|restart</c>).
/// </summary>
internal sealed record SetupArguments(
    bool Uninstall,
    bool Silent,
    string? ProgramFolder,
    string? DataFolder,
    int? Port,
    bool OpenFirewall,
    bool RemoveData,
    string? PackagePath,
    bool Relaunched,
    bool Tray,
    bool NoTray,
    string? ServiceAction)
{
    /// <summary>What needs administrator rights: everything but the tray icon.</summary>
    public bool NeedsElevation => !Tray;

    public static SetupArguments Parse(string[] args)
    {
        string? Value(string name) => args
            .Select(arg => arg.Trim())
            .FirstOrDefault(arg => arg.StartsWith($"/{name}=", StringComparison.OrdinalIgnoreCase) || arg.StartsWith($"--{name}=", StringComparison.OrdinalIgnoreCase))
            ?.Split('=', 2)[1].Trim('"');
        bool Flag(params string[] names) => args.Any(arg => names.Any(name =>
            string.Equals(arg, $"/{name}", StringComparison.OrdinalIgnoreCase) || string.Equals(arg, $"--{name}", StringComparison.OrdinalIgnoreCase)));

        return new SetupArguments(
            Flag("uninstall", "u"),
            Flag("silent", "quiet", "s", "q"),
            Value("dir"),
            Value("data"),
            int.TryParse(Value("port"), out var port) && port is > 0 and < 65536 ? port : null,
            Flag("firewall"),
            Flag("removedata"),
            Value("package"),
            Flag("relaunched"),
            Flag("tray"),
            Flag("notray"),
            Value("service")?.ToLowerInvariant() is { } action && action is "start" or "stop" or "restart" ? action : null);
    }
}

/// <summary>What the setup does, without a window - the wizard and the silent mode both call this.</summary>
internal static class SetupRunner
{
    public static string Version { get; } = Assembly.GetEntryAssembly()?
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => attribute.Key == "MatmonVersion")?.Value ?? "local";

    public static string SelfPath => Environment.ProcessPath ?? throw new InvalidOperationException("the setup does not know its own file");

    /// <summary>Whether a package is there to install - appended to this file, or given with /package.</summary>
    public static bool HasPackage(SetupArguments arguments)
    {
        if (arguments.PackagePath is not null)
        {
            return File.Exists(arguments.PackagePath);
        }

        using var self = File.OpenRead(SelfPath);
        return SetupPayload.Find(self) is not null;
    }

    public static void Install(SetupArguments arguments, WindowsInstallOptions options, Action<string> progress)
    {
        var temporary = Path.Combine(Path.GetTempPath(), $"matmon-setup-{Guid.NewGuid():N}");
        try
        {
            progress("Unpacking Matmon ...");
            using (var self = File.OpenRead(SelfPath))
            {
                if (arguments.PackagePath is not null)
                {
                    ZipFile.ExtractToDirectory(arguments.PackagePath, temporary);
                }
                else
                {
                    var (offset, length) = SetupPayload.Find(self) ?? throw new InvalidOperationException("this setup carries no Matmon package");
                    using var package = SetupPayload.OpenPackage(self, offset, length);
                    ZipFile.ExtractToDirectory(package, temporary);
                }
            }

            WindowsInstallation.Install(temporary, options, Version, BareSetup(), progress);
        }
        finally
        {
            try
            {
                Directory.Delete(temporary, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public static void Uninstall(bool removeData, Action<string> progress) => WindowsInstallation.Uninstall(removeData, progress);

    /// <summary>For the tray icon: start / stop / restart the service (this process runs elevated).</summary>
    public static int ControlService(string action)
    {
        try
        {
            if (action is "stop" or "restart")
            {
                Log("Stopping the Matmon service (tray icon) ...");
                WindowsInstallation.StopService();
            }

            if (action is "start" or "restart")
            {
                Log("Starting the Matmon service (tray icon) ...");
                WindowsInstallation.StartService();
            }

            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException or System.ComponentModel.Win32Exception)
        {
            Log($"FAILED to {action} the service: {ex.Message}");
            return 1;
        }
    }

    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Runs this program again with administrator rights (one UAC prompt) and waits for it. The program itself starts
    /// without them (asInvoker) so that the tray icon, which is the same file, runs as the signed-in user.
    /// </summary>
    public static int RunElevated(string[] args)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(SelfPath, string.Join(' ', args.Select(Quote)))
            {
                UseShellExecute = true,
                Verb = "runas"
            }) ?? throw new InvalidOperationException("the setup did not start");
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return 1223; // ERROR_CANCELLED: the UAC prompt was declined
        }
    }

    /// <summary>After an install by the elevated copy: the tray icon, started from here - as the signed-in user, not elevated.</summary>
    public static void StartTrayIcon()
    {
        if (File.Exists(WindowsInstallation.SetupProgramPath) && WindowsInstallation.TrayIconRegistered())
        {
            Process.Start(new ProcessStartInfo(WindowsInstallation.SetupProgramPath, "/tray") { UseShellExecute = false });
        }
    }

    // One argument for CreateProcess' command line (the CommandLineToArgvW rules: quotes, and backslashes before them, doubled).
    private static string Quote(string argument)
    {
        if (argument.Length > 0 && !argument.Any(c => char.IsWhiteSpace(c) || c == '"'))
        {
            return argument;
        }

        var builder = new System.Text.StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            builder.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
            backslashes = 0;
            builder.Append(c);
        }

        builder.Append('\\', backslashes * 2).Append('"');
        return builder.ToString();
    }

    /// <summary>This program without the package after it - what is installed as the uninstaller.</summary>
    private static byte[] BareSetup()
    {
        using var self = File.OpenRead(SelfPath);
        var length = SetupPayload.Find(self) is { } package ? package.Offset : self.Length;
        var bytes = new byte[length];
        self.Seek(0, SeekOrigin.Begin);
        self.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>
    /// The uninstaller lives in the folder it removes, and a running program cannot delete itself: it copies itself to the
    /// temp folder and starts that copy instead. True = this process should end now.
    /// </summary>
    public static bool RelaunchOutsideSetupFolder(string[] args)
    {
        var self = Path.GetFullPath(SelfPath);
        if (!self.StartsWith(Path.GetFullPath(WindowsInstallation.SetupFolder), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var copy = Path.Combine(Path.GetTempPath(), $"Uninstall Matmon {Guid.NewGuid():N}.exe");
        File.Copy(self, copy);
        var start = new ProcessStartInfo(copy) { UseShellExecute = false };
        foreach (var arg in args.Append("/relaunched"))
        {
            start.ArgumentList.Add(arg);
        }

        Process.Start(start);
        return true;
    }

    /// <summary>A copy the uninstaller made of itself removes itself a moment after it has ended.</summary>
    public static void DeleteSelfLater()
    {
        if (!Path.GetFileName(SelfPath).StartsWith("Uninstall Matmon ", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 3 127.0.0.1 >nul & del /f /q \"{SelfPath}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    public static string LogPath => Path.Combine(Path.GetTempPath(), "matmon-setup.log");

    public static void Log(string line)
    {
        try
        {
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }
}
