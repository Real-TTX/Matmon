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
    public static readonly Color Ok = Color.FromArgb(0x3F, 0xC4, 0x7A);
    public static readonly Color Warning = Color.FromArgb(0xF2, 0xB1, 0x3C);
    public static readonly Color Error = Color.FromArgb(0xE5, 0x4B, 0x5E);
    public static readonly Color Neutral = Color.FromArgb(0x9A, 0xA4, 0xB1);

    /// <summary>The mark's own size (the SVG's viewBox).</summary>
    public const float MarkWidth = 444;
    public const float MarkHeight = 352;

    // brand/matmon-mark-flat.svg, unchanged - M, L, Q and Z only. The flat mark, not the shaded one: the shaded mark's dark
    // left flank sinks into a dark background below ~40 px and the M reads as a V.
    private const string MarkPath = "M74.6 2Q78 0 82 0L90 0Q93.7 0 96.8 2.5L224.6 107.3Q228.5 110.5 232.4 107.4L366.3 2.5Q369.4 0 372.8 2.1L440.6 44.4Q444 46.5 444 51.5L444 290.7Q444 295.7 440.5 298.2L350 352Q328 352 328 322L328 160L229.4 247.3Q226.4 250 222.4 250L215.1 250Q211.1 250 208.2 247.2L116 158.3L116 322Q116 352 94 352L4.3 294.9Q0 292.2 0 287.2L0 51.4Q0 46.4 4 43.9Z";

    public static Icon Create(Color state)
    {
        const int size = 32;
        using var bitmap = new Bitmap(size, size);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.Clear(Color.Transparent);

            // The mark across the full width, kept clear of the bottom-right corner the dot sits in.
            var height = size * MarkHeight / MarkWidth;
            DrawMark(graphics, new RectangleF(0, 2, size, height));

            // State: a dot in the corner, outlined so it reads on light and dark taskbars alike.
            using var dot = new SolidBrush(state);
            using var outline = new Pen(Color.White, 2f);
            graphics.FillEllipse(dot, 18, 18, 13, 13);
            graphics.DrawEllipse(outline, 18, 18, 13, 13);
        }

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

    /// <summary>Draws the mark with its gradient into <paramref name="bounds"/> - a vector, so it is sharp at any size.</summary>
    public static void DrawMark(Graphics graphics, RectangleF bounds)
    {
        using var path = BuildMarkPath();
        using var transform = new Matrix();
        transform.Translate(bounds.X, bounds.Y);
        transform.Scale(bounds.Width / MarkWidth, bounds.Height / MarkHeight);
        path.Transform(transform);

        // The SVG's gradient: user space (0,0) -> (444,200), three stops.
        var from = new PointF(bounds.X, bounds.Y);
        var to = new PointF(bounds.X + bounds.Width, bounds.Y + 200 * bounds.Height / MarkHeight);
        using var brush = new LinearGradientBrush(from, to, Color.Black, Color.Black)
        {
            InterpolationColors = new ColorBlend
            {
                Colors = [Color.FromArgb(0x1A, 0x6F, 0xD0), Color.FromArgb(0x12, 0xA8, 0xDC), Color.FromArgb(0x17, 0xC2, 0xBC)],
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
#endif
