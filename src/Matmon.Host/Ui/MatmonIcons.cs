namespace Matmon.Host.Ui;

using Microsoft.AspNetCore.Html;

/// <summary>
/// Matmon's old icon names, drawn from MatUi's sprite. Matmon used to carry its own inline SVG glyphs; every one of them
/// has a counterpart in the toolkit's sprite now (ICON-02: one meaning, one icon everywhere), so this only maps the old
/// names - new markup calls <see cref="MatUi.Icon"/> directly.
/// </summary>
public static class MatmonIcons
{
    private static readonly IReadOnlyDictionary<string, string> SpriteNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["dashboard"] = "dashboard",
        ["monitoring"] = "network",
        ["network"] = "network",
        ["cloud"] = "cloud",
        ["bell"] = "bell",
        ["mail"] = "mail",
        ["inbox"] = "mail",
        ["gear"] = "settings",
        ["plus"] = "plus",
        ["copy"] = "copy",
        ["download"] = "download",
        ["upload"] = "upload",
        ["more"] = "more-horizontal",
        ["menu"] = "menu",
        ["probe"] = "server",
        ["folder"] = "folder",
        ["host"] = "monitor",
        ["sensor"] = "activity",
        ["template"] = "template",
        ["play"] = "play",
        ["pause"] = "pause",
        ["pencil"] = "edit",
        ["trash"] = "trash",
        ["check"] = "check",
        ["x"] = "x",
        ["arrow-left"] = "back",
        ["arrow-right"] = "arrow-right",
        ["move"] = "route",
        ["lock"] = "lock",
        ["spark"] = "sparkles",
        ["list"] = "list",
        ["search"] = "search",
        ["chart"] = "bar-chart",
        ["clock"] = "clock",
        ["signal"] = "activity",
        ["warning"] = "alert",
        ["tag"] = "tag",
        ["sun"] = "sun",
        ["moon"] = "moon",
        ["theme-system"] = "monitor",
        ["eye"] = "eye",
        ["arrow-up"] = "arrow-up",
        ["arrow-down"] = "arrow-down",
        ["square"] = "box",
        ["grid"] = "grid",
        ["activity"] = "activity",
        ["layers"] = "layers"
    };

    /// <summary>The icon of an old Matmon name, from the sprite (an unknown name falls back to a box).</summary>
    public static IHtmlContent Render(string name, string? cssClass = null)
        => MatUi.Icon(SpriteNames.TryGetValue(name, out var sprite) ? sprite : "box", cssClass);

    /// <summary>The sprite name an old Matmon icon name maps to (for markup a script builds).</summary>
    public static string SpriteName(string name) => SpriteNames.TryGetValue(name, out var sprite) ? sprite : "box";

    /// <summary>Every old name this maps (a catalogue that stores icon keys is checked against it).</summary>
    public static IEnumerable<string> Keys => SpriteNames.Keys;
}
