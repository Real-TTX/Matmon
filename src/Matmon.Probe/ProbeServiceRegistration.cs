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
}
