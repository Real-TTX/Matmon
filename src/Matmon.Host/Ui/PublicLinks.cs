using Matmon.Host.Services;

namespace Matmon.Host.Ui;

/// <summary>
/// The full address of a public page (a wallboard, a status page) - what is copied, shown and put into a QR code.
/// Opened directly, the request says where the instance lives. Through the Matmon.Cloud console it does not: the
/// request arrives from the tunnel with the instance's INTERNAL host, and a path alone is useless to copy (it used to
/// copy "/Maps/Public/..." without host or scheme). So the address is, in this order: the one an admin set under
/// System > General, the request's own when opened directly (remembered for the console), the one remembered.
/// </summary>
public static class PublicLinks
{
    /// <summary>True when the page was reached through the cloud console / tunnel.</summary>
    public static bool IsTunneled(HttpContext http) =>
        string.Equals(http.Request.Headers["X-Matmon-Embed"].ToString(), "1", StringComparison.Ordinal)
        || http.Request.Headers.ContainsKey("X-Matmon-Tunnel-Auth");

    /// <summary>The absolute address of <paramref name="path"/> (root-relative), or null when it cannot be known here.</summary>
    public static string? Absolute(HttpContext http, IMonitoringWorkspaceStore store, string path)
    {
        var (configured, remembered) = store.GetPublicAddress();
        if (configured is not null)
        {
            return configured + path;
        }

        if (!IsTunneled(http))
        {
            var origin = $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}";
            store.RememberDirectOrigin(origin);
            return origin + path;
        }

        return remembered is null ? null : remembered + path;
    }
}
