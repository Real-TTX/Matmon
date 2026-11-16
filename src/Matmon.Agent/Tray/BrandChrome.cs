#if WINDOWS
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using MatDesktop;

namespace Matmon.Agent.Tray;

/// <summary>
/// The window chrome of Matmon's own Windows programs - the instance setup (Matmon.Setup links this file) and the agent's
/// tray windows: the header with the mark, the button bar with the version on the left, headings, paragraphs and notices.
/// One copy, so the setup and the agent cannot drift apart.
/// <para>
/// Every colour comes from the tokens of MatUi through MatDesktop's copy in <c>src/Matmon.Agent/MatDesktop</c> (rule DESK-02 /
/// AGENT-05 of MatDesign; refreshed with <c>node tools/matdesktop-copy.js src/Matmon.Agent --tokens-only</c> in MatDesign, never
/// edited here): <see cref="Palette"/> is light mode for Matmon's accent. The trays and the setup have one mode, light - WinForms'
/// own controls (text boxes, check boxes, progress bars, message boxes) draw themselves in the system's light look.
/// A colour with an opacity (a hairline, a tint) is painted over the surface it lies on, because a WinForms colour cannot be translucent.
/// </para>
/// </summary>
internal static class BrandChrome
{
    /// <summary>Matmon's accent (MatDesign's <c>AppRegistry</c>, IDENT-01): the primary button and the links.</summary>
    public const string Accent = "#295cff";

    /// <summary>Every colour token of light mode for <see cref="Accent"/>, as MatUi computes them.</summary>
    public static readonly MatPalette Palette = MatPalette.For(Accent, dark: false);

    /// <summary>The header, the buttons and the fields (<c>--mat-surface</c>).</summary>
    public static readonly Color Surface = Solid("--mat-surface");

    /// <summary>The body of a window, under the header (<c>--mat-bg</c>).</summary>
    public static readonly Color Background = Solid("--mat-bg");

    /// <summary>The button bar (<c>--mat-nav-bg</c>, like the footer of MatDesktop's windows).</summary>
    public static readonly Color FooterBack = Solid("--mat-nav-bg");

    /// <summary>Titles, headings, the text of the body and of the fields (<c>--mat-text</c>).</summary>
    public static readonly Color Text = Solid("--mat-text");

    /// <summary>Labels, hints, the version, the word beside the name (<c>--mat-text-muted</c>).</summary>
    public static readonly Color Muted = Solid("--mat-text-muted");

    /// <summary>The line under the header (<c>--mat-border</c> on the surface).</summary>
    public static readonly Color Line = Solid("--mat-border", "--mat-surface");

    /// <summary>The line above the button bar (<c>--mat-border</c> on the bar).</summary>
    public static readonly Color FooterLine = Solid("--mat-border", "--mat-nav-bg");

    /// <summary>A field that cannot be typed into (<c>--mat-surface-3</c>).</summary>
    public static readonly Color ReadOnlyField = Solid("--mat-surface-3");

    /// <summary>A link (<c>--mat-accent-text</c>).</summary>
    public static readonly Color Link = Solid("--mat-accent-text");

    /// <summary>A sentence that says something failed (<c>--mat-danger-text</c>).</summary>
    public static readonly Color DangerText = Solid("--mat-danger-text");

    /// <summary>The font of a button: semibold, like MatUi's buttons.</summary>
    private static readonly Font ButtonFont = new("Segoe UI Semibold", 9.5f);

    /// <summary>A token as an opaque WinForms colour; a token with an opacity is painted over <paramref name="on"/>.</summary>
    public static Color Solid(string token, string on = "--mat-bg") => ToColor(Palette[token].Over(Palette[on]));

    public static Color ToColor(MatRgba colour) => Color.FromArgb(colour.A8, colour.R8, colour.G8, colour.B8);

    public static Font BodyFont() => new("Segoe UI", 9.5f);

    /// <summary>The channel of a build, from its version (<c>nightly-…</c>, <c>local-…</c>, else a release).</summary>
    public static string ChannelOf(string version) =>
        version.StartsWith("nightly", StringComparison.OrdinalIgnoreCase) ? "nightly"
        : version.StartsWith("local", StringComparison.OrdinalIgnoreCase) ? "local"
        : "release";

