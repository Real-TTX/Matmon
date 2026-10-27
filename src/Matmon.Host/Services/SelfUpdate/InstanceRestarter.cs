using Matmon.Probe;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace Matmon.Host.Services.SelfUpdate;

/// <summary>How this installation can restart itself.</summary>
public enum RestartMethod
{
    /// <summary>Nothing would start it again (a plain <c>dotnet run</c>) - restart it by hand.</summary>
    None,

    /// <summary>A Windows service: a detached helper stops and starts it.</summary>
    WindowsService,

    /// <summary>A container with the Docker socket: the engine restarts it.</summary>
    Docker,

    /// <summary>A container without the socket, or a systemd unit: the process ends with an error code and its restart
    /// policy / service manager is trusted to start it again.</summary>
    ExitAndRestartPolicy
}

/// <summary>
/// "Restart" on System > Updates (Primary only): restarts this installation the way it runs - the same split as the
/// self-update. The restart happens a moment after the request, so the browser gets its answer (the restart screen) first.
/// </summary>
public sealed class InstanceRestarter(IEnumerable<ISelfUpdateTarget> targets, IHostApplicationLifetime lifetime, ILogger<InstanceRestarter> logger)
{
    public const string WindowsCommand = "restart-service";

    /// <summary>New with every start of the process (random - it says nothing about the uptime), reported by /healthz: the
    /// restart screen tells a restart that was quicker than its poll from one that has not happened yet.</summary>
    public static readonly string ProcessRun = Guid.NewGuid().ToString("N")[..12];

    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(1.5);

    public RestartMethod Method
    {
        get
        {
            if (OperatingSystem.IsWindows() && WindowsServiceHelpers.IsWindowsService())
            {
                return RestartMethod.WindowsService;
            }
            if (targets.OfType<DockerSelfUpdateTarget>().FirstOrDefault() is { } docker && docker.IsAvailable(out _))
            {
                return RestartMethod.Docker;
            }
            return File.Exists("/.dockerenv") || ServiceControl.IsRunningAsService()
                ? RestartMethod.ExitAndRestartPolicy
                : RestartMethod.None;
        }
    }

    /// <summary>Starts the restart; null = on its way, else why it cannot.</summary>
    public string? Restart()
    {
        var method = Method;
        if (method == RestartMethod.None)
        {
            return "nothing would start Matmon again here (it does not run as a service or in a container) - restart it by hand";
        }

        logger.LogWarning("Restart requested ({Method})", method);
        _ = Task.Run(async () =>
        {
            await Task.Delay(Delay);
            try
            {
                switch (method)
                {
                    case RestartMethod.WindowsService:
                        var service = (OperatingSystem.IsWindows() ? ServiceControl.ResolveWindowsServiceName(Environment.ProcessPath) : null)
                            ?? WindowsServiceInstaller.ServiceName;
                        ServiceControl.LaunchDetached(Environment.ProcessPath!, [WindowsCommand, service]);
                        break;
                    case RestartMethod.Docker:
                        targets.OfType<DockerSelfUpdateTarget>().First().RestartOwnContainer();
                        break;
                    default:
                        // Non-zero, so a service manager that restarts only on failure does it too.
                        Environment.ExitCode = 3;
                        lifetime.StopApplication();
                        break;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The restart could not be started");
            }
        });
        return null;
    }

    /// <summary><c>Matmon.Host restart-service &lt;name&gt;</c>: the detached helper - stops the service and starts it again.</summary>
    public static int RunWindowsCommand(string[] args)
    {
        var service = args.Length > 1 ? args[1] : WindowsServiceInstaller.ServiceName;
        try
        {
            ServiceControl.Stop(service, TimeSpan.FromMinutes(1));
            ServiceControl.Start(service, TimeSpan.FromMinutes(1));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Restarting the service '{service}' failed: {ex.Message}");
            return 1;
        }
    }
}
