namespace Matmon.Host.Ui;

public static class BreadcrumbBuilder
{
    public static IReadOnlyList<BreadcrumbItem> Build(string path, string? title, IEnumerable<BreadcrumbItem>? overrideTrail = null)
    {
        var customTrail = overrideTrail?.Where(item => !string.IsNullOrWhiteSpace(item.Label)).ToArray();
        if (customTrail is { Length: > 0 })
        {
            return customTrail;
        }

        var normalizedPath = NormalizePath(path);
        if (normalizedPath is "/" or "/login")
        {
            return [];
        }

        var items = new List<BreadcrumbItem>
        {
            new("Monitoring", "/Index")
        };

        if (normalizedPath.StartsWith("/monitoring/sensor/", StringComparison.OrdinalIgnoreCase))
        {
            items.Add(new("Sensors", "/Monitoring"));
            items.Add(new(GetLeafTitle(title, "Sensor")));
            return items;
        }

        switch (normalizedPath.ToLowerInvariant())
        {
            case "/monitoring":
                items.Add(new("Sensors"));
                break;
            case "/discovery":
                items.Add(new("Discovery"));
                break;
            case "/workspace":
                items.Add(new("Infrastructure"));
                break;
            case "/templates":
                items.Add(new("Templates"));
                break;
            case "/notifications":
                items.Add(new("Notifications", "/Notifications"));
                items.Add(new("Rules"));
                break;
            case "/notifications/settings":
                items.Add(new("Notifications", "/Notifications"));
                items.Add(new("Settings"));
                break;
            case "/notifications/rule":
                items.Add(new("Notifications", "/Notifications"));
                items.Add(new("Rules", "/Notifications"));
                items.Add(new(GetLeafTitle(title, "Rule")));
                break;
            case "/events":
                items.Add(new("Events"));
                break;
            case "/alerts":
                items.Add(new("Alerts"));
                break;
            case "/paused":
                items.Add(new("Paused"));
                break;
            case "/config":
                items.Add(new("System"));
                break;
            case "/monitoring/sensor/new":
                items.Add(new("Sensors", "/Monitoring"));
                items.Add(new("New sensor"));
                break;
            case "/monitoring/probe/new":
                items.Add(new("Infrastructure", "/Workspace"));
                items.Add(new("New probe"));
                break;
            case "/monitoring/folder/new":
                items.Add(new("Infrastructure", "/Workspace"));
                items.Add(new("New folder"));
                break;
            case "/monitoring/host/new":
                items.Add(new("Infrastructure", "/Workspace"));
                items.Add(new("New host"));
                break;
            case "/monitoring/element":
                items.Add(new("Infrastructure", "/Workspace"));
                items.Add(new(GetLeafTitle(title, "Edit")));
                break;
            case "/monitoring/template":
                items.Add(new("Templates", "/Templates"));
                items.Add(new(GetLeafTitle(title, "Template")));
                break;
            default:
                items.Add(new(GetLeafTitle(title, "Page")));
                break;
        }

        return items;
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
