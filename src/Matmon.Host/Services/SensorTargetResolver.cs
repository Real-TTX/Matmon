using Matmon.Core.Domain;

namespace Matmon.Host.Services;

public static class SensorTargetResolver
{
    public static string Resolve(SensorElement sensor, IReadOnlyList<MonitoringElement> lineage)
    {
        return Resolve(sensor.Target, lineage);
    }

    public static string Resolve(string? explicitTarget, IReadOnlyList<MonitoringElement> lineage)
    {
        var trimmedTarget = explicitTarget?.Trim();
        if (!string.IsNullOrWhiteSpace(trimmedTarget))
        {
            return trimmedTarget;
        }

        return ResolveInheritedHostAddress(lineage) ?? string.Empty;
    }

    public static string? ResolveInheritedHostAddress(IReadOnlyList<MonitoringElement> lineage)
    {
        for (var index = lineage.Count - 1; index >= 0; index--)
        {
            if (lineage[index] is HostElement host && !string.IsNullOrWhiteSpace(host.Address))
            {
                return host.Address.Trim();
            }
        }

        return null;
    }
}
