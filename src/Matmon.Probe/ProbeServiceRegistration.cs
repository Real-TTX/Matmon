using Matmon.Core.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Matmon.Probe;

/// <summary>
/// Wires up a working probe: the heartbeat and the sensor worker (assignments, on-demand run jobs, discovery
/// jobs, the offline buffer) plus the two singletons they share. One list, used by both hosts - the Docker
/// probe (Matmon.Host in Secondary mode) and Matmon.Agent - for the same reason the executor list is shared:
/// two copies drift, and a drifted agent quietly stops doing half of what a probe does.
///
/// The singletons use TryAdd because the Host registers them in EVERY mode (the primary's Discovery page and
/// Probes page read them too) before it knows whether it will call this; registering them twice would give
/// the heartbeat and the worker two different runtime-state objects.
/// </summary>
public static class ProbeServiceRegistration
{
    public static IServiceCollection AddMatmonProbe(this IServiceCollection services)
    {
        services.AddHttpClient();
        services.TryAddSingleton<SlaveProbeRuntimeState>();
        services.TryAddSingleton<NetworkDiscoveryService>();
        services.AddHostedService<SlaveHeartbeatService>();
        services.AddHostedService<SlaveSensorWorker>();
        return services;
    }

    /// <summary>
    /// The sensors about the machine the probe itself runs on: Probe Health (its connection to its primary and
    /// the storage it runs on) and System Health (local) (CPU / memory / disks read straight from the OS). Kept
    /// out of AddMatmonSensorExecutors on purpose - the stateless cloud Executor must not offer them (it would
    /// report on its own container) and Probe Health needs probe infrastructure (the runtime state and an
    /// <see cref="IProbeStorageSource"/>) - and out of <see cref="AddMatmonProbe"/> because a primary runs them
    /// too without being a probe.
    /// The caller registers the <see cref="IProbeStorageSource"/>: the Host's is its data directory, the
    /// agent's is its own. SlaveProbeRuntimeState comes from AddMatmonProbe (or the Host, which registers it in every mode).
    /// </summary>
    public static IServiceCollection AddMatmonProbeLocalSensors(this IServiceCollection services)
    {
        services.AddTransient<ProbeHealthSensorExecutor>();
        services.AddTransient<ISensorExecutor>(sp => sp.GetRequiredService<ProbeHealthSensorExecutor>());
        services.AddTransient<ISensorExecutor, LocalHealthSensorExecutor>();
        return services;
    }
}
