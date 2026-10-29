#if WINDOWS
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Matmon.Agent.Tray;

/// <summary>
/// The window chrome of Matmon's own Windows programs - the instance setup (Matmon.Setup links this file) and the agent's
/// tray windows: the matmon.eu header, the grey button bar with the version on the left, headings, paragraphs and notices.
/// One copy, so the setup and the agent cannot drift apart.
/// </summary>
internal static class BrandChrome
{
    public static readonly Color Ink = Color.FromArgb(0x0F, 0x1B, 0x2D);
    public static readonly Color HeadingColor = Color.FromArgb(0x1B, 0x26, 0x38);
    public static readonly Color TextColor = Color.FromArgb(0x3A, 0x45, 0x55);
    public static readonly Color Muted = Color.FromArgb(0x5B, 0x67, 0x78);
    public static readonly Color FooterBack = Color.FromArgb(0xF4, 0xF6, 0xF9);
    public static readonly Color Line = Color.FromArgb(0xE3, 0xE8, 0xEF);

    public static Font BodyFont() => new("Segoe UI", 9.5f);

    /// <summary>
    /// A button for the bar: a fixed size and one margin rule (the gap to its left). AutoSize plus a margin of a button's own
    /// put it a few pixels higher than the others.
    /// </summary>
    public static Button FooterButton(string text, int width = 104, int gap = 8) => new()
    {
        Text = text,
        Size = new Size(width, 32),
        Margin = new Padding(gap, 0, 0, 0)
    };

    /// <summary>The bar: <paramref name="note"/> (version, channel) on the left, the buttons on the right - the first one rightmost - on one line.</summary>
    public static Panel Footer(string note, params Button[] buttons)
    {
        var row = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = Padding.Empty,
            Margin = Padding.Empty
        };
        row.Controls.AddRange(buttons);
        var label = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            ForeColor = Muted,
            Text = note
        };
        var footer = new Panel
        {
            Dock = DockStyle.Bottom,
            Padding = new Padding(20, 13, 16, 13),
            Height = 58,
            BackColor = FooterBack
        };
        footer.Controls.Add(label);
        footer.Controls.Add(row);
        return footer;
    }

    public static Label Heading(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font("Segoe UI Semibold", 14f),
        ForeColor = HeadingColor,
        Margin = new Padding(0, 0, 0, 12)
    };

    public static Label Paragraph(string text, Color? color = null, int width = 560) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(width, 0),
        ForeColor = color ?? TextColor,
        Margin = new Padding(0, 0, 0, 10)
    };
}

/// <summary>The top band, like the header of matmon.eu: the flat M mark beside the name on a light ground.</summary>
internal sealed class BrandHeader : Control
{
    private readonly string _title;
    private readonly string _subtitle;

    public BrandHeader(string title, string subtitle)
    {
        _title = title;
        _subtitle = subtitle;
        DoubleBuffered = true;
        BackColor = Color.White;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        // Pixel lengths follow the monitor (fonts already do); 1 at 100 %.
        var scale = DeviceDpi / 96f;
        var inset = 24 * scale;
        var markHeight = 40f * scale;
        var markWidth = markHeight * TrayIcons.MarkWidth / TrayIcons.MarkHeight;
        var top = (Height - markHeight) / 2f;
        TrayIcons.DrawMark(graphics, new RectangleF(inset, top, markWidth, markHeight));

        using var titleFont = new Font("Segoe UI Semibold", 20f);
        using var subtitleFont = new Font("Segoe UI", 12f);
        using var title = new SolidBrush(BrandChrome.Ink);
        using var soft = new SolidBrush(BrandChrome.Muted);
        var left = inset + markWidth + 14 * scale;
        var titleSize = graphics.MeasureString(_title, titleFont);
        var subtitleSize = graphics.MeasureString(_subtitle, subtitleFont);
        graphics.DrawString(_title, titleFont, title, left, (Height - titleSize.Height) / 2f);
        graphics.DrawString(_subtitle, subtitleFont, soft, left + titleSize.Width + 6 * scale, (Height - subtitleSize.Height) / 2f + 2 * scale);

        using var line = new Pen(BrandChrome.Line);
        graphics.DrawLine(line, 0, Height - 1, Width, Height - 1);
    }
}

internal enum NoticeTone
{
    Info,
    Success,
    Warning,
    Error
}

/// <summary>A notice like MatUi's alert: a tinted box with a bar in the tone's colour on the left, the text wrapping inside.</summary>
internal sealed class BrandNotice : Label
{
    private NoticeTone _tone;

    public BrandNotice(int width)
    {
        AutoSize = true;
        MaximumSize = new Size(width, 0);
        MinimumSize = new Size(width, 0);
        Padding = new Padding(14, 9, 12, 9);
        Margin = new Padding(0, 4, 0, 10);
        Tone = NoticeTone.Info;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public NoticeTone Tone
    {
        get => _tone;
        set
        {
            _tone = value;
            (BackColor, ForeColor) = value switch
            {
                NoticeTone.Success => (Color.FromArgb(0xE9, 0xF8, 0xF0), Color.FromArgb(0x1C, 0x6B, 0x43)),
                NoticeTone.Warning => (Color.FromArgb(0xFF, 0xF6, 0xE5), Color.FromArgb(0x7A, 0x4E, 0x00)),
                NoticeTone.Error => (Color.FromArgb(0xFD, 0xEC, 0xEE), Color.FromArgb(0x9B, 0x1C, 0x2E)),
                _ => (Color.FromArgb(0xEE, 0xF4, 0xFB), Color.FromArgb(0x1F, 0x3B, 0x63))
            };
            Invalidate();
        }
    }

    private Color Bar => _tone switch
    {
        NoticeTone.Success => TrayIcons.Ok,
        NoticeTone.Warning => TrayIcons.Warning,
        NoticeTone.Error => TrayIcons.Error,
        _ => Color.FromArgb(0x1A, 0x6F, 0xD0)
    };

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var bar = new SolidBrush(Bar);
        e.Graphics.FillRectangle(bar, 0, 0, Math.Max(3, (int)Math.Round(4 * DeviceDpi / 96f)), Height);
    }
}
#endif
