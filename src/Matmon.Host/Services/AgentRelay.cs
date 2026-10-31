using System.Net.Http.Headers;
using Matmon.Core;

namespace Matmon.Host.Services;

/// <summary>
/// A remote probe relays the agents in its network: an agent placed under the probe is installed against the probe's
/// address and talks to it exactly as it would to the primary, and the probe passes every agent request on to its primary
/// (<see cref="UseAgentRelay"/>, Secondary mode). The agent still authenticates with its OWN id and token end to end - the
/// probe adds who it is (its id and token) and the address the agent used, which the primary believes only when that probe
/// token is valid (<see cref="ReadRelay"/>): it is shown as "via" on the Agents page and is the address an install script
/// fetched through the probe embeds. When the primary is unreachable the probe answers 502 and the agent keeps its results
/// in its own offline buffer - nothing is buffered twice.
/// </summary>
public static class AgentRelay
{
    public const string ProbeHeader = "X-Matmon-Relay-Probe";
    public const string TokenHeader = "X-Matmon-Relay-Token";
    public const string OriginHeader = "X-Matmon-Relay-Origin";
    public const string HttpClientName = "matmon-agent-relay";

    /// <summary>What an agent calls: the probe endpoints, the enrolment and the agent downloads / install scripts.</summary>
    public static bool IsAgentPath(PathString path) =>
        path.StartsWithSegments("/api/probes", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/agents/enroll", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/agent", StringComparison.OrdinalIgnoreCase);

    private static readonly HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization", "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Host"
    };

    /// <summary>Secondary mode: passes agent requests on to the primary (before the probe's own page handling).</summary>
    public static void UseAgentRelay(this WebApplication app, ProbeRuntimeOptions options)
    {
        app.Use(async (context, next) =>
        {
            if (!IsAgentPath(context.Request.Path))
            {
                await next();
                return;
            }

            if (string.IsNullOrWhiteSpace(options.PrimaryUrl) || !Uri.TryCreate(options.PrimaryUrl.TrimEnd('/') + "/", UriKind.Absolute, out var primary))
            {
                await WriteText(context, StatusCodes.Status503ServiceUnavailable, "This probe has no primary to pass the request on to.");
                return;
            }

            var factory = context.RequestServices.GetRequiredService<IHttpClientFactory>();
            using var client = factory.CreateClient(HttpClientName);
            var target = new Uri(primary, context.Request.Path.Value!.TrimStart('/') + context.Request.QueryString.Value);
            using var forward = new HttpRequestMessage(new HttpMethod(context.Request.Method), target);

            if (context.Request.ContentLength > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
            {
                forward.Content = new StreamContent(context.Request.Body);
            }

            foreach (var header in context.Request.Headers)
            {
                if (HopByHop.Contains(header.Key) || header.Key.StartsWith("X-Matmon-Relay-", StringComparison.OrdinalIgnoreCase))
                {
                    continue; // an agent cannot pretend to be a relay
                }

                if (!forward.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
                {
                    forward.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
                }
            }

            forward.Headers.TryAddWithoutValidation(ProbeHeader, options.ProbeId ?? string.Empty);
            forward.Headers.TryAddWithoutValidation(TokenHeader, options.ProbeToken ?? string.Empty);
            forward.Headers.TryAddWithoutValidation(OriginHeader, $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}");

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(forward, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !context.RequestAborted.IsCancellationRequested)
            {
                await WriteText(context, StatusCodes.Status502BadGateway, $"This probe cannot reach its primary right now ({ex.Message}).");
                return;
            }

            using (response)
            {
                context.Response.StatusCode = (int)response.StatusCode;
                foreach (var header in response.Headers.Concat(response.Content.Headers))
                {
                    if (!HopByHop.Contains(header.Key))
                    {
                        context.Response.Headers[header.Key] = header.Value.ToArray();
                    }
                }

                await using var body = await response.Content.ReadAsStreamAsync(context.RequestAborted);
                await body.CopyToAsync(context.Response.Body, context.RequestAborted);
            }
        });
    }

    /// <summary>
    /// Primary side: the remote probe that relayed this request, when it said so with a valid token of its own - else null
    /// (a request straight from the agent, or headers nobody can vouch for). <paramref name="origin"/> is the address the
    /// agent used to reach that probe.
    /// </summary>
    public static string? ReadRelay(HttpRequest request, IMonitoringWorkspaceStore store, out string? origin)
    {
        origin = null;
        var probeId = request.Headers[ProbeHeader].ToString();
        var token = request.Headers[TokenHeader].ToString();
        if (string.IsNullOrWhiteSpace(probeId) || string.IsNullOrWhiteSpace(token)
            || store.FindProbeByProbeId(probeId) is not { ParentId: not null } || !store.TryValidateProbe(probeId, token))
        {
            return null;
        }

        origin = AgentInstallScripts.SafeUrl(request.Headers[OriginHeader].ToString());
        return probeId;
    }

    private static Task WriteText(HttpContext context, int status, string text)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/plain; charset=utf-8";
        return context.Response.WriteAsync(text);
    }
}
