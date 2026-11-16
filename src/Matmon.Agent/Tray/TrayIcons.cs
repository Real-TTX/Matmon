#if WINDOWS
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Matmon.Agent.Tray;

/// <summary>
/// The Matmon mark and the tray icons drawn from it at start-up rather than shipped as files: the flat M of matmon.eu plus a
/// status dot. Shared by the agent's tray and the Windows instance's tray and setup (Matmon.Setup links this file), so all
/// of them show the same mark as the website.
/// </summary>
internal static partial class TrayIcons
{
    // The four states of TRAY-02, in the status colours of the tokens (light mode, like MatDesktop's tray icons): green running,
    // amber waiting or attention, red stopped or an error, grey not set up. The words go with them (menu, tooltip, Info).
    public static readonly Color Ok = BrandChrome.Solid("--mat-success");
    public static readonly Color Warning = BrandChrome.Solid("--mat-warning");
    public static readonly Color Error = BrandChrome.Solid("--mat-danger");
    public static readonly Color Neutral = BrandChrome.Solid("--mat-text-muted");

    /// <summary>The mark's own size (the SVG's viewBox).</summary>
    public const float MarkWidth = 444;
    public const float MarkHeight = 352;

    // brand/matmon-mark-flat.svg, unchanged - M, L, Q and Z only. The flat mark, not the shaded one: the shaded mark's dark
    // left flank sinks into a dark background below ~40 px and the M reads as a V.
    private const string MarkPath = "M74.6 2Q78 0 82 0L90 0Q93.7 0 96.8 2.5L224.6 107.3Q228.5 110.5 232.4 107.4L366.3 2.5Q369.4 0 372.8 2.1L440.6 44.4Q444 46.5 444 51.5L444 290.7Q444 295.7 440.5 298.2L350 352Q328 352 328 322L328 160L229.4 247.3Q226.4 250 222.4 250L215.1 250Q211.1 250 208.2 247.2L116 158.3L116 322Q116 352 94 352L4.3 294.9Q0 292.2 0 287.2L0 51.4Q0 46.4 4 43.9Z";

    /// <summary>The mark with a state dot; without <paramref name="state"/> the mark alone (a window's icon).</summary>
    public static Icon Create(Color? state, MarkPalette? palette = null)
    {
        const int size = 32;
        using var bitmap = Draw(state, palette, size);
        var handle = bitmap.GetHicon();
        try
        {
            // Clone so the icon owns its data and the native handle can be released right away.
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    /// <summary>
    /// The picture of <see cref="Create"/>, <paramref name="size"/> px square: the mark, and the state as a dot at the bottom right that
    /// stands in a cut-out of the mark - a ring of transparency, so the dot reads on a light and on a dark taskbar alike and never
    /// touches the mark (TRAY-02; the proportions of MatDesktop's tray icons). Drawn four times as large and scaled down, because a
    /// GDI+ clip has hard edges.
    /// </summary>
    public static Bitmap Draw(Color? state, MarkPalette? palette, int size)
    {
        const int over = 4;
        var large = size * over;
        using var big = new Bitmap(large, large);
        using (var graphics = Graphics.FromImage(big))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.Clear(Color.Transparent);

            var height = large * MarkHeight / MarkWidth;
            var mark = new RectangleF(0, (large - height) / 2, large, height);
            if (state is { } color)
            {
                var center = large * 0.75f;
                var radius = large * 0.22f;
                var gap = large * 0.08f;
                using var cut = new GraphicsPath();
                cut.AddEllipse(center - radius - gap, center - radius - gap, (radius + gap) * 2, (radius + gap) * 2);
                using var clip = new Region(new RectangleF(0, 0, large, large));
                clip.Exclude(cut);
                graphics.Clip = clip;
                DrawMark(graphics, mark, palette);
                graphics.ResetClip();
                using var dot = new SolidBrush(color);
                graphics.FillEllipse(dot, center - radius, center - radius, radius * 2, radius * 2);
            }
            else
            {
                DrawMark(graphics, mark, palette);
            }
        }

        var bitmap = new Bitmap(size, size);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(big, new Rectangle(0, 0, size, size));
        }

        return bitmap;
    }

