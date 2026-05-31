using System.Net;
using System.Text.RegularExpressions;

namespace Matmon.Core.Domain;

public static class NotificationTemplateRenderer
{
    private static readonly Regex RawPlaceholderRegex = new(@"\{\{\{(?<key>[^{}]+)\}\}\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PlaceholderRegex = new(@"\{\{(?<key>[^{}]+)\}\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string RenderText(string? template, NotificationTemplateContext context, string? fallbackTemplate = null)
    {
        return Render(template, context.Values, null, fallbackTemplate, encodeValues: false);
    }

    public static string RenderHtml(string? template, NotificationTemplateContext context, string? fallbackTemplate = null)
    {
        return Render(template, context.Values, context.RawHtml, fallbackTemplate, encodeValues: true);
    }

    private static string Render(
        string? template,
        IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, string>? rawHtml,
        string? fallbackTemplate,
        bool encodeValues)
    {
        var source = string.IsNullOrWhiteSpace(template)
            ? fallbackTemplate
            : template;

        if (string.IsNullOrWhiteSpace(source))
        {
            return string.Empty;
        }

        var rendered = source;
        var rawTokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (rawHtml is not null)
        {
            rendered = RawPlaceholderRegex.Replace(rendered, match =>
            {
                var key = match.Groups["key"].Value.Trim();
                var value = rawHtml.TryGetValue(key, out var raw) ? raw : string.Empty;
                var token = $"__MATMON_RAW_{rawTokens.Count:N0}__";
                rawTokens[token] = value;
                return token;
            });
        }

        rendered = PlaceholderRegex.Replace(rendered, match =>
        {
            var key = match.Groups["key"].Value.Trim();
            var value = values.TryGetValue(key, out var matched) ? matched : string.Empty;
            return encodeValues ? WebUtility.HtmlEncode(value) : value;
        });

        foreach (var token in rawTokens)
        {
            rendered = rendered.Replace(token.Key, token.Value, StringComparison.Ordinal);
        }

        return rendered;
    }
}
