using Matmon.Core.Domain;
using Matmon.Host.Ui;
using Matmon.Probe;

namespace Matmon.Host.Services;

/// <summary>
/// Everything an agent is downloaded through (Primary only):
/// <list type="bullet">
/// <item><c>GET /api/agent/packages</c> - the manifest (version, size, hash, signature per platform) the agents' auto-update
/// and the install scripts read;</item>
/// <item><c>GET /api/agent/packages/{rid}</c> - the PLAIN package (what is hashed and signed);</item>
/// <item><c>GET /api/agent/installer/{rid}</c> - the same package with the instance address and an enrolment code appended
/// (<see cref="AgentInstallerPayload"/>): a double-click / <c>sudo ./matmon-agent install</c> needs nothing typed;</item>
/// <item><c>GET /api/agent/install.ps1</c> and <c>/install.sh</c> - the one-liner installs (<see cref="AgentInstallScripts"/>).</item>
/// </list>
/// Not anonymous: a signed-in user, a valid enrolment code in the <c>X-Matmon-Enrollment-Code</c> header (checked, NOT
/// consumed - and never a query parameter, which ends up in access logs), or an enrolled agent (probe id + token). The
/// installer additionally takes a signed-in ADMIN plus the id of a code created in this process
/// (<see cref="AgentInstallerCodes"/>) - the browser button on the Agents page, which has no header to send.
/// </summary>
public static class AgentDownloadEndpoints
{
    public const string CodeHeader = "X-Matmon-Enrollment-Code";

