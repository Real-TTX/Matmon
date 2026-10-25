using Matmon.Core.Domain;

namespace Matmon.Host.Services;

/// <summary>
/// Resolves + enforces this instance's license. The signed token is fetched from Matmon.Cloud and cached
/// in the workspace; validation is fully offline (baked public key) so the monitor keeps enforcing the last
/// known license even when the cloud is unreachable. Falls back to the free Personal plan when no valid token exists.
/// </summary>
public interface ILicenseService
{
    /// <summary>The current effective license (verified; expired/invalid/missing → the free Personal plan).</summary>
    LicenseInfo Current { get; }

    /// <summary>Whether another probe may be added under the current license. Agents do not count and need no check.</summary>
    bool CanAddProbe(out string reason);

    /// <summary>Whether another sensor may be added under the current license.</summary>
    bool CanAddSensor(out string reason);
}

public sealed class LicenseService : ILicenseService
{
    private readonly IMonitoringWorkspaceStore _store;

    public LicenseService(IMonitoringWorkspaceStore store)
    {
        _store = store;
    }

    public LicenseInfo Current
    {
        get
        {
            var info = LicenseCrypto.Verify(_store.GetLicenseToken(), LicensePublicKey.Spki);
            if (info is null || info.IsExpired(DateTimeOffset.UtcNow) || !IsForThisInstance(info))
            {
                return LicenseInfo.Fallback();
            }

            return info;
        }
    }

    // A token names the cloud instance it was signed for. While this installation is linked (in the UI) to an
    // instance, a token for ANOTHER instance is not its licence - it would let a box that was relinked keep a paid plan
    // it does not hold. An unlinked box (an offline token pasted by hand) accepts the token it was given.
    private bool IsForThisInstance(LicenseInfo info)
    {
        var link = _store.GetCloudConnectionSettings();
        return string.IsNullOrWhiteSpace(info.InstanceId)
            || !link.Configured
            || !link.Enabled
            || string.IsNullOrWhiteSpace(link.InstanceId)
            || string.Equals(info.InstanceId, link.InstanceId, StringComparison.OrdinalIgnoreCase);
    }

    public bool CanAddProbe(out string reason)
    {
        var license = Current;
        if (license.IsUnlimited)
        {
            reason = string.Empty;
            return true;
        }

        if (LicenseInfo.CountLicensedProbes(_store.GetAllElements()) < license.ProbeLimit)
        {
            reason = string.Empty;
            return true;
        }

        reason = $"Probe limit reached ({license.ProbeLimit} on the {license.DisplayName} plan). " +
            "Upgrade the plan in Matmon.Cloud to add more probes - agents do not count and can still be added.";
        return false;
    }

    public bool CanAddSensor(out string reason)
    {
        var license = Current;
        if (license.IsSensorUnlimited)
        {
            reason = string.Empty;
            return true;
        }

        var sensorCount = _store.GetAllElements().OfType<SensorElement>().Count();
        if (sensorCount < license.SensorLimit)
        {
            reason = string.Empty;
            return true;
        }

        reason = $"Sensor limit reached ({license.SensorLimit} on the {license.DisplayName} plan). " +
            "Upgrade the plan in Matmon.Cloud to add more sensors.";
        return false;
    }
}
