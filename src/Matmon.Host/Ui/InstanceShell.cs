using Matmon.Core;
using Matmon.Core.Domain;
using Matmon.Host.Services;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace Matmon.Host.Ui;

/// <summary>An item of the menu (SHELL-02): an icon and a label; <see cref="BadgeKey"/> names the live count the
/// dashboard poll keeps up to date on it (SHELL-14).</summary>
public sealed record InstanceNavItem(
    string Title,
    string Page,
    string Icon,
    bool Active,
    string? BadgeKey = null,
    int BadgeCount = 0,
    string? BadgeTone = null,
    string? RouteValue = null);

/// <summary>A group of menu items, with an optional small heading.</summary>
public sealed record InstanceNavGroup(string? Title, IReadOnlyList<InstanceNavItem> Items);

/// <summary>
/// Everything the layout needs to draw the MatUi shell of a Matmon instance for one request: the brand (Matmon or a
/// managing partner's white label), the menu with its live counts, the user and the version. Built by
/// <see cref="InstanceShellBuilder"/>; the layout only draws it.
/// </summary>
public sealed class InstanceShell
{
    public bool Authed { get; init; }

    /// <summary>Rendered inside the Matmon.Cloud console (the tunnel's embed route): no menu, no header bar - the cloud
    /// draws one menu around the frame.</summary>
    public bool Embedded { get; init; }

    public bool Secondary { get; init; }

    public bool IsAdmin { get; init; }

    /// <summary>"Matmon", or the partner's product name under a white label.</summary>
    public string BrandName { get; init; } = "Matmon";

    /// <summary>The line under the name in the menu header.</summary>
    public string Tagline { get; init; } = "Lightweight monitoring";

    /// <summary>The partner's logo replaces the Matmon mark (white label).</summary>
    public bool PartnerLogo { get; init; }

    /// <summary>The partner's logo is a complete lockup with the name in it: the name is then for screen readers only.</summary>
    public bool PartnerLogoOnly { get; init; }

    /// <summary>The partner chose the logo above the name (stacked) rather than beside it.</summary>
    public bool BrandStacked { get; init; }

    public bool PartnerFavicon { get; init; }

    /// <summary>The partner's accent (re-validated #RRGGBB) - MatUi derives hover, tints, links and the focus ring from it.</summary>
    public string? PartnerAccent { get; init; }

    /// <summary>"Managed by …" under a co-brand without a product name.</summary>
    public string? ManagedBy { get; init; }

    /// <summary>A white label keeps a quiet "powered by Matmon" under the version.</summary>
    public bool PoweredBy { get; init; }

    public IReadOnlyList<InstanceNavGroup> Menu { get; init; } = [];

    /// <summary>The alert status at the top of the menu - it IS the menu's entry to the Alerts page (null on a secondary).</summary>
    public NavInfo? Info { get; init; }

    public string UserName { get; init; } = "Account";

    public string UserRole { get; init; } = string.Empty;

    public string Version { get; init; } = Matmon.Probe.MatmonVersion.Current;

    public bool UpdateAvailable { get; init; }

    public string? LatestVersion { get; init; }

    /// <summary>The menu the cloud console mirrors (label + path), published into the embedded page.</summary>
    public IReadOnlyList<object> EmbedNav { get; init; } = [];
}

