using System.Globalization;

namespace Matmon.Core.Domain;

/// <summary>
/// A monitoring "target" is what a notification rule, map tile, etc. points at. It is
/// stored as a single string token that is either an element id (a <see cref="Guid"/>)
/// or a tag, written as <c>tag:&lt;name&gt;</c>. A tag target is dynamic: it resolves to
/// every sensor whose <em>effective</em> tags include that tag (same aggregation as a
/// folder, but cross-tree). This helper only parses/formats the token — resolving it to
/// actual sensors needs the topology and lives in the workspace store.
/// </summary>
public static class MonitoringTargetResolver
{
    public const string TagPrefix = "tag:";

    public static bool IsTag(string? token) =>
        !string.IsNullOrWhiteSpace(token) &&
        token.StartsWith(TagPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The normalized tag name of a <c>tag:&lt;name&gt;</c> token, or null.</summary>
    public static string? TagName(string? token)
    {
        if (!IsTag(token))
        {
            return null;
        }

        var name = token!.Substring(TagPrefix.Length).Trim();
        return string.IsNullOrEmpty(name) ? null : name;
    }

    /// <summary>The element id of a GUID token (i.e. an element target), or null.</summary>
    public static Guid? ElementId(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || IsTag(token))
        {
            return null;
        }

        return Guid.TryParse(token.Trim(), out var id) ? id : null;
    }

    /// <summary>Builds the token for a tag target.</summary>
    public static string ForTag(string tagName)
    {
        var normalized = MonitoringTagResolver.Normalize(new[] { tagName });
        var name = normalized.Count > 0 ? normalized[0] : (tagName ?? string.Empty).Trim();
        return TagPrefix + name;
    }

    /// <summary>Builds the token for an element target.</summary>
    public static string ForElement(Guid elementId) =>
        elementId.ToString("D", CultureInfo.InvariantCulture);
}
