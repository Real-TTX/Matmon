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
    private string _subtitle;
    private readonly MarkPalette? _palette;

    /// <summary>The word beside the name ("Setup", "Probe setup", "Info") - the setup changes it once a role is chosen.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Subtitle
    {
        get => _subtitle;
        set
        {
            _subtitle = value;
            Invalidate();
        }
    }

    public BrandHeader(string title, string subtitle, MarkPalette? palette = null)
    {
        _palette = palette;
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
        TrayIcons.DrawMark(graphics, new RectangleF(inset, top, markWidth, markHeight), _palette);

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

/// <summary>
/// The frame of every small window of Matmon's Windows programs - the agent's Set up and Info, the instance tray's Info:
/// the matmon.eu header, a white body that grows with its content, the grey button bar with version and channel on the left.
/// (The setup wizard is the one bigger window; it uses the same header and bar.)
/// </summary>
internal abstract class BrandWindow : Form
{
    public const int ClientWidth = 600;
    public const int ContentWidth = ClientWidth - 56;

    private readonly BrandHeader _header;
    private readonly Panel _body = new() { Dock = DockStyle.Fill, Padding = new Padding(28, 22, 28, 12) };
    private readonly MarkPalette? _palette;
    private Panel? _footer;
    private Icon? _windowIcon;

    protected readonly FlowLayoutPanel Content = new()
    {
        Dock = DockStyle.Top,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        Margin = Padding.Empty,
        Padding = Padding.Empty
    };

    protected BrandWindow(string windowTitle, string headerTitle, string subtitle, MarkPalette? palette)
    {
        SuspendLayout();
        _palette = palette;
        // Laid out at 96 dpi and scaled by Windows' factor for the monitor (the trays run PerMonitorV2).
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = windowTitle;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = BrandChrome.BodyFont();
        BackColor = Color.White;
        ClientSize = new Size(ClientWidth, 360);
        SetWindowIcon(null);

        _header = new BrandHeader(headerTitle, subtitle, palette) { Dock = DockStyle.Top, Height = 84 };
        _body.Controls.Add(Content);
    }

    /// <summary>Called once by the subclass when its content and buttons are in place.</summary>
    protected void Complete(string note, params Button[] buttons)
    {
        _footer = BrandChrome.Footer(note, buttons);
        Controls.Add(_body);
        Controls.Add(_footer);
        Controls.Add(_header);
        ResumeLayout(false);
        PerformLayout();
        FitHeight();
    }

    /// <summary>The text on the left of the button bar (version and channel).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Note
    {
        get => _footer?.Controls.OfType<Label>().FirstOrDefault()?.Text ?? string.Empty;
        set
        {
            if (_footer?.Controls.OfType<Label>().FirstOrDefault() is { } label)
            {
                label.Text = value;
            }
        }
    }

    /// <summary>The window is as tall as what it shows - no scrollbar, no empty half.</summary>
    public void FitHeight()
    {
        if (_footer is null)
        {
            return;
        }

        Content.PerformLayout();
        var content = Content.GetPreferredSize(new Size(Content.Width, 0)).Height;
        ClientSize = new Size(ClientSize.Width, _header.Height + _body.Padding.Vertical + content + _footer.Height);
    }

    protected void SetWindowIcon(Color? state)
    {
        var previous = _windowIcon;
        _windowIcon = TrayIcons.Create(state, _palette);
        Icon = _windowIcon;
        previous?.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _windowIcon?.Dispose();
        }

        base.Dispose(disposing);
    }

    public static Label FieldLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = BrandChrome.HeadingColor,
        Margin = new Padding(0, 4, 0, 3)
    };

    public static Label Hint(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(ContentWidth, 0),
        ForeColor = BrandChrome.Muted,
        Margin = new Padding(0, 3, 0, 10)
    };
}

/// <summary>What an Info window shows: the state (the tray icon's colour, a word, a line of detail) and key/value rows.</summary>
internal sealed record InfoView(Color State, string Title, string Detail, IReadOnlyList<KeyValuePair<string, string>> Rows);

/// <summary>
/// "Info" - ONE window for every tray (instance, probe, agent), so they cannot drift: the product in the header, the state
/// line, what it is connected to and which version runs, and the version + channel in the bar. Closing only hides it; the
/// tray keeps it and refreshes it in place.
/// </summary>
internal sealed class BrandInfoForm : BrandWindow
{
    private const int KeyWidth = 140;

    private readonly StateLine _state = new(ContentWidth);
    private readonly TableLayoutPanel _rows = new()
    {
        ColumnCount = 2,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Margin = new Padding(0, 6, 0, 0),
        Padding = Padding.Empty
    };

