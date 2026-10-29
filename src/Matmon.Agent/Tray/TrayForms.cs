#if WINDOWS
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;
using Matmon.Probe;

namespace Matmon.Agent.Tray;

/// <summary>
/// The frame both agent windows share, in the look of the instance setup (<see cref="BrandChrome"/>): the matmon.eu
/// header, a white body that grows with its content, the grey button bar with the version on the left.
/// </summary>
internal abstract class AgentWindow : Form
{
    protected const int ClientWidth = 600;
    protected const int ContentWidth = ClientWidth - 56;

    private readonly BrandHeader _header;
    private readonly Panel _body = new() { Dock = DockStyle.Fill, Padding = new Padding(28, 22, 28, 12) };
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

    protected AgentWindow(string title, string subtitle)
    {
        SuspendLayout();
        // Laid out at 96 dpi and scaled by Windows' factor for the monitor (the tray runs PerMonitorV2).
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = BrandChrome.BodyFont();
        BackColor = Color.White;
        ClientSize = new Size(ClientWidth, 360);
        SetWindowIcon(null);

        _header = new BrandHeader("Matmon Agent", subtitle) { Dock = DockStyle.Top, Height = 84 };
        _body.Controls.Add(Content);
    }

    /// <summary>Called once by the subclass when its content and buttons are in place.</summary>
    protected void Complete(params Button[] buttons)
    {
        _footer = BrandChrome.Footer($"Version {MatmonVersion.Current}", buttons);
        Controls.Add(_body);
        Controls.Add(_footer);
        Controls.Add(_header);
        ResumeLayout(false);
        PerformLayout();
        FitHeight();
    }

    /// <summary>The window is as tall as what it shows - no scrollbar, no empty half.</summary>
    protected void FitHeight()
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
        _windowIcon = TrayIcons.Create(state);
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

    protected static Label FieldLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = BrandChrome.HeadingColor,
        Margin = new Padding(0, 4, 0, 3)
    };

    protected static Label Hint(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(ContentWidth, 0),
        ForeColor = BrandChrome.Muted,
        Margin = new Padding(0, 3, 0, 10)
    };
}

/// <summary>What the agent is doing right now - read from the service, refreshed with the tray.</summary>
internal sealed class StatusForm : AgentWindow
{
    private const int KeyWidth = 128;

    private readonly TrayApplication _tray;
    private readonly StateLine _state = new(ContentWidth);
    private readonly TableLayoutPanel _rows = new()
    {
        ColumnCount = 2,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Margin = new Padding(0, 6, 0, 0),
        Padding = Padding.Empty
    };
    private readonly Button _openInstance = BrandChrome.FooterButton("Open instance", width: 124);

