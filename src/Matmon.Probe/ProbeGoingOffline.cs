using Matmon.Core;
using Microsoft.Extensions.Logging;

namespace Matmon.Probe;

/// <summary>
/// Tells the primary that this probe / agent is going away right now - its service stopping, the machine shutting down or
/// going to sleep - so an agent host that may go offline (a notebook) shows "offline" at once instead of after three missed
/// heartbeats. Best effort with a short timeout: a machine on its way down must not be held up, and a notice that does not
/// arrive costs only those three heartbeats. The next heartbeat clears it.
/// </summary>
public sealed class ProbeGoingOffline(IHttpClientFactory httpClientFactory, ProbeRuntimeOptions options, ILogger<ProbeGoingOffline> logger)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    public async Task SendAsync(TimeSpan timeout, string reason)
    {
        if (options.Mode != AppMode.Secondary || !Uri.TryCreate(options.PrimaryUrl, UriKind.Absolute, out var primary))
        {
            return;
        }

        var probeId = string.IsNullOrWhiteSpace(options.ProbeId) ? Environment.MachineName : options.ProbeId;
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            using var client = httpClientFactory.CreateClient("SecondaryHeartbeatService");
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(primary, $"/api/probes/{Uri.EscapeDataString(probeId)}/going-offline"));
            if (!string.IsNullOrWhiteSpace(options.ProbeToken))
            {
                request.Headers.TryAddWithoutValidation("X-Matmon-Probe-Token", options.ProbeToken);
            }

            using var response = await client.SendAsync(request, cts.Token);
            logger.LogInformation("Told the primary this machine is going offline ({Reason}): {Status}", reason, (int)response.StatusCode);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not tell the primary this machine is going offline ({Reason})", reason);
        }
    }
}
