#if WINDOWS
using System.ServiceProcess;
using Matmon.Probe;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Matmon.Agent;

/// <summary>
/// The Windows service lifetime plus the machine's power events: before a notebook goes to sleep the agent tells its primary
/// it is going offline (so a host that may go offline shows it at once, not after three missed heartbeats), and after it wakes
/// up it beats at once. A shutdown is covered by the service stopping (<see cref="SlaveHeartbeatService.StopAsync"/>).
/// </summary>
public sealed class AgentServiceLifetime : WindowsServiceLifetime
{
    private readonly ProbeGoingOffline _goingOffline;
    private readonly SlaveProbeRuntimeState _state;

    public AgentServiceLifetime(
        IHostEnvironment environment,
        IHostApplicationLifetime applicationLifetime,
        ILoggerFactory loggerFactory,
        IOptions<HostOptions> optionsAccessor,
        IOptions<WindowsServiceLifetimeOptions> windowsServiceOptionsAccessor,
        ProbeGoingOffline goingOffline,
        SlaveProbeRuntimeState state)
        : base(environment, applicationLifetime, loggerFactory, optionsAccessor, windowsServiceOptionsAccessor)
    {
        _goingOffline = goingOffline;
        _state = state;
        CanHandlePowerEvent = true;
    }

    protected override bool OnPowerEvent(PowerBroadcastStatus powerStatus)
    {
        switch (powerStatus)
        {
            case PowerBroadcastStatus.Suspend:
                // Windows gives a suspending service a moment; the notice waits at most its own short timeout.
                _goingOffline.SendAsync(ProbeGoingOffline.DefaultTimeout, "going to sleep").GetAwaiter().GetResult();
                break;
            case PowerBroadcastStatus.ResumeSuspend:
            case PowerBroadcastStatus.ResumeAutomatic:
                _state.RequestHeartbeat();
                break;
        }

        return base.OnPowerEvent(powerStatus);
    }
}
#endif
