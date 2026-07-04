using Matmon.Core.Domain;

namespace Matmon.Host.Services;

/// <summary>
/// Resolves + enforces this instance's license. The signed token is fetched from Matmon.Cloud and cached
/// in the workspace; validation is fully offline (baked public key) so the monitor keeps enforcing the last
/// known license even when the cloud is unreachable. Falls back to Free/limited when no valid token exists.
/// </summary>
public interface ILicenseService
{
    /// <summary>The current effective license (verified; expired/invalid/missing → Free fallback).</summary>
    LicenseInfo Current { get; }

    /// <summary>Whether another probe may be added under the current license.</summary>
    bool CanAddProbe(out string reason);
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
            if (info is null || info.IsExpired(DateTimeOffset.UtcNow))
            {
                return LicenseInfo.Fallback();
            }

            return info;
        }
    }

    public bool CanAddProbe(out string reason)
    {
        var license = Current;
        if (license.IsUnlimited)
        {
            reason = string.Empty;
            return true;
        }

        var probeCount = _store.GetAllElements().OfType<ProbeElement>().Count();
        if (probeCount < license.ProbeLimit)
        {
            reason = string.Empty;
            return true;
        }

        reason = $"Probe limit reached ({license.ProbeLimit} on the {license.Tier} plan). " +
            "Upgrade the plan in Matmon.Cloud to add more probes.";
        return false;
    }
}
