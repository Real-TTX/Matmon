namespace Matmon.Core.Domain;

/// <summary>
/// Pure helpers for tag normalization and the cascading "effective tags" of an
/// element (its own tags plus every ancestor's), kept free of storage so it can
/// be unit tested and reused by the store, UI and map filtering.
/// </summary>
public static class MonitoringTagResolver
{
    /// <summary>
    /// Cleans a raw tag list: trims, drops blanks, and de-duplicates
    /// case-insensitively while keeping the first-seen spelling and order.
    /// </summary>
    public static List<string> Normalize(IEnumerable<string>? tags)
    {
        var result = new List<string>();
        if (tags is null)
        {
            return result;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in tags)
        {
            var trimmed = tag?.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                continue;
            }

            if (seen.Add(trimmed))
            {
                result.Add(trimmed);
            }
        }

        return result;
    }

    /// <summary>Parses a free-text field (comma / newline separated) into normalized tags.</summary>
    public static List<string> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return Normalize(text.Split([',', '\n', '\r', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    /// <summary>
    /// The element's own tags plus every ancestor's, de-duplicated. The lineage
    /// is expected root → element (as built for inheritance resolution).
    /// </summary>
    public static List<string> ResolveEffective(IReadOnlyList<MonitoringElement> lineage)
    {
        ArgumentNullException.ThrowIfNull(lineage);
        return Normalize(lineage.SelectMany(element => element.Tags));
    }

    public static bool HasTag(IEnumerable<string> tags, string tag)
        => tags.Any(candidate => string.Equals(candidate, tag, StringComparison.OrdinalIgnoreCase));
}
