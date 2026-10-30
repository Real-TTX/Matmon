namespace Matmon.Host.Services;

public interface IConfigurationOverviewProvider
{
    ConfigurationOverview GetOverview();

    /// <summary>The probe rows alone - what the cloud heartbeat sends every beat, without the storage scan the full overview does.</summary>
    IReadOnlyList<SystemProbeOverview> GetProbes() => GetOverview().Probes;
}
