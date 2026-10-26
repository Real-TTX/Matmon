using Matmon.Host.Services.SelfUpdate;

namespace Matmon.Setup;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var arguments = SetupArguments.Parse(args);
        if (arguments.Tray)
        {
            return TrayApp.Run();
        }

        // The program starts without administrator rights (the tray icon is this same file): everything else runs in an
        // elevated copy. Started from here, the tray icon after an install runs as the signed-in user, not elevated.
        if (arguments.NeedsElevation && !SetupRunner.IsAdministrator())
        {
            var exitCode = SetupRunner.RunElevated(args);
            if (exitCode == 0 && !arguments.Uninstall && arguments.ServiceAction is null)
            {
                SetupRunner.StartTrayIcon();
            }

            return exitCode;
        }

        if (arguments.ServiceAction is { } action)
        {
            return SetupRunner.ControlService(action);
        }

        if (arguments.Uninstall && !arguments.Relaunched && SetupRunner.RelaunchOutsideSetupFolder(args))
        {
            return 0;
        }

        try
        {
            return arguments.Silent ? RunSilent(arguments) : RunWizard(arguments);
        }
        finally
        {
            if (arguments.Uninstall)
            {
                SetupRunner.DeleteSelfLater();
            }
        }
    }

    private static int RunWizard(SetupArguments arguments)
    {
        ApplicationConfiguration.Initialize();
        using var form = new SetupForm(arguments);
        Application.Run(form);
        return form.ExitCode;
    }

    // For scripts and software distribution: no window, the outcome in the exit code and %TEMP%\matmon-setup.log.
    private static int RunSilent(SetupArguments arguments)
    {
        try
        {
            if (arguments.Uninstall)
            {
                SetupRunner.Uninstall(arguments.RemoveData, SetupRunner.Log);
                return 0;
            }

            if (SetupRunner.SourceOf(arguments) == PackageSource.File && !File.Exists(arguments.PackagePath))
            {
                SetupRunner.Log($"The package {arguments.PackagePath} does not exist.");
                return 2;
            }

            var installed = WindowsInstallation.Find();
            var options = new WindowsInstallOptions(
                arguments.ProgramFolder ?? installed?.ProgramFolder ?? WindowsInstallation.DefaultProgramFolder,
                arguments.DataFolder ?? installed?.DataFolder ?? WindowsInstallation.DefaultDataFolder,
                arguments.Port ?? installed?.Port ?? 8099,
                arguments.OpenFirewall,
                !arguments.NoTray);
            // The online setup logs its download every 10 % - enough to see that it moves, not a line per percent.
            var logged = -1;
            SetupRunner.Install(arguments, options, SetupRunner.Log, share =>
            {
                var tenth = (int)(share * 10);
                if (tenth != logged)
                {
                    logged = tenth;
                    SetupRunner.Log($"Downloaded {tenth * 10} %");
                }
            });
            return 0;
        }
        catch (Exception ex)
        {
            SetupRunner.Log($"FAILED: {ex.Message}");
            return 1;
        }
    }
}
