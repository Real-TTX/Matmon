namespace Matmon.Host.Ui;

using Microsoft.AspNetCore.Html;
using System.Collections.Generic;
using System.Text.Encodings.Web;

public static class MatmonIcons
{
    private static readonly HtmlEncoder Encoder = HtmlEncoder.Default;

    private static readonly IReadOnlyDictionary<string, string> IconBodies = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
    {
        ["dashboard"] = """
            <rect x="2" y="2" width="4" height="4" />
            <rect x="10" y="2" width="4" height="4" />
            <rect x="2" y="10" width="4" height="4" />
            <rect x="10" y="10" width="4" height="4" />
            """,
        ["monitoring"] = """
            <circle cx="8" cy="3" r="1.2" />
            <circle cx="3.5" cy="11.5" r="1.1" />
            <circle cx="12.5" cy="11.5" r="1.1" />
            <path d="M8 4.2v2.5" />
            <path d="M7.2 6.7L4.4 10.5" />
            <path d="M8.8 6.7l2.8 3.8" />
            """,
        ["network"] = """
            <circle cx="4" cy="4" r="1" />
            <circle cx="12" cy="4" r="1" />
            <circle cx="8" cy="12" r="1" />
            <path d="M4.8 4.7 7.2 10.8" />
            <path d="M11.2 4.7 8.8 10.8" />
            <path d="M5 4h6" />
            """,
        ["cloud"] = """
            <path d="M4.7 11.8A2.4 2.4 0 0 1 4.9 7 3.2 3.2 0 0 1 11 6a2.5 2.5 0 0 1 .2 5.8Z" />
            """,
        ["bell"] = """
            <path d="M8 2.4A3.7 3.7 0 0 0 4.3 6.1v2c0 .9-.2 1.7-.7 2.4L3 11.6h10l-.6-1.1a4.5 4.5 0 0 1-.7-2.4v-2A3.7 3.7 0 0 0 8 2.4Z" />
            <path d="M6.6 12.5a1.5 1.5 0 0 0 2.8 0" />
            """,
        ["mail"] = """
            <rect x="2.4" y="4" width="11.2" height="8" rx="1.2" />
            <path d="M3.2 5.1 8 8.8l4.8-3.7" />
            """,
        ["inbox"] = """
            <path d="M2.8 6.1h3.2l1.1 2h1.8l1.1-2h3.2v5.1H2.8Z" />
            <path d="M5 6.1 7.4 3.8h1.2L11 6.1" />
            """,
        ["gear"] = """
            <circle cx="8" cy="8" r="2.2" />
            <circle cx="8" cy="8" r="4.8" />
            <path d="M8 1.6v1.5" />
            <path d="M8 12.9v1.5" />
            <path d="M1.6 8h1.5" />
            <path d="M12.9 8h1.5" />
            <path d="M3.2 3.2l1.1 1.1" />
            <path d="M11.7 11.7l1.1 1.1" />
            <path d="M12.8 3.2l-1.1 1.1" />
            <path d="M3.2 12.8l1.1-1.1" />
            """,
        ["plus"] = """
            <path d="M8 3v10" />
            <path d="M3 8h10" />
            """,
        ["copy"] = """
            <rect x="5.2" y="5.2" width="7.8" height="7.8" rx="1.2" />
            <path d="M4.2 10.2H3.3A1.6 1.6 0 0 1 1.7 8.6V3.3A1.6 1.6 0 0 1 3.3 1.7h5.3A1.6 1.6 0 0 1 10.2 3.3v.9" />
            """,
        ["download"] = """
            <path d="M8 2.8v6.4" />
            <path d="M5.6 7.2 8 9.6l2.4-2.4" />
            <path d="M3 12.8h10" />
            """,
        ["upload"] = """
            <path d="M8 13.2V6.8" />
            <path d="M5.6 9.4 8 7l2.4 2.4" />
            <path d="M3 3.2h10" />
            """,
        ["more"] = """
            <circle cx="4" cy="8" r="0.8" fill="currentColor" stroke="none" />
            <circle cx="8" cy="8" r="0.8" fill="currentColor" stroke="none" />
            <circle cx="12" cy="8" r="0.8" fill="currentColor" stroke="none" />
            """,
        ["menu"] = """
            <path d="M3 4h10" />
            <path d="M3 8h10" />
            <path d="M3 12h10" />
            """,
        ["probe"] = """
            <path d="M2.3 11.2a8 8 0 0 1 11.4 0" />
            <path d="M4.3 9.1a5.1 5.1 0 0 1 7.4 0" />
            <path d="M6.3 7a2.1 2.1 0 0 1 3.4 0" />
            <circle cx="8" cy="13.2" r="0.9" />
            """,
        ["folder"] = """
            <path d="M2.5 4.8h4.2l1.2 1.4h5.6v5.8H2.5Z" />
            """,
        ["host"] = """
            <rect x="2.5" y="3.2" width="11" height="7.1" />
            <path d="M6 13.1h4" />
            <path d="M8 10.3v2.8" />
            """,
        ["sensor"] = """
            <circle cx="8" cy="8" r="4.8" />
            <circle cx="8" cy="8" r="1.5" />
            <path d="M8 1.5v2" />
            <path d="M8 12.5v2" />
            <path d="M1.5 8h2" />
            <path d="M12.5 8h2" />
            """,
        ["template"] = """
            <path d="M3 5.1 8 3l5 2.1-5 2.1Z" />
            <path d="M3 8 8 10.1 13 8" />
            <path d="M3 11 8 13.1 13 11" />
            """,
        ["play"] = """
            <path d="M5 3.8 11.6 8 5 12.2Z" fill="currentColor" stroke="none" />
            """,
        ["pause"] = """
            <path d="M5 3.5v9" />
            <path d="M10.5 3.5v9" />
            """,
        ["pencil"] = """
            <path d="m4.2 11.3 7.2-7.2 1.8 1.8-7.2 7.2-2.6.6.6-2.4Z" />
            <path d="m10.4 3.7 1.8 1.8" />
            """,
        ["trash"] = """
            <path d="M3.5 5.2h9" />
            <path d="M6.1 5.2v-1h3.8v1" />
            <path d="M4.4 5.2 4.8 13h6.4l.4-7.8" />
            <path d="M6.6 7.1v3.9" />
            <path d="M9.4 7.1v3.9" />
            """,
        ["check"] = """
            <path d="m3.4 8.3 3 3 6.2-6.2" />
            """,
        ["x"] = """
            <path d="M4 4l8 8" />
            <path d="M12 4 4 12" />
            """,
        ["arrow-left"] = """
            <path d="M7 3.5 2.5 8l4.5 4.5" />
            <path d="M2.8 8h10.4" />
            """,
        ["arrow-right"] = """
            <path d="M9 3.5 13.5 8 9 12.5" />
            <path d="M13.2 8H2.8" />
            """,
        ["move"] = """
            <path d="M8 1.8v12.4" />
            <path d="M1.8 8h12.4" />
            <path d="M8 1.8 6.2 3.6" />
            <path d="M8 1.8 9.8 3.6" />
            <path d="M8 14.2 6.2 12.4" />
            <path d="M8 14.2 9.8 12.4" />
            <path d="M1.8 8 3.6 6.2" />
            <path d="M1.8 8 3.6 9.8" />
            <path d="M14.2 8 12.4 6.2" />
            <path d="M14.2 8 12.4 9.8" />
            """,
        ["lock"] = """
            <rect x="3.2" y="7.2" width="9.6" height="6.3" />
            <path d="M5.5 7.2V5.7A2.5 2.5 0 0 1 8 3.2a2.5 2.5 0 0 1 2.5 2.5v1.5" />
            """,
        ["spark"] = """
            <path d="M2.5 11.5h2.2l1.4-6 2.1 10 1.7-7h3.6" />
            """,
        ["list"] = """
            <path d="M4 4h8" />
            <path d="M4 8h8" />
            <path d="M4 12h8" />
            <circle cx="2.7" cy="4" r="0.7" fill="currentColor" stroke="none" />
            <circle cx="2.7" cy="8" r="0.7" fill="currentColor" stroke="none" />
            <circle cx="2.7" cy="12" r="0.7" fill="currentColor" stroke="none" />
            """,
        ["search"] = """
            <circle cx="7" cy="7" r="3.8" />
            <path d="M9.8 9.8 12.8 12.8" />
            """,
        ["chart"] = """
            <path d="M2.5 13.5h11" />
            <path d="M3.5 12.8V4.2" />
            <path d="M3.5 11l2.8-2.1 2.1 1.1 3.2-4.6" />
            <circle cx="6.3" cy="8.9" r="0.55" fill="currentColor" stroke="none" />
            <circle cx="8.4" cy="10" r="0.55" fill="currentColor" stroke="none" />
            <circle cx="11.6" cy="5.4" r="0.55" fill="currentColor" stroke="none" />
            """,
        ["clock"] = """
            <circle cx="8" cy="8" r="5.2" />
            <path d="M8 5.2v3l2.1 1.2" />
            """,
        ["signal"] = """
            <path d="M4 11.5a5.6 5.6 0 0 1 8 0" />
            <path d="M5.8 9.3a3.2 3.2 0 0 1 4.4 0" />
            <path d="M7.2 7.5a1.1 1.1 0 0 1 1.6 0" />
            <circle cx="8" cy="13.1" r="0.9" />
            """,
        ["warning"] = """
            <path d="M8 3.2 13 12H3Z" />
            <path d="M8 6.1v2.7" />
            <path d="M8 10.5h.01" />
            """,
        ["tag"] = """
            <path d="M8.4 2.5H13V7.1l-5.6 5.6a1.2 1.2 0 0 1-1.7 0L2.8 9.3a1.2 1.2 0 0 1 0-1.7z" />
            <circle cx="10.4" cy="5.1" r="0.95" />
            """,
        ["sun"] = """
            <circle cx="8" cy="8" r="2.6" />
            <path d="M8 1.7v1.4" />
            <path d="M8 12.9v1.4" />
            <path d="M1.7 8h1.4" />
            <path d="M12.9 8h1.4" />
            <path d="M3.3 3.3l1 1" />
            <path d="M11.7 11.7l1 1" />
            <path d="M12.7 3.3l-1 1" />
            <path d="M3.3 12.7l1-1" />
            """,
        ["moon"] = """
            <path d="M10.8 2.7A5.8 5.8 0 1 0 13.3 10a4.8 4.8 0 0 1-2.5.7A5.8 5.8 0 0 1 10.8 2.7Z" />
            """,
        // Split sun/moon - the "System / follow OS" theme state.
        ["theme-system"] = """
            <circle cx="8" cy="8" r="4.4" />
            <path d="M8 3.6A4.4 4.4 0 0 1 8 12.4Z" fill="currentColor" stroke="none" />
            <path d="M8 0.9v1.3" />
            <path d="M8 13.8v1.3" />
            <path d="M1.1 8h1.3" />
            <path d="M2.9 2.9l0.95 0.95" />
            <path d="M2.9 13.1l0.95-0.95" />
            """,
        ["square"] = """
            <rect x="3" y="3" width="10" height="10" />
            """,
        ["eye"] = """
            <path d="M1.6 8S3.9 3.8 8 3.8 14.4 8 14.4 8 12.1 12.2 8 12.2 1.6 8 1.6 8Z" />
            <circle cx="8" cy="8" r="2.1" />
            """,
        ["arrow-up"] = """
            <path d="M8 13.2V2.8" />
            <path d="M3.5 7.3 8 2.8l4.5 4.5" />
            """,
        ["arrow-down"] = """
            <path d="M8 2.8v10.4" />
            <path d="M3.5 8.7 8 13.2l4.5-4.5" />
            """,
    };

    public static IHtmlContent Render(string name, string? cssClass = null)
    {
        var body = IconBodies.TryGetValue(name, out var iconBody) ? iconBody : IconBodies["square"];
        var className = string.IsNullOrWhiteSpace(cssClass) ? "ui-icon" : $"ui-icon {cssClass}";
        return new HtmlString($"""
            <svg class="{Encoder.Encode(className)}" viewBox="0 0 16 16" aria-hidden="true" focusable="false" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round">{body}</svg>
            """);
    }
}
