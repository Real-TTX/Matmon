using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Html;

namespace Matmon.Host.Ui;

/// <summary>
/// Small helpers for the markup of MatUi, the toolkit every Mat app is drawn with. The toolkit itself is COPIED into
/// <c>wwwroot/matui</c> (rule CODE-03 - never loaded from another site) by <c>scripts/sync-matui.ps1</c>; this class only
/// writes the markup that points into it.
/// </summary>
public static partial class MatUi
{
    [GeneratedRegex("^[a-z0-9-]+$")]
    private static partial Regex NameRegex();

    /// <summary>An icon of the inline sprite (<c>matui-icons.svg</c>). Decorative: a button that has no text next to it
    /// carries its own <c>aria-label</c>.</summary>
    public static IHtmlContent Icon(string name, string? extraClass = null)
    {
        if (!NameRegex().IsMatch(name))
        {
            throw new ArgumentException($"'{name}' is not an icon name.", nameof(name));
        }

        var cls = string.IsNullOrEmpty(extraClass) ? "mat-icon" : $"mat-icon {extraClass}";
        return new HtmlString($"<svg class=\"{cls}\" aria-hidden=\"true\" focusable=\"false\"><use href=\"#mat-i-{name}\"></use></svg>");
    }

    /// <summary>One or two letters for an avatar: the initials of a name, or of the part of an e-mail address before the @.</summary>
    public static string Initials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "?";
        }

        var text = name.Trim();
        var at = text.IndexOf('@');
        if (at > 0)
        {
            text = text[..at];
        }

        var parts = text.Split([' ', '.', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
        var letters = parts.Length >= 2
            ? $"{parts[0][0]}{parts[1][0]}"
            : text[..Math.Min(2, text.Length)];
        return letters.ToUpperInvariant();
    }

    /// <summary>
    /// The pill of a state as MatUi spells it (<c>mat-pill--ok|warn|error|info|busy|plain</c>). Takes a tone or one of
    /// Matmon's state keys (<see cref="Matmon.Core.Domain.MonitoringStatePresentation"/>.Key: ok, warning, error, paused,
    /// disabled, unknown - and "ack" for an acknowledged alert). The word goes inside the pill: colour alone never carries
    /// the state (TABLE-03, COLOR-04).
    /// </summary>
    public static string PillClass(string? tone) => tone switch
    {
        "ok" or "success" or "online" or "healthy" => "mat-pill mat-pill--ok",
        "warn" or "warning" => "mat-pill mat-pill--warn",
        "error" or "danger" or "critical" => "mat-pill mat-pill--error",
        "info" or "ack" => "mat-pill mat-pill--info",
        "busy" or "running" => "mat-pill mat-pill--busy",
        "accent" => "mat-pill mat-pill--accent",
        "paused" or "disabled" or "plain" => "mat-pill mat-pill--plain",
        _ => "mat-pill"
    };

    /// <summary>The pill of a sensor state.</summary>
    public static string StatePill(Matmon.Core.Domain.SensorState state) => PillClass(Matmon.Core.Domain.MonitoringStatePresentation.Key(state));

    /// <summary>HTML-encodes a value for an attribute the page writes by hand (Razor encodes its own expressions).</summary>
    public static string Attr(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
}

/// <summary>
/// The toolkit files as a page needs them: the icon sprite, which every page inlines once (the icons reference it by id,
/// so it works offline and under a strict CSP), and the version hash the stylesheet and script addresses carry so a new
/// copy is never served from a stale cache.
/// </summary>
public sealed class MatUiToolkit
{
    public MatUiToolkit(IWebHostEnvironment environment)
    {
        var root = environment.WebRootFileProvider;
        Sprite = Read(root, "matui/matui-icons.svg") ?? string.Empty;

        var manifest = Read(root, "matui/matui.manifest.json");
        if (manifest is not null)
        {
            using var doc = JsonDocument.Parse(manifest);
            Version = doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() ?? "?" : "?";
            Hash = doc.RootElement.TryGetProperty("hash", out var h) ? h.GetString() ?? Version : Version;
        }
    }

    /// <summary>The inline sprite (<c>&lt;svg class="mat-sprite"&gt;</c> with one symbol per icon).</summary>
    public string Sprite { get; }

    /// <summary>The MatUi version of the copy, e.g. <c>0.8.0</c>.</summary>
    public string Version { get; } = "?";

    /// <summary>The hash of the copy (from its manifest), appended to the file addresses.</summary>
    public string Hash { get; } = "dev";

    private static string? Read(Microsoft.Extensions.FileProviders.IFileProvider provider, string path)
    {
        var file = provider.GetFileInfo(path);
        if (!file.Exists)
        {
            return null;
        }

        using var stream = file.CreateReadStream();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
