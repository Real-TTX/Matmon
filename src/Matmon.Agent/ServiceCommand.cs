using Matmon.Probe;

namespace Matmon.Agent;

/// <summary>
/// <c>matmon-agent.exe service start|stop|restart</c> - Windows only, elevated: what the tray's "Start service …", "Stop service …"
/// and "Restart service …" run (one UAC prompt each), because the tray itself runs as the signed-in user and never with
/// administrator rights (AGENT-04). It waits until the service got where it was sent (a minute at most); exit code 0 when it did.
/// </summary>
public static class ServiceCommand
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>The action of <c>service &lt;action&gt;</c>, or null when the arguments are not that.</summary>
    public static string? ParseAction(string[] args) =>
        args is ["service", var action] && action is "start" or "stop" or "restart" ? action : null;

    /// <summary>"started" / "stopped" / "restarted", for a sentence about the outcome.</summary>
    public static string Past(string action) => action switch
    {
        "start" => "started",
        "stop" => "stopped",
        _ => "restarted"
    };

    public static int Run(string[] args)
    {
        if (ParseAction(args) is not { } action)
        {
            Console.Error.WriteLine("Usage: matmon-agent service start|stop|restart");
            return 64;
        }

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("service is for Windows. On Linux use 'systemctl <action> matmon-agent'.");
            return 2;
        }

        var service = SetupCommand.InstalledServiceName() ?? SetupCommand.ServiceName;
        try
        {
            if (action is "stop" or "restart")
            {
                ServiceControl.Stop(service, Timeout);
            }

            if (action is "start" or "restart")
            {
                ServiceControl.Start(service, Timeout);
            }

            Console.WriteLine($"The \"{service}\" service was {Past(action)}.");
            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine($"The \"{service}\" service could not be {Past(action)}: {ex.Message}");
            return 1;
        }
    }
}
