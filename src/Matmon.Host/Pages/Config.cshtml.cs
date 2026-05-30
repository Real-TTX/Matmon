using Matmon.Host.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matmon.Host.Pages;

public class ConfigModel : PageModel
{
    private readonly IConfigurationOverviewProvider _configurationOverviewProvider;

    public ConfigModel(IConfigurationOverviewProvider configurationOverviewProvider)
    {
        _configurationOverviewProvider = configurationOverviewProvider;
    }

    public ConfigurationOverview Overview { get; private set; } = default!;

    [BindProperty(SupportsGet = true)]
    public string? Tab { get; set; }

    public string ActiveTab => NormalizeTab(Tab);

    public void OnGet()
    {
        Overview = _configurationOverviewProvider.GetOverview();
    }

    public bool IsActiveTab(string tab)
    {
        return string.Equals(ActiveTab, tab, StringComparison.OrdinalIgnoreCase);
    }

    public string FormatDateTime(DateTimeOffset? timestampUtc)
    {
        if (timestampUtc is null)
        {
            return "-";
        }

        return timestampUtc.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss");
    }

    public static string FormatPercent(double? value)
    {
        return value.HasValue
            ? $"{value.Value:0.##}%"
            : "-";
    }

    private static string NormalizeTab(string? tab)
    {
        return tab?.Trim().ToLowerInvariant() switch
        {
            "probes" => "probes",
            "storage" => "storage",
            _ => "general"
        };
    }
}