    public StatusForm(TrayApplication tray) : base("Matmon Agent", "Status")
    {
        _tray = tray;
        _rows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, KeyWidth));
        _rows.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _openInstance.Click += (_, _) => _tray.OpenInstance();
        var logs = BrandChrome.FooterButton("Open log folder", width: 124);
        logs.Click += (_, _) => _tray.OpenLogFolder();
        var close = BrandChrome.FooterButton("Close", gap: 16);
        close.Click += (_, _) => Hide();
        CancelButton = close;

        Content.Controls.AddRange([_state, _rows]);
        Complete(close, logs, _openInstance);
    }

    // Closing only hides: the tray keeps the window and refreshes it in place.
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnFormClosing(e);
    }

    public void Show(AgentStatusSnapshot? status, TrayState state, string description)
    {
        var color = state switch
        {
            TrayState.Connected => TrayIcons.Ok,
            TrayState.Unreachable => TrayIcons.Warning,
            TrayState.Stopped => TrayIcons.Error,
            _ => TrayIcons.Neutral
        };
        var (title, detail) = state switch
        {
            TrayState.Connected => ("Connected", $"{status?.ProbeName ?? "This machine"} reports to its Matmon instance."),
            TrayState.Unreachable => ("Instance not reachable", status?.StatusMessage is { Length: > 0 } message ? message : Capitalize(description)),
            TrayState.Stopped => ("Service not running", "The \"Matmon Agent\" service is installed but not running."),
            _ => ("Not set up", "This machine is not connected to a Matmon instance yet.")
        };
        if (_state.Set(color, title, detail))
        {
            SetWindowIcon(color);
        }

        _openInstance.Enabled = !string.IsNullOrWhiteSpace(status?.PrimaryUrl);

        _rows.SuspendLayout();
        _rows.Controls.Clear();
        _rows.RowStyles.Clear();
        if (status is null)
        {
            Row("Service", state == TrayState.Stopped ? "installed, not running" : "not installed");
            Row("Next step", state == TrayState.Stopped ? "start the \"Matmon Agent\" service" : "Set up… from the tray menu");
        }
        else
        {
            Row("Probe", $"{status.ProbeName} ({status.ProbeId})");
            Row("Instance", status.PrimaryUrl ?? "-");
            Row("Last heartbeat", status.LastHeartbeatUtc is { } beat ? $"{beat.ToLocalTime():g} ({Ago(beat)})" : "none yet");
            Row("Sensors", status.AssignedSensorCount.ToString());
            Row("Version", status.Version);
            Row("Last update", status.UpdateStatus ?? "none");
            Row("Running since", status.StartedUtc.ToLocalTime().ToString("g"));
            Row("Config", status.ConfigPath);
        }

        _rows.ResumeLayout();
        FitHeight();
    }

    private void Row(string label, string value)
    {
        _rows.Controls.Add(new Label { Text = label, AutoSize = true, ForeColor = BrandChrome.Muted, Margin = new Padding(0, 4, 12, 4) });
        _rows.Controls.Add(new Label
        {
            Text = value,
            AutoSize = true,
            ForeColor = BrandChrome.TextColor,
            MaximumSize = new Size(ContentWidth - KeyWidth, 0),
            Margin = new Padding(0, 4, 0, 4)
        });
    }

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static string Ago(DateTimeOffset when)
    {
        var elapsed = DateTimeOffset.UtcNow - when;
        return elapsed.TotalSeconds < 90 ? $"{Math.Max(0, (int)elapsed.TotalSeconds)} s ago"
            : elapsed.TotalMinutes < 90 ? $"{(int)elapsed.TotalMinutes} min ago"
            : $"{(int)elapsed.TotalHours} h ago";
    }

    /// <summary>The state as the tray icon says it: a dot in the icon's colour, a word, a line of detail, a rule under it.</summary>
    private sealed class StateLine : Control
    {
        private readonly int _width;
        private Color _color = TrayIcons.Neutral;
        private string _title = string.Empty;
        private string _detail = string.Empty;

        public StateLine(int width)
        {
            _width = width;
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

        private Font TitleFont() => new("Segoe UI Semibold", 13f);

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
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
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

/// <summary>
/// "Set up": the instance URL and a code from its Agents page. Runs <c>setup</c> elevated (one UAC prompt),
/// which enrols, installs to Program Files, creates or restarts the service and registers this tray.
/// </summary>
internal sealed class SetupForm : AgentWindow
{
    private readonly TrayApplication _tray;
    private readonly TextBox _url = new() { Width = ContentWidth, PlaceholderText = "https://matmon.example", Margin = Padding.Empty };
    private readonly TextBox _code = new() { Width = ContentWidth, PlaceholderText = "XXXX-XXXX-XXXX-XXXX-XXXX", CharacterCasing = CharacterCasing.Upper, Margin = Padding.Empty };
    private readonly BrandNotice _replaceWarning = new(ContentWidth) { Tone = NoticeTone.Warning, Visible = false };
    private readonly FlowLayoutPanel _busy = new()
    {
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        Margin = new Padding(0, 4, 0, 10),
        Visible = false
    };
    private readonly BrandNotice _result = new(ContentWidth) { Visible = false };
    private readonly Button _setUp = BrandChrome.FooterButton("Set up");
    private readonly Button _cancel = BrandChrome.FooterButton("Cancel", gap: 16);

    public SetupForm(TrayApplication tray) : base("Set up Matmon Agent", "Set up")
    {
        _tray = tray;

        _busy.Controls.Add(new Label
        {
            Text = "Setting up… Windows asks for administrator rights once.",
            AutoSize = true,
            ForeColor = BrandChrome.TextColor,
            Margin = new Padding(0, 0, 0, 6)
        });
        _busy.Controls.Add(new ProgressBar { Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30, Size = new Size(ContentWidth, 18), Margin = Padding.Empty });

        _cancel.Click += (_, _) => Close();
        _setUp.Click += async (_, _) => await SetUpAsync();
        AcceptButton = _setUp;
        CancelButton = _cancel;

        Content.Controls.AddRange(
        [
            BrandChrome.Heading("Connect this machine"),
            BrandChrome.Paragraph(
                "Create an enrolment code under Probes > Agents on your Matmon instance, then enter it here. " +
                "Setting up needs administrator rights once.", width: ContentWidth),
            FieldLabel("Instance URL"), _url,
            Hint("The address you open Matmon at in the browser."),
            FieldLabel("Enrolment code"), _code,
            Hint("A code works once and only for the time chosen when it was created."),
            _replaceWarning,
            _busy,
            _result
        ]);
        Complete(_cancel, _setUp);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        var status = _tray.Status;
        if (string.IsNullOrWhiteSpace(_url.Text) && !string.IsNullOrWhiteSpace(status?.PrimaryUrl))
        {
            _url.Text = status.PrimaryUrl;
        }

        _replaceWarning.Visible = status is not null;
        _replaceWarning.Text = status is null
            ? string.Empty
            : $"This machine is already set up as '{status.ProbeName}'. Setting it up again replaces that identity with a new probe.";
        FitHeight();
        (string.IsNullOrWhiteSpace(_url.Text) ? _url : _code).Focus();
    }

    private async Task SetUpAsync()
    {
        var url = _url.Text.Trim();
        var code = _code.Text.Trim();
        if (url.Length == 0 || code.Length == 0)
        {
            ShowResult(false, "Enter the instance URL and the enrolment code.");
            return;
        }

        var self = Environment.ProcessPath!;
        var resultFile = Path.Combine(Path.GetTempPath(), $"matmon-agent-setup-{Guid.NewGuid():N}.json");
        // --force whenever an identity is already on disk - not only when the service answers: a setup that failed
        // after enrolling (or a service removed without its agent.json) otherwise refused every later attempt with
        // "already enrolled", which nobody can answer from the tray.
        var hasIdentity = _tray.Status is not null || File.Exists(AgentConfigFile.Resolve([]));
        var arguments = $"setup --url \"{url}\" --code \"{code}\" --result \"{resultFile}\"" + (hasIdentity ? " --force" : string.Empty);

        ShowBusy(true);
        try
        {
            using var process = Process.Start(new ProcessStartInfo(self, arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            });
            if (process is null)
            {
                ShowResult(false, "Setup did not start.");
                return;
            }

            await process.WaitForExitAsync();
            var result = File.Exists(resultFile)
                ? JsonSerializer.Deserialize<SetupResult>(File.ReadAllText(resultFile), new JsonSerializerOptions(JsonSerializerDefaults.Web))
                : null;
            if (result is null)
            {
                ShowResult(false, $"Setup ended without a result (exit code {process.ExitCode}).");
                return;
            }

            ShowResult(result.Success, result.Message);
            if (result.Success)
            {
                _code.Clear();
                await _tray.RefreshAsync();
                // Set up from a download: the tray should run from the installed copy (that one starts at sign-in).
                if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(SetupCommand.InstalledPath), StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show(this, result.Message, "Matmon Agent", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    _tray.StartInstalledTrayAndExit();
                }
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            ShowResult(false, "Setup needs administrator rights - the Windows prompt was cancelled.");
        }
        catch (Exception ex)
        {
            ShowResult(false, $"Setup failed: {ex.Message}");
        }
        finally
        {
            AgentUpdateFiles.TryDelete(resultFile);
            ShowBusy(false);
        }
    }

    /// <summary>While the elevated setup runs: a moving bar, and nothing to press twice.</summary>
    private void ShowBusy(bool busy)
    {
        if (IsDisposed)
        {
            return;
        }

        _busy.Visible = busy;
        if (busy)
        {
            _result.Visible = false;
        }

        _setUp.Enabled = !busy;
        _cancel.Enabled = !busy;
        _url.ReadOnly = busy;
        _code.ReadOnly = busy;
        FitHeight();
    }

    private void ShowResult(bool? success, string message)
    {
        if (IsDisposed)
        {
            return;
        }

        _busy.Visible = false;
        _result.Tone = success switch
        {
            true => NoticeTone.Success,
            false => NoticeTone.Error,
            null => NoticeTone.Info
        };
        _result.Text = message;
        _result.Visible = true;
        FitHeight();
    }
}
#endif
