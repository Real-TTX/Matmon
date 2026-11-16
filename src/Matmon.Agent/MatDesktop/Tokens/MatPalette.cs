// MatDesktop 0.1.0 – a copy from MatDesign, do not edit here
// Plain C#, only System: a WinForms tray copies it together with MatTokens.g.cs and MatColor.cs (AGENT-05, tools/matdesktop-copy.js --tokens-only).
namespace MatDesktop;

/// <summary>How an accent token is made from the app's accent (<see cref="MatAccentRule"/>), as <c>matui.tokens.css</c> writes it.</summary>
public enum MatAccentKind
{
    /// <summary>The app's accent as it is (<c>var(--mat-app-accent, default)</c>).</summary>
    App,

    /// <summary>The app's accent mixed in OKLab toward white or black (the dark accent).</summary>
    AppMix,

    /// <summary>The accent of the mode mixed in OKLab toward white or black (hover, pressed, the text colour of dark mode).</summary>
    Mix,

    /// <summary>The accent of the mode with an opacity (<c>color-mix(in srgb, accent n%, transparent)</c>): a tint, the focus ring, the selection.</summary>
    Alpha,

    /// <summary>The app's colour of text on the accent, or <see cref="MatAccentRule.Value"/> when it sets none.</summary>
    OnApp,

    /// <summary>A fixed colour, <see cref="MatAccentRule.Value"/>.</summary>
    Fixed,

    /// <summary>The app's own colour of accent text, or the accent mixed toward white or black when it sets none.</summary>
    TextApp,
}

/// <summary>What an accent is mixed with.</summary>
public enum MatMixWith
{
    /// <summary>Black.</summary>
    Black,

    /// <summary>White.</summary>
    White,
}

/// <summary>One derived accent token: its name, how it is made, the share of the accent (0..1) and what it is mixed with, or a fixed value.</summary>
public sealed record MatAccentRule(string Token, MatAccentKind Kind, double Share = 1, MatMixWith With = MatMixWith.Black, string? Value = null);

/// <summary>
/// Every colour token of one mode for one accent, computed the way <c>matui.tokens.css</c> computes it: the base colours of <see cref="MatTokens"/> and the accent tokens by the rules of
/// <see cref="MatTokens.AccentLight"/> and <see cref="MatTokens.AccentDark"/> (OKLab mixing, an opacity for a tint). The keys are the names of the CSS (<c>--mat-surface</c>).
/// Colour schemes (COLOR-09) are not part of MatDesktop 0.1: the accent text keeps all of the accent (<c>--mat-accent-text-keep: 100%</c>), as in the standard look.
/// </summary>
public sealed class MatPalette
{
    private readonly Dictionary<string, MatRgba> _colors;

    private MatPalette(bool dark, Dictionary<string, MatRgba> colors)
    {
        IsDark = dark;
        _colors = colors;
    }

    /// <summary>True for dark mode.</summary>
    public bool IsDark { get; }

    /// <summary>Every colour token of the mode, by the name of the CSS.</summary>
    public IReadOnlyDictionary<string, MatRgba> Colors => _colors;

    /// <summary>The value of a colour token (<c>--mat-surface</c>).</summary>
    public MatRgba this[string token] => _colors.TryGetValue(token, out var c) ? c : throw new KeyNotFoundException($"{token} is not a colour token of MatDesktop.");

    /// <summary>The page background (<c>--mat-bg</c>).</summary>
    public MatRgba Bg => this["--mat-bg"];

    /// <summary>Cards, panels, dialogs (<c>--mat-surface</c>).</summary>
    public MatRgba Surface => this["--mat-surface"];

    /// <summary>The primary text (<c>--mat-text</c>).</summary>
    public MatRgba Text => this["--mat-text"];

    /// <summary>Secondary text (<c>--mat-text-muted</c>).</summary>
    public MatRgba TextMuted => this["--mat-text-muted"];

    /// <summary>Hairlines (<c>--mat-border</c>, with its opacity).</summary>
    public MatRgba Border => this["--mat-border"];

    /// <summary>The accent of the mode (<c>--mat-accent</c>).</summary>
    public MatRgba Accent => this["--mat-accent"];

    /// <summary>Text on the accent (<c>--mat-on-accent</c>).</summary>
    public MatRgba OnAccent => this["--mat-on-accent"];

    /// <summary>The accent as text on a surface (<c>--mat-accent-text</c>).</summary>
    public MatRgba AccentText => this["--mat-accent-text"];

    /// <summary>Running, healthy (<c>--mat-success</c>).</summary>
    public MatRgba Success => this["--mat-success"];

    /// <summary>Waiting, attention (<c>--mat-warning</c>).</summary>
    public MatRgba Warning => this["--mat-warning"];

    /// <summary>Stopped, an error (<c>--mat-danger</c>).</summary>
    public MatRgba Danger => this["--mat-danger"];

    /// <summary>
    /// The colours of one mode for an accent: <paramref name="accentHex"/> is the app's accent (<c>--mat-app-accent</c>, IDENT-01), <paramref name="onAccent"/> the colour of text on it
    /// where the app sets one (<c>--mat-app-on-accent</c>, light mode only, like the CSS) and <paramref name="accentText"/> its own accent text colour (<c>--mat-app-accent-text</c>,
    /// light mode only). Null or empty takes what the CSS takes when an app sets nothing.
    /// </summary>
    public static MatPalette For(string? accentHex, bool dark, string? onAccent = null, string? accentText = null)
    {
        var app = MatRgba.Parse(string.IsNullOrWhiteSpace(accentHex) ? MatTokens.DefaultAccent : accentHex);
        var colors = MatTokens.Colors.ToDictionary(c => c.Token, c => dark ? c.Dark : c.Light, StringComparer.Ordinal);
        var accent = app;
        foreach (var rule in dark ? MatTokens.AccentDark : MatTokens.AccentLight)
        {
            var toward = rule.With == MatMixWith.White ? MatRgba.White : MatRgba.Black;
            var value = rule.Kind switch
            {
                MatAccentKind.App => app,
                MatAccentKind.AppMix => MatColor.Mix(app, toward, rule.Share),
                MatAccentKind.Mix => MatColor.Mix(accent, toward, rule.Share),
                MatAccentKind.Alpha => MatColor.Transparent(accent, rule.Share),
                MatAccentKind.OnApp => MatRgba.Parse(string.IsNullOrWhiteSpace(onAccent) ? rule.Value! : onAccent),
                MatAccentKind.Fixed => MatRgba.Parse(rule.Value!),
                MatAccentKind.TextApp => string.IsNullOrWhiteSpace(accentText) ? MatColor.Mix(accent, toward, rule.Share) : MatRgba.Parse(accentText),
                _ => throw new InvalidOperationException($"{rule.Kind} is not a kind of accent rule."),
            };
            colors[rule.Token] = value;
            if (rule.Token == "--mat-accent")
            {
                accent = value;
            }
        }

        return new MatPalette(dark, colors);
    }
}
