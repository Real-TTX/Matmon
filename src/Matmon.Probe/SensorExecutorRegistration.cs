using Matmon.Core.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Matmon.Probe;

/// <summary>
/// The one registration list for every sensor that lives in <c>Matmon.Core</c>. It used to be the body of a
/// local function in the Host's Program.cs, which is exactly where a second host - Matmon.Agent - cannot reach
/// it; an agent with its own copy of this list would sooner or later offer a different set of sensors than
/// the primary assigning work to it.
///
/// Deliberately NOT in here: the executors that exist only inside the Host (Mail Health, Matmon Update, Probe
/// Health) and the probe heartbeat, which the Host registers itself on top of this list, gated as before.
/// </summary>
public static class SensorExecutorRegistration
{
    public static IServiceCollection AddMatmonSensorExecutors(this IServiceCollection services)
    {
        services.AddTransient<ISensorExecutor, PingSensorExecutor>();
        services.AddHttpClient<HttpSensorExecutor>();
        services.AddTransient<ISensorExecutor>(sp => sp.GetRequiredService<HttpSensorExecutor>());
        services.AddHttpClient<HttpAdvancedSensorExecutor>();
        services.AddTransient<ISensorExecutor>(sp => sp.GetRequiredService<HttpAdvancedSensorExecutor>());
        services.AddTransient<ISensorExecutor, SnmpSensorExecutor>();
        services.AddTransient<ISensorExecutor, SynologyNasSensorExecutor>();
        services.AddTransient<ISensorExecutor, SynologyHealthSensorExecutor>();
        services.AddTransient<ISensorExecutor, SynologyDiskSensorExecutor>();
        services.AddTransient<ISensorExecutor, SynologyUpdateSensorExecutor>();
        services.AddTransient<ISensorExecutor, SnmpInterfaceSensorExecutor>();
        services.AddTransient<ISensorExecutor, UpsSnmpSensorExecutor>();
        // NB: ProxmoxPveSensorExecutor is intentionally NOT registered as a selectable type - the legacy
        // scope-based "proxmox" type was retired in favour of proxmox-health / proxmox-node-health, which
        // instantiate it directly as the shared REST/auth engine.
        services.AddTransient<ISensorExecutor, ProxmoxHealthSensorExecutor>();
        services.AddTransient<ISensorExecutor, ProxmoxNodeHealthSensorExecutor>();
        services.AddTransient<ISensorExecutor, ProxmoxDiskSensorExecutor>();
        services.AddTransient<ISensorExecutor, VMwareHealthSensorExecutor>();
        services.AddTransient<ISensorExecutor, VMwareHostHealthSensorExecutor>();
        services.AddTransient<ISensorExecutor, UnifiHealthSensorExecutor>();
        services.AddTransient<ISensorExecutor, PowerShellRemoteSensorExecutor>();
        services.AddTransient<ISensorExecutor, LocalScriptSensorExecutor>();
        services.AddTransient<ISensorExecutor, LocalProgramSensorExecutor>();
        services.AddTransient<ISensorExecutor, WindowsHealthSensorExecutor>();
        services.AddTransient<ISensorExecutor, WindowsDiskSensorExecutor>();
        services.AddTransient<ISensorExecutor, WindowsUpdateSensorExecutor>();
        services.AddTransient<ISensorExecutor, WindowsServiceSensorExecutor>();
        services.AddTransient<ISensorExecutor, WindowsProcessSensorExecutor>();
        services.AddTransient<ISensorExecutor, LinuxSshHealthSensorExecutor>();
        services.AddTransient<ISensorExecutor, LinuxDiskSensorExecutor>();
        services.AddTransient<ISensorExecutor, LinuxUpdateSensorExecutor>();
        services.AddTransient<ISensorExecutor, SslCertificateSensorExecutor>();
        services.AddTransient<ISensorExecutor, CertificateChainSensorExecutor>();
        services.AddTransient<ISensorExecutor, MssqlSensorExecutor>();
        services.AddTransient<ISensorExecutor, PostgreSqlSensorExecutor>();
        services.AddTransient<ISensorExecutor, MySqlSensorExecutor>();
        services.AddTransient<ISensorExecutor, TcpPortSensorExecutor>();
        services.AddTransient<ISensorExecutor, DnsSensorExecutor>();
        services.AddTransient<ISensorExecutor, NtpSensorExecutor>();
        services.AddTransient<ISensorExecutor, DockerContainerSensorExecutor>();
        services.AddTransient<ISensorExecutor, WindowsEventLogSensorExecutor>();
        return services;
    }
}
