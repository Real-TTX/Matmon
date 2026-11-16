// MatDesktop 0.1.0 – a copy from MatDesign, do not edit here
// Plain C#, only System: a WinForms tray copies it together with MatTokens.g.cs and MatPalette.cs (AGENT-05, tools/matdesktop-copy.js --tokens-only).
using System.Globalization;

namespace MatDesktop;

/// <summary>
/// An sRGB colour with an opacity, as the tokens write them (<c>#rrggbb</c>, <c>rgba(31, 30, 27, .13)</c>): the channels 0..255 as numbers (a colour that is mixed keeps its fractions,
/// like the browser does, and is rounded only when it is written), <see cref="A"/> 0..1.
/// </summary>
public readonly record struct MatRgba(double R, double G, double B, double A = 1)
{
    /// <summary>Opaque white.</summary>
    public static MatRgba White { get; } = new(255, 255, 255);

    /// <summary>Opaque black.</summary>
    public static MatRgba Black { get; } = new(0, 0, 0);

    /// <summary>Red, rounded and clamped to a byte.</summary>
    public byte R8 => ToByte(R);

    /// <summary>Green, rounded and clamped to a byte.</summary>
    public byte G8 => ToByte(G);

    /// <summary>Blue, rounded and clamped to a byte.</summary>
    public byte B8 => ToByte(B);

    /// <summary>The opacity as a byte (0..255).</summary>
    public byte A8 => ToByte(A * 255);

    /// <summary><c>#rrggbb</c> (the opacity left out).</summary>
    public string Hex => $"#{R8:x2}{G8:x2}{B8:x2}";

    /// <summary><c>#AARRGGBB</c>, the form of Avalonia, WPF and <c>Color.FromArgb</c> in WinForms.</summary>
    public string ArgbHex => $"#{A8:X2}{R8:X2}{G8:X2}{B8:X2}";

    /// <summary>A colour like <c>#5b5fe6</c>, <c>#fff</c>, <c>rgb(1, 2, 3)</c> or <c>rgba(31, 30, 27, .13)</c>.</summary>
    public static MatRgba Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var v = value.Trim();
        if (v.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
        {
            var open = v.IndexOf('(', StringComparison.Ordinal);
            var close = v.LastIndexOf(')');
            var parts = open < 0 || close < open ? [] : v[(open + 1)..close].Split([',', ' ', '/'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length is < 3 or > 4)
            {
                throw new FormatException($"'{value}' is not a colour like rgba(1, 2, 3, .5).");
            }

            static double Number(string s) => double.Parse(s.TrimEnd('%'), CultureInfo.InvariantCulture);
            var alpha = parts.Length == 4 ? (parts[3].EndsWith('%') ? Number(parts[3]) / 100 : Number(parts[3])) : 1;
            return new MatRgba(Number(parts[0]), Number(parts[1]), Number(parts[2]), alpha);
        }

        var h = v.TrimStart('#');
        if (h.Length == 3)
        {
            h = string.Concat(h.Select(c => $"{c}{c}"));
        }

        if (h.Length != 6 || !int.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var n))
        {
            throw new FormatException($"'{value}' is not a colour like #rrggbb.");
        }

        return new MatRgba((n >> 16) & 255, (n >> 8) & 255, n & 255);
    }

    /// <summary>The same colour with another opacity.</summary>
    public MatRgba WithAlpha(double alpha) => this with { A = alpha };

    /// <summary>The colour that is seen when this one is painted over an opaque <paramref name="background"/> (in sRGB, like a browser does): what a hairline or a tint looks like.</summary>
    public MatRgba Over(MatRgba background) => new(
        R * A + background.R * (1 - A),
        G * A + background.G * (1 - A),
        B * A + background.B * (1 - A));

    /// <inheritdoc />
    public override string ToString() => A >= 1 ? Hex : ArgbHex;

    private static byte ToByte(double v) => (byte)Math.Round(Math.Clamp(v, 0, 255));
}

/// <summary>A colour token with its light and its dark value (<see cref="MatTokens.Colors"/>).</summary>
public sealed record MatTokenColor(string Token, MatRgba Light, MatRgba Dark);

/// <summary>A size token in px (<see cref="MatTokens.Pixels"/>).</summary>
public sealed record MatTokenNumber(string Token, double Pixels);

/// <summary>Colour maths as the CSS does it: <c>color-mix(in oklab, ...)</c>, <c>color-mix(in srgb, x, transparent)</c> and the contrast of WCAG 2.</summary>
public static class MatColor
{
    /// <summary><c>color-mix(in oklab, a share, b)</c>: <paramref name="share"/> (0..1) is the part of <paramref name="a"/>. Both are taken as opaque.</summary>
    public static MatRgba Mix(MatRgba a, MatRgba b, double share)
    {
        var (l1, a1, b1) = ToOklab(a);
        var (l2, a2, b2) = ToOklab(b);
        return FromOklab(l1 * share + l2 * (1 - share), a1 * share + a2 * (1 - share), b1 * share + b2 * (1 - share));
    }

    /// <summary><c>color-mix(in srgb, colour share, transparent)</c>: the colour with <paramref name="share"/> as its opacity.</summary>
    public static MatRgba Transparent(MatRgba colour, double share) => colour.WithAlpha(colour.A * share);

    /// <summary>Relative luminance (WCAG 2).</summary>
    public static double Luminance(MatRgba c) => 0.2126 * ToLinear(c.R) + 0.7152 * ToLinear(c.G) + 0.0722 * ToLinear(c.B);

    /// <summary>The contrast ratio of WCAG 2, 1 to 21 (both taken as opaque: paint a tint over its background first, <see cref="MatRgba.Over"/>).</summary>
    public static double Contrast(MatRgba a, MatRgba b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        var (hi, lo) = la >= lb ? (la, lb) : (lb, la);
        return (hi + 0.05) / (lo + 0.05);
    }

    private static double ToLinear(double c)
    {
        c /= 255;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private static double FromLinear(double c)
    {
        var v = c <= 0.0031308 ? 12.92 * c : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;
        return Math.Clamp(v, 0, 1) * 255;
    }

    private static (double L, double A, double B) ToOklab(MatRgba c)
    {
        var r = ToLinear(c.R);
        var g = ToLinear(c.G);
        var b = ToLinear(c.B);
        var l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
        var m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
        var s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
        return (
            0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
            1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
            0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
    }

    private static MatRgba FromOklab(double lightness, double a, double b)
    {
        var l = Math.Pow(lightness + 0.3963377774 * a + 0.2158037573 * b, 3);
        var m = Math.Pow(lightness - 0.1055613458 * a - 0.0638541728 * b, 3);
        var s = Math.Pow(lightness - 0.0894841775 * a - 1.2914855480 * b, 3);
        return new MatRgba(
            FromLinear(4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s),
            FromLinear(-1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s),
            FromLinear(-0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s));
    }
}