    /// <summary><c>Version 0.1.42-20261115 · release</c> - the one form of the version in every bar (SETUP-07).</summary>
    public static string VersionNote(string version) => $"Version {version} · {ChannelOf(version)}";

    /// <summary><c>0.1.42-20261115 · release</c> - the value of a row that is already labelled Version.</summary>
    public static string VersionValue(string version) => $"{version} · {ChannelOf(version)}";

    /// <summary>What the bar's version says on hover: a local build gets no updates (SETUP-07), any other nothing.</summary>
    public static string? LocalNote(string note) => note.EndsWith("· local", StringComparison.Ordinal) ? "A local build gets no updates." : null;

    /// <summary>
    /// A button for the bar: a fixed size and one margin rule (the gap to its left). AutoSize plus a margin of a button's own
    /// put it a few pixels higher than the others.
    /// </summary>
    public static Button FooterButton(string text, int width = 104, int gap = 8, ButtonLook look = ButtonLook.Secondary)
    {
        var button = new Button
        {
            Text = text,
            Size = new Size(width, 32),
            Margin = new Padding(gap, 0, 0, 0)
        };
        Style(button, look);
        return button;
    }

    /// <summary>
    /// A button in the flat look of MatUi: the primary action filled with the accent, a secondary one on the surface with a line,
    /// a destructive one in the danger colour (BUTTON-01). The colours of hover and pressed come from the tokens too.
    /// </summary>
    public static void Style(Button button, ButtonLook look)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        button.Font = ButtonFont;
        button.FlatAppearance.BorderSize = 1;
        button.Tag = look;
        button.EnabledChanged -= Restyle;
        button.EnabledChanged += Restyle;
        Paint(button, look);
    }

    private static void Restyle(object? sender, EventArgs e)
    {
        if (sender is Button { Tag: ButtonLook look } button)
        {
            Paint(button, look);
        }
    }

    private static void Paint(Button button, ButtonLook look)
    {
        button.Cursor = button.Enabled ? Cursors.Hand : Cursors.Default;
        if (!button.Enabled)
        {
            // WinForms draws the text of a disabled flat button in the system's grey whatever the look: a quiet, neutral button
            // (the filled accent with grey text on it could not be read).
            (button.BackColor, button.FlatAppearance.BorderColor) = (Solid("--mat-surface-3"), Solid("--mat-border", "--mat-surface-3"));
            return;
        }

        (button.BackColor, button.ForeColor, button.FlatAppearance.BorderColor, button.FlatAppearance.MouseOverBackColor, button.FlatAppearance.MouseDownBackColor) = look switch
        {
            ButtonLook.Primary => (Solid("--mat-accent"), Solid("--mat-on-accent"), Solid("--mat-accent"), Solid("--mat-accent-hover"), Solid("--mat-accent-active")),
            // MatDesktop's Mat.DangerLine: the danger colour at 45 % (computed in its Avalonia theme, not in the tokens).
            ButtonLook.Danger => (Surface, DangerText, ToColor(MatColor.Transparent(Palette.Danger, 0.45).Over(Palette.Surface)),
                Solid("--mat-danger-bg", "--mat-surface"), Solid("--mat-danger-bg", "--mat-surface")),
            _ => (Surface, Text, Solid("--mat-border-strong", "--mat-surface"), Solid("--mat-surface-3"), Solid("--mat-surface-3"))
        };
    }

    /// <summary>
    /// Gives what WinForms draws in the system's colours the colours of the tokens: fields, lists, links and buttons that were not
    /// styled yet (a Browse button). Called on a page once its controls are in place.
    /// </summary>
    public static void Theme(Control root)
    {
        foreach (Control control in root.Controls)
        {
            switch (control)
            {
                case TextBox box:
                    box.ForeColor = Text;
                    box.BackColor = box.ReadOnly ? ReadOnlyField : Surface;
                    box.ReadOnlyChanged -= FollowReadOnly;
                    box.ReadOnlyChanged += FollowReadOnly;
                    break;
                case ListBox or NumericUpDown:
                    control.ForeColor = Text;
                    control.BackColor = Surface;
                    break;
                case LinkLabel link:
                    link.LinkColor = link.ActiveLinkColor = link.VisitedLinkColor = Link;
                    break;
                case Button { FlatStyle: FlatStyle.Standard } button:
                    Style(button, ButtonLook.Secondary);
                    break;
            }

            Theme(control);
        }
    }

    private static void FollowReadOnly(object? sender, EventArgs e)
    {
        if (sender is TextBox box)
        {
            box.BackColor = box.ReadOnly ? ReadOnlyField : Surface;
        }
    }

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
            ForeColor = Muted
        };
        var footer = new Panel
        {
            Dock = DockStyle.Bottom,
            Padding = new Padding(20, 13, 16, 13),
            Height = 58,
            BackColor = FooterBack
        };
        // A hairline above the bar, like the footer of MatDesktop's windows.
        footer.Paint += (_, e) =>
        {
            using var line = new Pen(FooterLine);
            e.Graphics.DrawLine(line, 0, 0, footer.Width, 0);
        };
        footer.Controls.Add(label);
        footer.Controls.Add(row);
        SetNote(footer, note);
        return footer;
    }

    /// <summary>The text on the left of a bar made by <see cref="Footer"/>, with the local build's note as its tooltip.</summary>
    public static void SetNote(Panel footer, string note)
    {
        if (footer.Controls.OfType<Label>().FirstOrDefault() is not { } label)
        {
            return;
        }

        label.Text = note;
        if (footer.Tag is not ToolTip tip)
        {
            tip = new ToolTip();
            footer.Tag = tip;
            footer.Disposed += (_, _) => tip.Dispose();
        }

        tip.SetToolTip(label, LocalNote(note));
    }

    public static Label Heading(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font("Segoe UI Semibold", 14f),
        ForeColor = Text,
        Margin = new Padding(0, 0, 0, 12)
    };

    /// <summary>The width of the text of a window: 600 less the body's padding.</summary>
    public const int TextWidth = 544;

    public static Label Paragraph(string text, Color? color = null, int width = TextWidth) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(width, 0),
        ForeColor = color ?? Text,
        Margin = new Padding(0, 0, 0, 10)
    };
}