    /// <summary>Draws the mark with its gradient into <paramref name="bounds"/> - a vector, so it is sharp at any size.</summary>
    public static void DrawMark(Graphics graphics, RectangleF bounds, MarkPalette? palette = null)
    {
        using var path = BuildMarkPath();
        using var transform = new Matrix();
        transform.Translate(bounds.X, bounds.Y);
        transform.Scale(bounds.Width / MarkWidth, bounds.Height / MarkHeight);
        path.Transform(transform);

        // The SVG's gradient: user space (0,0) -> (444,200), three stops.
        var from = new PointF(bounds.X, bounds.Y);
        var to = new PointF(bounds.X + bounds.Width, bounds.Y + 200 * bounds.Height / MarkHeight);
        // The two colours of the constructor are placeholders: InterpolationColors replaces them.
        using var brush = new LinearGradientBrush(from, to, Color.Transparent, Color.Transparent)
        {
            InterpolationColors = new ColorBlend
            {
                Colors = (palette ?? MarkPalette.Brand).Colors,
                Positions = [0f, 0.55f, 1f]
            },
            WrapMode = WrapMode.TileFlipX
        };
        graphics.FillPath(brush, path);
    }

    private static GraphicsPath BuildMarkPath()
    {
        var path = new GraphicsPath();
        var tokens = PathToken().Matches(MarkPath).Select(match => match.Value).ToArray();
        var current = PointF.Empty;
        var index = 0;
        float Next() => float.Parse(tokens[index++], CultureInfo.InvariantCulture);
        while (index < tokens.Length)
        {
            switch (tokens[index++])
            {
                case "M":
                    path.StartFigure();
                    current = new PointF(Next(), Next());
                    break;
                case "L":
                    var line = new PointF(Next(), Next());
                    path.AddLine(current, line);
                    current = line;
                    break;
                case "Q":
                    // A quadratic curve is the cubic whose control points lie two thirds of the way to the quadratic one.
                    var control = new PointF(Next(), Next());
                    var end = new PointF(Next(), Next());
                    path.AddBezier(
                        current,
                        new PointF(current.X + 2f / 3 * (control.X - current.X), current.Y + 2f / 3 * (control.Y - current.Y)),
                        new PointF(end.X + 2f / 3 * (control.X - end.X), end.Y + 2f / 3 * (control.Y - end.Y)),
                        end);
                    current = end;
                    break;
                case "Z":
                    path.CloseFigure();
                    break;
            }
        }

        return path;
    }

    [GeneratedRegex(@"[MLQZ]|-?\d*\.?\d+")]
    private static partial Regex PathToken();

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}

/// <summary>
/// The colours of the mark's gradient (three stops along the SVG's diagonal). <see cref="Brand"/> is Matmon's own - the instance's tray and
/// setup and every window header; <see cref="Agent"/> tells the agent's tray icon apart from the instance's on a machine that
/// runs both: violet to blue instead of blue to teal, bright enough to read on a dark taskbar and saturated enough on a light one.
/// These are the colours of the LOGO (brand/matmon-mark-flat.svg, scripts/make-windows-icon.cs), the only literal colours of the
/// trays and the setup; everything a window paints comes from the tokens (<see cref="BrandChrome"/>).
/// </summary>
internal sealed record MarkPalette(Color[] Colors)
{
    public static readonly MarkPalette Brand = new([Color.FromArgb(0x1A, 0x6F, 0xD0), Color.FromArgb(0x12, 0xA8, 0xDC), Color.FromArgb(0x17, 0xC2, 0xBC)]);

    public static readonly MarkPalette Agent = new([Color.FromArgb(0x8B, 0x4D, 0xF0), Color.FromArgb(0x63, 0x5B, 0xF2), Color.FromArgb(0x2F, 0x86, 0xF0)]);
}
#endif