    public static void MapAgentDownloadEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/agent/packages", async (HttpContext http, AgentPackageStore packages, IMonitoringWorkspaceStore workspaceStore) =>
        {
            if (!IsAuthorized(http, workspaceStore))
            {
                return Results.Unauthorized();
            }

            var catalog = await packages.GetCatalogAsync(http.RequestAborted);
            NoStore(http);
            return Results.Ok(new
            {
                // Top level: what an older agent reads. The feed's version when the feed serves everything, else the
                // instance's (hand-placed packages are the instance's build). Newer agents read the package's own.
                version = catalog.Packages.Count > 0 && catalog.Packages.All(package => package.Version == catalog.Packages[0].Version)
                    ? catalog.Packages[0].Version
                    : packages.InstanceVersion,
                instanceVersion = packages.InstanceVersion,
                problem = catalog.Problem,
                packages = catalog.Packages.Select(package => new
                {
                    runtimeId = package.Platform.RuntimeId,
                    displayName = package.Platform.DisplayName,
                    fileName = package.Platform.FileName,
                    size = package.Length,
                    sha256 = package.Sha256,
                    signature = package.Signature,
                    version = package.Version,
                    url = $"/api/agent/packages/{package.Platform.RuntimeId}"
                })
            });
        }).AllowAnonymous().RequireRateLimiting("agent-enroll");

        app.MapGet("/api/agent/packages/{runtimeId}", async (string runtimeId, HttpContext http, AgentPackageStore packages, IMonitoringWorkspaceStore workspaceStore) =>
        {
            if (!IsAuthorized(http, workspaceStore))
            {
                return Results.Unauthorized();
            }

            AgentPackage package;
            try
            {
                package = await packages.GetFileAsync(runtimeId, http.RequestAborted);
            }
            catch (AgentPackageUnavailableException ex)
            {
                return Unavailable(ex);
            }

            // The hash rides as a header too, so a script (or the updater) can verify what it wrote to disk.
            http.Response.Headers["X-Matmon-Agent-Sha256"] = package.Sha256;
            if (package.Signature is not null)
            {
                http.Response.Headers["X-Matmon-Agent-Signature"] = package.Signature;
            }

            if (package.Version is not null)
            {
                http.Response.Headers["X-Matmon-Agent-Version"] = package.Version;
            }

            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return Results.File(
                package.FilePath!,
                "application/octet-stream",
                package.Platform.FileName,
                lastModified: new DateTimeOffset(package.WrittenUtc, TimeSpan.Zero),
                entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{package.Sha256}\""),
                enableRangeProcessing: true);
        }).AllowAnonymous().RequireRateLimiting("agent-enroll");

        app.MapGet("/api/agent/installer/{runtimeId}", async (string runtimeId, Guid? enrollment, HttpContext http, AgentPackageStore packages,
            IMonitoringWorkspaceStore workspaceStore, AgentInstallerCodes codes) =>
        {
            var code = ResolveInstallerCode(http, enrollment, workspaceStore, codes, out var refusal);
            if (code is null)
            {
                return refusal!;
            }

            var url = PublicLinks.Absolute(http, workspaceStore, string.Empty);
            if (AgentInstallScripts.SafeUrl(url) is not { } safeUrl)
            {
                return Text(StatusCodes.Status409Conflict,
                    "This instance does not know the address agents should use. Set the public address under System > General, or open Matmon directly once.");
            }

            AgentPackage package;
            try
            {
                package = await packages.GetFileAsync(runtimeId, http.RequestAborted);
            }
            catch (AgentPackageUnavailableException ex)
            {
                return Unavailable(ex);
            }

            var name = enrollment is { } id
                ? workspaceStore.GetPendingAgentEnrollments().FirstOrDefault(pending => pending.Id == id)?.Name
                : null;
            var suffix = AgentInstallerPayload.BuildSuffix(package.Length, new AgentInstallerSettings(safeUrl, code));
            NoStore(http);
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            http.Response.ContentLength = package.Length + suffix.Length;
            return Results.Stream(async output =>
            {
                await using (var input = File.OpenRead(package.FilePath!))
                {
                    await input.CopyToAsync(output, http.RequestAborted);
                }

                await output.WriteAsync(suffix, http.RequestAborted);
            }, "application/octet-stream", InstallerFileName(package.Platform, name));
        }).AllowAnonymous().RequireRateLimiting("agent-enroll");

        app.MapGet("/api/agent/install.ps1", (HttpContext http, AgentPackageStore packages, IMonitoringWorkspaceStore workspaceStore) =>
            ScriptAsync(http, packages, workspaceStore, windows: true)).AllowAnonymous().RequireRateLimiting("agent-enroll");

        app.MapGet("/api/agent/install.sh", (HttpContext http, AgentPackageStore packages, IMonitoringWorkspaceStore workspaceStore) =>
            ScriptAsync(http, packages, workspaceStore, windows: false)).AllowAnonymous().RequireRateLimiting("agent-enroll");
    }

    /// <summary>
    /// The address an agent installed with this enrolment talks to: the one the request came through when a remote probe
    /// relayed it, else the address set for the remote probe above the agent's place (that probe relays it), else
    /// <paramref name="fallback"/> (the instance's own). Null when none can go into a script.
    /// </summary>
    public static string? AgentUrl(HttpContext http, IMonitoringWorkspaceStore store, AgentEnrollment? enrollment, string fallback)
    {
        if (AgentRelay.ReadRelay(http.Request, store, out var origin) is not null && origin is not null)
        {
            return origin;
        }

        var relay = enrollment is null ? null : store.FindAgentRelayProbe(enrollment);
        return relay is { ParentId: not null }
            ? AgentInstallScripts.SafeUrl(relay.AgentAddress)
            : AgentInstallScripts.SafeUrl(fallback);
    }

    private static string MissingAddress(IMonitoringWorkspaceStore store, AgentEnrollment? enrollment) =>
        enrollment is not null && store.FindAgentRelayProbe(enrollment) is { ParentId: not null } probe
            ? $"The agent talks through the probe '{probe.Name}', and nobody has set the address agents use to reach it. Set it in the probe's settings (Probes > {probe.Name} > Edit)."
            : "This instance does not know the address agents should use. Set the public address under System > General, or open Matmon directly once.";

    /// <summary>
    /// The download name of a ready-to-install agent: says it installs, and for which machine when the code names one -
    /// e.g. <c>matmon-agent-setup-FILESRV-01.exe</c>, <c>matmon-agent-linux-x64-FILESRV-01</c>.
    /// </summary>
    public static string InstallerFileName(AgentPlatform platform, string? name)
    {
        var slug = new string((name ?? string.Empty).Trim().Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray()).Trim('-', '.');
        if (slug.Length > 60)
        {
            slug = slug[..60];
        }

        var suffix = slug.Length == 0 ? string.Empty : $"-{slug}";
        return platform.IsWindows ? $"matmon-agent-setup{suffix}.exe" : $"matmon-agent-{platform.RuntimeId}{suffix}";
    }

    private static async Task<IResult> ScriptAsync(HttpContext http, AgentPackageStore packages, IMonitoringWorkspaceStore workspaceStore, bool windows)
    {
        var header = http.Request.Headers[CodeHeader].ToString();
        if (string.IsNullOrWhiteSpace(header))
        {
            return Text(StatusCodes.Status401Unauthorized,
                $"Send the enrolment code in the {CodeHeader} header - copy the complete command from Probes > Agents on the instance.");
        }

        var code = AgentEnrollmentCode.Canonical(header);
        if (code is null || !workspaceStore.IsAgentEnrollmentCodeValid(code))
        {
            return Text(StatusCodes.Status400BadRequest,
                "The enrolment code is wrong, already used or expired. Create a new one under Probes > Agents on the instance.");
        }

        // The address the script was just fetched from IS reachable from the machine that runs it - through a relaying
        // probe that is the probe's, and an agent placed under a remote probe talks to that probe in any case.
        var url = AgentUrl(http, workspaceStore, workspaceStore.FindAgentEnrollmentByCode(code),
            $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}");
        if (url is null)
        {
            return Text(StatusCodes.Status400BadRequest, MissingAddress(workspaceStore, workspaceStore.FindAgentEnrollmentByCode(code)));
        }

        var catalog = await packages.GetCatalogAsync(http.RequestAborted);
        NoStore(http);
        var script = windows
            ? AgentInstallScripts.PowerShell(url, code, catalog.Find("win-x64")?.Sha256)
            : AgentInstallScripts.Shell(url, code, AgentPackageStore.Platforms.ToDictionary(
                platform => platform.RuntimeId, platform => catalog.Find(platform.RuntimeId)?.Sha256));
        return Results.Text(script, windows ? "text/plain; charset=utf-8" : "text/x-shellscript; charset=utf-8");
    }

    /// <summary>The plain code a ready-to-install download embeds - from the header, or (signed-in admin) from the code
    /// created in this process under that enrolment id. Always re-checked against the store: a used code is not handed out.</summary>
    private static string? ResolveInstallerCode(HttpContext http, Guid? enrollment, IMonitoringWorkspaceStore workspaceStore, AgentInstallerCodes codes, out IResult? refusal)
    {
        refusal = null;
        string? code = null;
        var header = http.Request.Headers[CodeHeader].ToString();
        if (!string.IsNullOrWhiteSpace(header))
        {
            code = AgentEnrollmentCode.Canonical(header);
        }
        else if (enrollment is { } id)
        {
            if (http.User.Identity?.IsAuthenticated != true)
            {
                refusal = Results.Unauthorized();
                return null;
            }

            if (!MatmonSecurity.IsAdmin(http.User))
            {
                refusal = Results.Forbid();
                return null;
            }

            code = codes.Find(id);
            if (code is null)
            {
                refusal = Text(StatusCodes.Status410Gone,
                    "This code can no longer be downloaded (it was used, revoked or has expired, or the instance restarted since it was created). Create a new one under Probes > Agents.");
                return null;
            }
        }
        else
        {
            refusal = Text(StatusCodes.Status401Unauthorized, $"Send the enrolment code in the {CodeHeader} header.");
            return null;
        }

        if (code is null || !workspaceStore.IsAgentEnrollmentCodeValid(code))
        {
            if (enrollment is { } used)
            {
                codes.Forget(used);
            }

            refusal = Text(StatusCodes.Status410Gone,
                "This download's code was already used or has expired. Create a new one under Probes > Agents.");
            return null;
        }

        return code;
    }

    internal static bool IsAuthorized(HttpContext http, IMonitoringWorkspaceStore workspaceStore)
    {
        if (http.User.Identity?.IsAuthenticated == true)
        {
            return true;
        }

        // A header, not a query parameter: a URL ends up in proxy and access logs, and while the code dies at
        // enrolment, until then it is exactly what lets a machine become a probe.
        if (http.Request.Headers.TryGetValue(CodeHeader, out var code) &&
            workspaceStore.IsAgentEnrollmentCodeValid(code.ToString()))
        {
            return true;
        }

        var token = http.Request.Headers["X-Matmon-Probe-Token"].ToString();
        if (string.IsNullOrWhiteSpace(token))
        {
            token = http.Request.Query["token"].ToString();
        }

        return http.Request.Headers.TryGetValue("X-Matmon-Probe-Id", out var probeId) &&
            workspaceStore.TryValidateProbe(probeId.ToString(), token);
    }

    private static IResult Unavailable(AgentPackageUnavailableException ex) =>
        Text(ex.NotFound ? StatusCodes.Status404NotFound : StatusCodes.Status503ServiceUnavailable, ex.Message);

    private static IResult Text(int status, string message) => Results.Text(message + "\n", "text/plain; charset=utf-8", statusCode: status);

    private static void NoStore(HttpContext http) => http.Response.Headers.CacheControl = "no-store";
}
