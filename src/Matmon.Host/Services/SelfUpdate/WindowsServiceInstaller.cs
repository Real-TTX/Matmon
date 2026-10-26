namespace Matmon.Host.Services.SelfUpdate;

/// <summary>
/// <c>Matmon.Host.exe install-service [--port 8099] [--data &lt;folder&gt;] [--dir &lt;folder&gt;] [--open-firewall]</c> and
/// <c>uninstall-service [--remove-data]</c> - the console way, run elevated from the unpacked package. The setup program
/// (MatmonSetup.exe) is the usual way; both go through <see cref="WindowsInstallation"/>, so they install the same thing.
/// </summary>
public static class WindowsServiceInstaller
{
    public const string InstallCommand = "install-service";
    public const string UninstallCommand = "uninstall-service";
    // Spelled out rather than WindowsInstallation.ServiceName: that class is Windows-only, and Program.cs reads this on every OS.
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
            if (args[0] == UninstallCommand)
            {
                WindowsInstallation.Uninstall(args.Contains("--remove-data"), Console.WriteLine);
                return 0;
            }

            var installed = WindowsInstallation.Find();
            var options = new WindowsInstallOptions(
                Option(args, "--dir") ?? installed?.ProgramFolder ?? WindowsInstallation.DefaultProgramFolder,
                Option(args, "--data") ?? installed?.DataFolder ?? WindowsInstallation.DefaultDataFolder,
                int.TryParse(Option(args, "--port"), out var port) && port is > 0 and < 65536 ? port : installed?.Port ?? 8099,
                args.Contains("--open-firewall"));
            WindowsInstallation.Install(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), options, MatmonVersion.Current,
                uninstaller: null, Console.WriteLine);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed: {ex.Message}");
            Console.Error.WriteLine("Run it from an elevated prompt (\"Run as administrator\").");
            return 1;
        }
    }

    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
