using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;

namespace Matmon.Host.Services.SelfUpdate;

/// <summary>
/// <c>Matmon.Host.exe install-service [--port 8099] [--data &lt;folder&gt;] [--open-firewall]</c> and
/// <c>uninstall-service</c> - run once, elevated, from the unpacked Windows package.
///
/// Installs the instance the way the self-update expects it: the program in <c>%ProgramFiles%\Matmon</c> (replaced as a
/// whole on every update) and the data in <c>%ProgramData%\Matmon\data</c> (never touched by an update), a service
/// <c>Matmon</c> that starts with Windows and restarts after a crash. The port and the data folder are the service's own
/// environment (its registry key), so nothing about them lives in the program folder an update swaps.
/// </summary>
public static class WindowsServiceInstaller
{
    public const string InstallCommand = "install-service";
    public const string UninstallCommand = "uninstall-service";
    public const string ServiceName = "Matmon";

    public static int Run(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("install-service is for Windows. On Linux run Matmon with Docker.");
            return 2;
        }

        try
        {
            return args[0] == UninstallCommand ? Uninstall() : Install(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed: {ex.Message}");
            Console.Error.WriteLine("Run it from an elevated prompt (\"Run as administrator\").");
            return 1;
        }
    }

    [SupportedOSPlatform("windows")]
    private static int Install(string[] args)
    {
        var port = int.TryParse(Option(args, "--port"), out var parsed) && parsed is > 0 and < 65536 ? parsed : 8099;
        var data = Option(args, "--data")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Matmon", "data");
        var programFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Matmon");
        var executable = Path.Combine(programFolder, "Matmon.Host.exe");
        var source = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

        var existing = ServiceControl.ResolveWindowsServiceName(executable);
        if (existing is not null)
        {
            ServiceControl.Stop(existing, TimeSpan.FromSeconds(90));
        }

        if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(programFolder), StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"Copying the program to {programFolder} ...");
            WindowsSelfUpdateTarget.TryDeleteDirectory(programFolder);
            WindowsSelfUpdateTarget.CopyDirectory(source, programFolder);
        }

        Directory.CreateDirectory(data);
        SecureDataFolder(data);
        var service = existing ?? ServiceName;
        if (existing is null)
        {
            Sc("create", service, $"binPath= \"\\\"{executable}\\\"\"", "start= delayed-auto", "DisplayName= \"Matmon\"");
            Sc("description", service, "\"Matmon network monitoring - the web interface and the sensors of this instance.\"");
            Sc("failure", service, "reset= 86400", "actions= restart/10000/restart/60000/restart/60000");
        }

        using (var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{service}", writable: true)
                   ?? throw new InvalidOperationException($"the service key of '{service}' is missing"))
        {
            key.SetValue("Environment", new[]
            {
                $"ASPNETCORE_URLS=http://+:{port}",
                $"Matmon__WorkspacePath={Path.Combine(data, "workspace.json")}"
            }, RegistryValueKind.MultiString);
        }

        if (args.Contains("--open-firewall"))
        {
            Run("netsh", $"advfirewall firewall add rule name=\"Matmon (TCP {port})\" dir=in action=allow protocol=TCP localport={port}");
        }

        ServiceControl.Start(service, TimeSpan.FromSeconds(90));
        Console.WriteLine($"Matmon runs as the Windows service '{service}': http://localhost:{port}");
        Console.WriteLine($"Data: {data}");
        if (!args.Contains("--open-firewall"))
        {
            Console.WriteLine($"Other machines reach it once TCP {port} is allowed in the firewall (or run again with --open-firewall).");
        }

        return 0;
    }

    [SupportedOSPlatform("windows")]
    private static int Uninstall()
    {
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Matmon", "Matmon.Host.exe");
        var service = ServiceControl.ResolveWindowsServiceName(executable);
        if (service is null)
        {
            Console.WriteLine("No Matmon service is installed.");
            return 0;
        }

        ServiceControl.Stop(service, TimeSpan.FromSeconds(90));
        Sc("delete", service);
        Console.WriteLine($"The service '{service}' is removed. The program folder and the data were left in place.");
        return 0;
    }

    /// <summary>
    /// The data folder holds workspace.json AND the DataProtection keys that decrypt its secrets. Under ProgramData it
    /// would inherit "Users may read" - so it gets an ACL of its own: SYSTEM (the service) and Administrators, nobody else.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static void SecureDataFolder(string directory)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                inherit, PropagationFlags.None, AccessControlType.Allow));
        }

        new DirectoryInfo(directory).SetAccessControl(security);
    }

    private static void Sc(params string[] arguments) => Run("sc.exe", string.Join(' ', arguments));

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

    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