/// <summary>Builds the <see cref="InstanceShell"/> of a request - the logic the layout used to carry inline.</summary>
public sealed class InstanceShellBuilder(
    MatmonRuntimeOptions runtimeOptions,
    IDashboardSnapshotProvider dashboard,
    CloudUpdateState cloudUpdate,
    IMonitoringWorkspaceStore store)
{
    public InstanceShell Build(HttpContext http, ViewDataDictionary viewData)
    {
        var user = http.User;
        var authed = user.Identity?.IsAuthenticated == true;
        var embedded = string.Equals(http.Request.Headers["X-Matmon-Embed"].ToString(), "1", StringComparison.Ordinal);
        var secondary = runtimeOptions.Mode == AppMode.Secondary;
        var isAdmin = user.IsInRole(MatmonUserRole.Admin.ToString());

        // The menu stays lit on every sub-page of its section (editors, tabs): matched by path prefix, most specific
        // first - /monitoring is shared by Sensors, the template editor and the probe-usage page.
        var path = http.Request.Path.Value ?? string.Empty;
        bool Starts(string prefix) => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        var dashboardActive = string.Equals(path, "/", StringComparison.OrdinalIgnoreCase);
        // Agents are probes (a probe installed on the machine it monitors), so they live under Probes; the process log is
        // a System tab. Neither has a menu item of its own.
        var probesActive = Starts("/probes") || Starts("/agents") || Starts("/system/probes") || Starts("/monitoring/probe");
        var templatesActive = Starts("/templates") || Starts("/monitoring/template");
        var alertsActive = Starts("/alerts");
        var systemActive = (Starts("/config") || Starts("/system") || Starts("/backup") || Starts("/logs")) && !probesActive;
        var sensorsActive = (Starts("/monitoring") || Starts("/paused")) && !templatesActive && !probesActive;

        WorkspaceSummary? summary = null;
        if (authed && !secondary)
        {
            try { summary = dashboard.GetWorkspaceSummary(); }
            catch { summary = null; }
        }

        // The alert status at the top of the menu agrees with the Alerts page: the big number is the open
        // (unacknowledged) alerts, Err/Warn are the active alerts by severity (they outlive the sensor's recovery,
        // Alerta-style), Ack the acknowledged ones, Pause the paused sensors. dashboard.js keeps it current.
        var open = summary?.OpenAlertCount ?? 0;
        var errors = summary?.ErrorAlertCount ?? 0;
        var warnings = summary?.WarningAlertCount ?? 0;
        var acknowledged = summary?.AcknowledgedAlertCount ?? 0;
        var paused = summary?.PausedSensorCount ?? 0;
        NavInfo? info = null;
        if (authed && !secondary)
        {
            var (tone, state) = AlertStatus(errors, warnings, acknowledged, paused);
            info = new NavInfo("alerts", "Alerts", "bell", "/Alerts?alertFilter=open", open, tone, state, alertsActive,
            [
                new("error", "Err", errors, "/Alerts?alertFilter=error", "error", "Active error alerts"),
                new("warning", "Warn", warnings, "/Alerts?alertFilter=warning", "warn", "Active warning alerts"),
                new("ack", "Ack", acknowledged, "/Alerts?alertFilter=ack", "info", "Acknowledged alerts"),
                new("paused", "Pause", paused, "/Paused", "plain", "Paused sensors")
            ],
            Description: "Open alerts");
        }

        var menu = new List<InstanceNavGroup>();
        if (authed)
        {
            if (secondary)
            {
                menu.Add(new InstanceNavGroup(null, [new("Probe", "/Index", "server", dashboardActive)]));
            }
            else
            {
                // What the menu is for: the first group is what an operator looks at every day - the state
                // (the alert status above it IS the way to Alerts, so there is no Alerts item), the things being watched,
                // the boards, the history; then what is set up once and touched now and then; System last, the
                // instance's own administration (users, backup, cloud, licence, plugins, the process log), admins only.
                // ONE list without group headings (decided 2026-10-27 - a "Setup" heading over five items was noise).
                // Actions are not places: adding a sensor, a host or an agent are buttons on the page they belong to.
                menu.Add(new InstanceNavGroup(null,
                [
                    new("Dashboard", "/Index", "dashboard", dashboardActive),
                    new("Sensors", "/Monitoring", "activity", sensorsActive),
                    new("Maps", "/Maps", "globe", Starts("/maps")),
                    new("Events", "/Events", "list", Starts("/events")),
                    new("Probes", "/Probes", "server", probesActive),
                    new("Discovery", "/Discovery", "compass", Starts("/discovery")),
                    new("Templates", "/Templates", "template", templatesActive),
                    new("Notifications", "/Notifications", "mail", Starts("/notifications")),
                    new("Maintenance", "/Maintenance", "wrench", Starts("/maintenance")),
                    .. (isAdmin ? [new InstanceNavItem("System", "/Config", "settings", systemActive)] : Array.Empty<InstanceNavItem>())
                ]));
            }
        }

        // The menu the Matmon.Cloud console mirrors (role- and mode-aware), so the cloud's menu is exactly this one -
        // one flat list, as in the layout.
        var embedNav = new List<object>();
        if (embedded)
        {
            if (secondary)
            {
                embedNav.Add(new { label = "Probe", path = "" });
            }
            else
            {
                embedNav.Add(new { label = "Dashboard", path = "" });
                embedNav.Add(new { label = "Alerts", path = "Alerts" });
                embedNav.Add(new { label = "Sensors", path = "Monitoring" });
                embedNav.Add(new { label = "Maps", path = "Maps" });
                embedNav.Add(new { label = "Events", path = "Events" });
                embedNav.Add(new { label = "Probes", path = "Probes" });
                embedNav.Add(new { label = "Discovery", path = "Discovery" });
                embedNav.Add(new { label = "Templates", path = "Templates" });
                embedNav.Add(new { label = "Notifications", path = "Notifications" });
                embedNav.Add(new { label = "Maintenance", path = "Maintenance" });
                if (isAdmin)
                {
                    embedNav.Add(new { label = "System", path = "Config" });
                }
            }
        }

        // White label / co-brand from the managing partner (cheap accessors; every value re-validated where it is a
        // colour). A product name replaces "Matmon"; without one, the partner shows as "Managed by …".
        var productName = store.GetServicePartnerProductName();
        var partnerName = store.GetServicePartnerName();
        var hasLogo = store.GetServicePartnerHasLogo();
        var whiteLabel = !string.IsNullOrWhiteSpace(productName);
        var slogan = store.GetServicePartnerSlogan();

        var role = user.Claims.FirstOrDefault(claim => claim.Type == System.Security.Claims.ClaimTypes.Role)?.Value ?? "Viewer";

        return new InstanceShell
        {
            Authed = authed,
            Embedded = embedded,
            Secondary = secondary,
            IsAdmin = isAdmin,
            BrandName = whiteLabel ? productName! : "Matmon",
            Tagline = whiteLabel
                ? (!string.IsNullOrWhiteSpace(slogan) ? slogan! : !string.IsNullOrWhiteSpace(partnerName) ? $"Managed by {partnerName}" : "Monitoring")
                : secondary ? "Secondary probe node" : !string.IsNullOrWhiteSpace(partnerName) ? $"Managed by {partnerName}" : "Lightweight monitoring",
            PartnerLogo = whiteLabel && hasLogo,
            PartnerLogoOnly = whiteLabel && hasLogo && store.GetServicePartnerLogoIsOem(),
            BrandStacked = whiteLabel && hasLogo && store.GetServicePartnerSidebarStyle() == 0,
            PartnerFavicon = store.GetServicePartnerHasSmallLogo(),
            PartnerAccent = BrandingSafety.SafeHexColor(store.GetServicePartnerBrandColor()),
            ManagedBy = !whiteLabel && !string.IsNullOrWhiteSpace(partnerName) ? partnerName : null,
            PoweredBy = whiteLabel,
            Menu = menu,
            Info = info,
            UserName = user.Identity?.Name ?? "Account",
            UserRole = role,
            UpdateAvailable = cloudUpdate.UpdateAvailable,
            LatestVersion = cloudUpdate.LatestVersion,
            EmbedNav = embedNav
        };
    }

    /// <summary>The tone and the state word of the alert status - the most severe thing first. The same rule runs in
    /// dashboard.js (<c>alertStatus</c>) for the live update; change both together.</summary>
    public static (string Tone, string State) AlertStatus(int errors, int warnings, int acknowledged, int paused)
        => errors > 0 ? ("error", "Error")
            : warnings > 0 ? ("warn", "Warning")
            : acknowledged > 0 ? ("info", "Ack")
            : paused > 0 ? ("plain", "Paused")
            : ("ok", "OK");
}
