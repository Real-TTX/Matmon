using Matmon.Core;
using Matmon.Host.Services;
using Matmon.Probe;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matmon.Host.Pages;

/// <summary>
/// What a remote (Docker) probe shows in a browser. A probe has no users and no setup - it is configured and operated
/// from its primary - so its old sign-in page was a dead end nobody could pass (and its "recovery file" hint did not
/// apply: that runs on a primary only). Instead every page of a probe leads here: what it is, whom it reports to,
/// whether that works, how much it runs and holds back, and a link to the primary. Anonymous by design, read-only,
/// and it never shows the probe token. Only served in Secondary mode (404 elsewhere).
/// </summary>
[AllowAnonymous]
public sealed class ProbeStatusModel(MatmonRuntimeOptions options, SlaveProbeRuntimeState runtime, IProbeSelfUpdate? selfUpdate = null) : PageModel
{
    public SlaveProbeRuntimeSnapshot State { get; private set; } = null!;

    public string ProbeName => string.IsNullOrWhiteSpace(options.ProbeName) ? "Matmon probe" : options.ProbeName!;

    public string? PrimaryUrl => Uri.TryCreate(options.PrimaryUrl, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) : null;

    public int Buffered { get; private set; }

    public string Version => MatmonVersion.Current;

    /// <summary>The update line: installing, how the last update ended, or whether the primary can update it at all.</summary>
    public string? UpdateText => selfUpdate?.Report() is not { } report ? null
        : report.Installing ? $"installing {report.InstallingTo}"
        : ProbeUpdateView.DescribeOutcome(report) is { } outcome ? outcome
        : report.CanUpdate ? "can be updated from its primary"
        : $"updated by hand ({report.UnavailableReason})";

    /// <summary>"ok" / "warning" / "error" for the state line.</summary>
    public string Tone => State.IsConnected ? "ok" : State.LastHeartbeatUtc is null ? "warning" : "error";

    public string StateText => State.IsConnected
        ? "Connected to its primary"
        : State.LastHeartbeatUtc is null ? "Not connected yet" : "Primary unreachable";

    public IActionResult OnGet()
    {
        if (options.Mode != AppMode.Secondary)
        {
            return NotFound();
        }

        State = runtime.Snapshot();
        Buffered = runtime.BufferedObservations;
        return Page();
    }

    /// <summary>"12 s ago" / "5 min ago" / "3 h ago" / a date.</summary>
    public static string Ago(DateTimeOffset? utc)
    {
        if (utc is not { } value)
        {
            return "never";
        }

        var age = DateTimeOffset.UtcNow - value;
        return age.TotalSeconds < 60 ? $"{Math.Max(0, (int)age.TotalSeconds)} s ago"
            : age.TotalMinutes < 60 ? $"{(int)age.TotalMinutes} min ago"
            : age.TotalHours < 48 ? $"{(int)age.TotalHours} h ago"
            : value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }
}
