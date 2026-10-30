using System.Net;
using Matmon.Host.Pages;
using Matmon.Probe;
using Microsoft.AspNetCore.Http;

namespace Matmon.Host.Services;

/// <summary>
/// What a probe tells a program on its OWN machine (the Windows tray, <c>GET /api/probe/status</c>): the same facts as its
/// <c>/probe</c> status page, as JSON. Never the token; the primary as scheme + host only.
/// </summary>
public sealed record ProbeLocalStatus(
    string Role,
    string Version,
    string ProbeName,
    string? ProbeId,
    string? PrimaryUrl,
    bool Connected,
    string StatusMessage,
    DateTimeOffset? LastHeartbeatUtc,
    DateTimeOffset? LastResultPostUtc,
    int AssignedSensorCount,
    int BufferedObservations,
    string? Update)
{
    public static ProbeLocalStatus Build(ProbeRuntimeOptions options, SlaveProbeRuntimeSnapshot runtime, int buffered, ProbeSelfUpdateReport? update) => new(
        "probe",
        MatmonVersion.Current,
        string.IsNullOrWhiteSpace(options.ProbeName) ? "Matmon probe" : options.ProbeName!,
        options.ProbeId,
        Uri.TryCreate(options.PrimaryUrl, UriKind.Absolute, out var uri) ? $"{uri.Scheme}://{uri.Authority}" : null,
        runtime.IsConnected,
        runtime.StatusMessage,
        runtime.LastHeartbeatUtc,
        runtime.LastResultPostUtc,
        runtime.AssignedSensorCount,
        buffered,
        DescribeUpdate(update));

    /// <summary>The update line: installing, how the last update ended, or how it is updated at all.</summary>
    public static string? DescribeUpdate(ProbeSelfUpdateReport? report) => report is null ? null
        : report.Installing ? $"installing {report.InstallingTo}"
        : ProbeUpdateView.DescribeOutcome(report) is { } outcome ? outcome
        : report.CanUpdate ? "can be updated from its primary"
        : $"updated by hand ({report.UnavailableReason})";

    /// <summary>
    /// Only a program on this machine: a loopback connection that did not come through a proxy. The forwarded-headers
    /// middleware believes X-Forwarded-For from private addresses, so a header claiming 127.0.0.1 could otherwise pass;
    /// a request that carried one (or whose original value the middleware kept) is refused.
    /// </summary>
    public static bool IsLocalCaller(HttpContext context) =>
        context.Connection.RemoteIpAddress is { } address && IPAddress.IsLoopback(address)
        && !context.Request.Headers.ContainsKey("X-Forwarded-For")
        && !context.Request.Headers.ContainsKey("X-Original-For");
}