    /// <param name="buttons">The tray's own buttons, left to right (Open ..., Open log folder); Close is added rightmost.</param>
    public BrandInfoForm(string product, MarkPalette? palette, string note, params Button[] buttons)
        : base($"{product} - Info", product, "Info", palette)
    {
        _rows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, KeyWidth));
        _rows.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var close = BrandChrome.FooterButton("Close", width: 88, gap: 16);
        close.Click += (_, _) => Hide();
        CancelButton = close;
        Content.Controls.AddRange([_state, _rows]);
        Complete(note, [close, .. Enumerable.Reverse(buttons)]);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnFormClosing(e);
    }

    public void ShowInfo(InfoView view)
    {
        if (_state.Set(view.State, view.Title, view.Detail))
        {
            SetWindowIcon(view.State);
        }

        _rows.SuspendLayout();
        _rows.Controls.Clear();
        _rows.RowStyles.Clear();
        foreach (var (key, value) in view.Rows)
        {
            _rows.Controls.Add(new Label { Text = key, AutoSize = true, ForeColor = BrandChrome.Muted, Margin = new Padding(0, 4, 12, 4) });
            _rows.Controls.Add(new Label
            {
                Text = value,
                AutoSize = true,
                ForeColor = BrandChrome.TextColor,
                MaximumSize = new Size(ContentWidth - KeyWidth, 0),
                Margin = new Padding(0, 4, 0, 4)
            });
        }

        _rows.ResumeLayout();
        FitHeight();
    }

    /// <summary>"12 s ago" / "5 min ago" / "3 h ago".</summary>
    public static string Ago(DateTimeOffset when)
    {
        var elapsed = DateTimeOffset.UtcNow - when;
        return elapsed.TotalSeconds < 90 ? $"{Math.Max(0, (int)elapsed.TotalSeconds)} s ago"
            : elapsed.TotalMinutes < 90 ? $"{(int)elapsed.TotalMinutes} min ago"
            : $"{(int)elapsed.TotalHours} h ago";
    }

    /// <summary>The state as the tray icon says it: a dot in the icon's colour, a word, a line of detail, a rule under it.</summary>
    private sealed class StateLine : Control
    {
        private Color _color = TrayIcons.Neutral;
        private string _title = string.Empty;
        private string _detail = string.Empty;

        public StateLine(int width)
        {
            DoubleBuffered = true;
            Margin = new Padding(0, 0, 0, 6);
            Size = new Size(width, 60);
        }

        /// <summary>Returns whether the colour changed (the window icon follows it).</summary>
        public bool Set(Color color, string title, string detail)
        {
            var changed = color != _color || _title.Length == 0;
            (_color, _title, _detail) = (color, title, detail);
            Height = Measure();
            Invalidate();
            return changed;
        }

        private float Factor => DeviceDpi / 96f;

        private static Font TitleFont() => new("Segoe UI Semibold", 13f);

        private int TextLeft => (int)(26 * Factor);

        private int Measure()
        {
            using var titleFont = TitleFont();
            var titleHeight = TextRenderer.MeasureText(_title, titleFont).Height;
            var detailHeight = TextRenderer.MeasureText(_detail, Font, new Size(Width - TextLeft, 0), TextFormatFlags.WordBreak).Height;
            return titleHeight + (int)(2 * Factor) + detailHeight + (int)(16 * Factor);
        }

        protected override void ScaleControl(SizeF factor, BoundsSpecified specified)
        {
            base.ScaleControl(factor, specified);
            Height = Measure();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var graphics = e.Graphics;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var titleFont = TitleFont();
            var titleHeight = TextRenderer.MeasureText(_title, titleFont).Height;

            var dot = 12 * Factor;
            using var brush = new SolidBrush(_color);
            graphics.FillEllipse(brush, 2 * Factor, (titleHeight - dot) / 2f, dot, dot);

            TextRenderer.DrawText(graphics, _title, titleFont, new Point(TextLeft - (int)(3 * Factor), 0), BrandChrome.HeadingColor, TextFormatFlags.NoPadding);
            var detailTop = titleHeight + (int)(2 * Factor);
            TextRenderer.DrawText(graphics, _detail, Font, new Rectangle(TextLeft - (int)(3 * Factor), detailTop, Width - TextLeft, Height - detailTop),
                BrandChrome.Muted, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);

            using var line = new Pen(BrandChrome.Line);
            graphics.DrawLine(line, 0, Height - 1, Width, Height - 1);
        }
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
