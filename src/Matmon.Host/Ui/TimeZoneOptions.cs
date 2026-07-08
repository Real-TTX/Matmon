using Microsoft.AspNetCore.Mvc.Rendering;

namespace Matmon.Host.Ui;

/// <summary>Builds the display-timezone dropdown: a leading "use default" entry, then every system zone
/// ordered by UTC offset. Used by the per-user (Account) and system-default (System) pickers.</summary>
public static class TimeZoneOptions
{
    public static List<SelectListItem> Build(string? selectedId, string defaultLabel)
    {
        var items = new List<SelectListItem>
        {
            new(defaultLabel, string.Empty) { Selected = string.IsNullOrWhiteSpace(selectedId) },
        };

        items.AddRange(TimeZoneInfo.GetSystemTimeZones()
            .OrderBy(zone => zone.BaseUtcOffset)
            .ThenBy(zone => zone.Id, StringComparer.OrdinalIgnoreCase)
            .Select(zone => new SelectListItem(
                $"(UTC{FormatOffset(zone.BaseUtcOffset)}) {zone.Id}",
                zone.Id,
                string.Equals(zone.Id, selectedId, StringComparison.Ordinal))));

        return items;
    }

    private static string FormatOffset(TimeSpan offset) =>
        offset == TimeSpan.Zero ? "+00:00" : (offset < TimeSpan.Zero ? "-" : "+") + offset.ToString(@"hh\:mm");
}
