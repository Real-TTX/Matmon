namespace Matmon.Host.Ui;

/// <summary>
/// The breadcrumb trail of a page. It starts at the MENU ITEM the page belongs to (Sensors, Probes, Notifications,
/// System …) - the name the user just clicked - and ends at the page. It used to start with "Monitoring", a name no menu
/// item carries, and put pages under sections they are not in (Probes under Sensors, new hosts under an "Infrastructure"
/// page the menu does not have). A top-level page is a trail of one. A page can pass its own trail (ViewData).
/// </summary>
public static class BreadcrumbBuilder
{
    public static IReadOnlyList<BreadcrumbItem> Build(string path, string? title, IEnumerable<BreadcrumbItem>? overrideTrail = null)
    {
        var customTrail = overrideTrail?.Where(item => !string.IsNullOrWhiteSpace(item.Label)).ToArray();
        if (customTrail is { Length: > 0 })
        {
            return customTrail;
        }

        var normalizedPath = NormalizePath(path).ToLowerInvariant();
        if (normalizedPath is "/" or "/index" or "/login" or "/access-denied")
        {
            return [];
        }

        // Sections whose sub-pages share a prefix: the section, then the page.
        if (normalizedPath.StartsWith("/monitoring/sensor/", StringComparison.Ordinal) && normalizedPath != "/monitoring/sensor/new" && normalizedPath != "/monitoring/sensor/assistant")
        {
            return [new("Sensors", "/Monitoring"), new(GetLeafTitle(title, "Sensor"))];
        }

        if (normalizedPath.StartsWith("/monitoring/probe/", StringComparison.Ordinal))
        {
            return [new("Probes", "/Probes"), new(GetLeafTitle(title, "Probe"))];
        }

        return normalizedPath switch
        {
            "/monitoring" => [new("Sensors")],
            "/monitoring/sensor/new" => [new("Sensors", "/Monitoring"), new("New sensor")],
            "/monitoring/sensor/assistant" => [new("Sensors", "/Monitoring"), new(GetLeafTitle(title, "Sensor assistant"))],
            "/monitoring/folder/new" => [new("Sensors", "/Monitoring"), new("New folder")],
            "/monitoring/host/new" => [new("Sensors", "/Monitoring"), new("New host")],
            "/monitoring/element" => [new("Sensors", "/Monitoring"), new(GetLeafTitle(title, "Edit"))],
            "/paused" => [new("Alerts", "/Alerts"), new("Paused sensors")],
            "/maps/edit" => [new("Maps", "/Maps"), new(GetLeafTitle(title, "Map"))],

            "/probes" => [new("Probes")],
            "/agents" => [new("Probes", "/Probes"), new("Agents")],
            "/system/probes/new" => [new("Probes", "/Probes"), new("New probe")],
            "/system/probes/install" => [new("Probes", "/Probes"), new("Install")],

            "/monitoring/template" => [new("Templates", "/Templates"), new(GetLeafTitle(title, "Template"))],

            // One page with three tabs (rules, senders, receivers): the trail ends at the page, not at one tab.
            "/notifications" => [new("Notifications")],
            "/notifications/rule" => [new("Notifications", "/Notifications"), new(GetLeafTitle(title, "Rule"))],

            "/config" => [new("System")],
            "/logs" => [new("System", "/Config"), new("Logs")],
            "/system/mibs" => [new("System", "/Config"), new("SNMP MIBs")],
            "/system/users/new" => [new("System", "/Config"), new("Users", "/Config?tab=users"), new("New user")],
            "/system/users/edit" => [new("System", "/Config"), new("Users", "/Config?tab=users"), new("Edit user")],
            "/backupjobeditor" => [new("System", "/Config"), new("Backup", "/Config?tab=backup"), new("Backup job")],
            "/backuprestore" => [new("System", "/Config"), new("Backup", "/Config?tab=backup"), new("Restore assistant")],

            _ => [new(GetLeafTitle(title, "Page"))]
        };
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "/";
        }

        var normalized = path.TrimEnd('/');
        return normalized.Length == 0 ? "/" : normalized;
    }

    private static string GetLeafTitle(string? title, string fallback)
    {
        return string.IsNullOrWhiteSpace(title) ? fallback : title;
    }
}
