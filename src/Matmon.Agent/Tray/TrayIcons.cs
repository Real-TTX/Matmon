#if WINDOWS
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Matmon.Agent.Tray;

/// <summary>
/// The tray icons, drawn at start-up rather than shipped as files: the brand tile plus a status dot. Shared by the agent's
/// tray and the Windows instance's (Matmon.Setup links this file), so both look like one family.
/// </summary>
internal static class TrayIcons
{
    public static readonly Color Ok = Color.FromArgb(0x3F, 0xC4, 0x7A);
    public static readonly Color Warning = Color.FromArgb(0xF2, 0xB1, 0x3C);
    public static readonly Color Error = Color.FromArgb(0xE5, 0x4B, 0x5E);
    public static readonly Color Neutral = Color.FromArgb(0x9A, 0xA4, 0xB1);

    public static Icon Create(Color state)
    {
        const int size = 32;
        using var bitmap = new Bitmap(size, size);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            // The brand gradient (hue 211 -> 180) on a rounded tile, a white M on top.
            using var tile = RoundedRectangle(new Rectangle(1, 1, 26, 26), 6);
            using var gradient = new LinearGradientBrush(new Point(0, 0), new Point(28, 28), Color.FromArgb(0x2B, 0x7B, 0xD6), Color.FromArgb(0x1F, 0xB5, 0xB0));
            graphics.FillPath(gradient, tile);
            using var pen = new Pen(Color.White, 3.2f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
            graphics.DrawLines(pen, [new PointF(7, 20), new PointF(7, 8), new PointF(14, 15), new PointF(21, 8), new PointF(21, 20)]);

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

    private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
#endif