/// <summary>The three looks of a button (MatUi's <c>mat-btn--primary</c>, <c>--secondary</c>, <c>--danger</c>).</summary>
internal enum ButtonLook
{
    Secondary,
    Primary,
    Danger
}

/// <summary>The top band: the flat M mark beside the name and the mode, on the surface, with a line under it.</summary>
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
        BackColor = BrandChrome.Surface;
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
        using var title = new SolidBrush(BrandChrome.Text);
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
/// the header, a body that grows with its content, the button bar with version and channel on the left.
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
        BackColor = BrandChrome.Background;
        ForeColor = BrandChrome.Text;
        ClientSize = new Size(ClientWidth, 360);
        SetWindowIcon(null);

        _header = new BrandHeader(headerTitle, subtitle, palette) { Dock = DockStyle.Top, Height = 84 };
        _body.Controls.Add(Content);
    }

    /// <summary>Called once by the subclass when its content and buttons are in place.</summary>
    protected void Complete(string note, params Button[] buttons)
    {
        _footer = BrandChrome.Footer(note, buttons);
        BrandChrome.Theme(Content);
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
            if (_footer is not null)
            {
                BrandChrome.SetNote(_footer, value);
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
        ForeColor = BrandChrome.Text,
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

/// <summary>What an Info window shows: the state (the tray icon's colour, a word, a sentence) and key/value rows.</summary>
internal sealed record InfoView(Color State, string Title, string Detail, IReadOnlyList<KeyValuePair<string, string>> Rows);

/// <summary>
/// "Info" (TRAY-05) - ONE window for every tray (instance, probe, agent), so they cannot drift: the product in the header, the
/// state as a dot, a word and a sentence, the facts as rows on a card, and the bar with the version and the buttons Open (the
/// primary one), the tray's own, Close. Closing only hides it; the tray keeps it and refreshes it in place.
/// </summary>
internal sealed class BrandInfoForm : BrandWindow
{
    private const int KeyWidth = 140;

    private readonly StateLine _state = new(ContentWidth);
    private readonly FactsCard _rows = new(ContentWidth);

    /// <param name="buttons">The tray's own buttons, left to right (Open first); Close is added rightmost.</param>
    public BrandInfoForm(string product, MarkPalette? palette, string note, params Button[] buttons)
        : base($"{product} - Info", product, "Info", palette)
    {
        _rows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, KeyWidth));
        _rows.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var close = BrandChrome.FooterButton("Close", width: 88);
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
            _rows.Controls.Add(new Label { Text = key, AutoSize = true, ForeColor = BrandChrome.Muted, Margin = new Padding(0, 7, 12, 7) });
            _rows.Controls.Add(new Label
            {
                Text = value,
                AutoSize = true,
                ForeColor = BrandChrome.Text,
                MaximumSize = new Size(ContentWidth - KeyWidth - _rows.Padding.Horizontal, 0),
                Margin = new Padding(0, 7, 0, 7)
            });
        }

        _rows.ResumeLayout();
        _rows.Invalidate();
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

    /// <summary>The facts as rows on a card (MatUi's <c>mat-card</c>): the surface, a line around it and a line between two rows.</summary>
    private sealed class FactsCard : TableLayoutPanel
    {
        public FactsCard(int width)
        {
            ColumnCount = 2;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            MinimumSize = new Size(width, 0);
            MaximumSize = new Size(width, 0);
            Margin = new Padding(0, 4, 0, 0);
            Padding = new Padding(16, 2, 16, 2);
            BackColor = BrandChrome.Surface;
            DoubleBuffered = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var line = new Pen(BrandChrome.Line);
            var y = Padding.Top;
            var heights = GetRowHeights();
            for (var row = 0; row < heights.Length - 1; row++)
            {
                y += heights[row];
                e.Graphics.DrawLine(line, Padding.Left, y, Width - Padding.Right, y);
            }

            e.Graphics.DrawRectangle(line, 0, 0, Width - 1, Height - 1);
        }
    }

    /// <summary>The state as the tray icon says it: a dot in the icon's colour, a word, the sentence under it.</summary>
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
            return titleHeight + (int)(2 * Factor) + detailHeight + (int)(12 * Factor);
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

            TextRenderer.DrawText(graphics, _title, titleFont, new Point(TextLeft - (int)(3 * Factor), 0), BrandChrome.Text, TextFormatFlags.NoPadding);
            var detailTop = titleHeight + (int)(2 * Factor);
            TextRenderer.DrawText(graphics, _detail, Font, new Rectangle(TextLeft - (int)(3 * Factor), detailTop, Width - TextLeft, Height - detailTop),
                BrandChrome.Muted, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
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

/// <summary>A notice like MatUi's alert: a box tinted in the tone's colour, the text in the tone's text colour, wrapping inside - a tint, no bar.</summary>
internal sealed class BrandNotice : Label
{
    private NoticeTone _tone;

    public BrandNotice(int width)
    {
        AutoSize = true;
        MaximumSize = new Size(width, 0);
        MinimumSize = new Size(width, 0);
        Padding = new Padding(12, 9, 12, 9);
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
            // The tints are painted over the body of the window (--mat-bg), where the notices stand.
            (BackColor, ForeColor) = value switch
            {
                NoticeTone.Success => (BrandChrome.Solid("--mat-success-bg"), BrandChrome.Solid("--mat-success-text")),
                NoticeTone.Warning => (BrandChrome.Solid("--mat-warning-bg"), BrandChrome.Solid("--mat-warning-text")),
                NoticeTone.Error => (BrandChrome.Solid("--mat-danger-bg"), BrandChrome.Solid("--mat-danger-text")),
                _ => (BrandChrome.Solid("--mat-info-bg"), BrandChrome.Solid("--mat-info-text"))
            };
            Invalidate();
        }
    }
}
#endif
