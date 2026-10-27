using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Net;
using System.Net.Sockets;
using Matmon.Host.Services.SelfUpdate;

namespace Matmon.Setup;

/// <summary>
/// The setup wizard. Install: Welcome -> Options -> Progress -> Finish. Uninstall: Confirm -> Progress -> Finish. The work
/// runs on a background task (<see cref="SetupRunner"/>) and reports into the progress page; the window only steers.
/// </summary>
internal sealed class SetupForm : Form
{

    private readonly SetupArguments _arguments;
    private readonly WindowsInstallationInfo? _installed;
    private readonly PackageSource _source;
    private readonly Panel _body = new() { Dock = DockStyle.Fill, Padding = new Padding(28, 22, 28, 12) };
    // Fixed size and the same margin for all three: AutoSize plus a margin of its own put Cancel a few pixels higher than the others.
    private readonly Button _back = new() { Text = "< Back", Size = new Size(104, 32), Margin = new Padding(8, 0, 0, 0) };
    private readonly Button _next = new() { Text = "Next >", Size = new Size(104, 32), Margin = new Padding(8, 0, 0, 0) };
    private readonly Button _cancel = new() { Text = "Cancel", Size = new Size(104, 32), Margin = new Padding(16, 0, 0, 0) };

    private readonly TextBox _programFolder = new() { Width = 360 };
    private readonly TextBox _dataFolder = new() { Width = 360 };
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Width = 100 };
    private readonly CheckBox _firewall = new() { Text = "Allow access from other computers (Windows firewall)", AutoSize = true };
    private readonly CheckBox _tray = new() { Text = "Show Matmon in the notification area (open it, start and stop the service)", AutoSize = true };
    private readonly CheckBox _removeData = new() { AutoSize = true };
    private readonly CheckBox _openNow = new() { Text = "Open Matmon in the browser", AutoSize = true, Checked = true };
    private readonly Label _step = new() { AutoSize = true };
    private readonly ProgressBar _progress = new() { Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30, Height = 18 };
    private readonly ListBox _log = new() { IntegralHeight = false, BorderStyle = BorderStyle.FixedSingle };

    private Page _page;
    private bool _working;
    private bool _succeeded;
    private string? _failure;

    public int ExitCode { get; private set; } = 1;

    private enum Page
    {
        Welcome,
        Options,
        Confirm,
        Progress,
        Finish
    }

    public SetupForm(SetupArguments arguments)
    {
        _arguments = arguments;
        _installed = WindowsInstallation.Find();
        _source = arguments.Uninstall ? PackageSource.Embedded : SetupRunner.SourceOf(arguments);

        Text = arguments.Uninstall ? "Uninstall Matmon" : _source == PackageSource.Online ? "Matmon Setup" : $"Matmon {SetupRunner.Version} Setup";
        Icon = Icon.ExtractAssociatedIcon(SetupRunner.SelfPath);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(640, 540);
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Color.White;

        _programFolder.Text = arguments.ProgramFolder ?? _installed?.ProgramFolder ?? WindowsInstallation.DefaultProgramFolder;
        _dataFolder.Text = arguments.DataFolder ?? _installed?.DataFolder ?? WindowsInstallation.DefaultDataFolder;
        _port.Value = arguments.Port ?? _installed?.Port ?? 8099;
        _firewall.Checked = arguments.OpenFirewall;
        _tray.Checked = !arguments.NoTray;
        _removeData.Text = $"Also delete the data - configuration, history and backups in {_installed?.DataFolder ?? WindowsInstallation.DefaultDataFolder}";

        // The bar: version and channel on the left, the buttons on the right, both centred on the same line.
        // The online setup does not know the version it installs until it has asked the feed - so it names the channel instead.
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = Padding.Empty,
            Margin = Padding.Empty
        };
        buttons.Controls.AddRange([_cancel, _next, _back]);
        var version = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(0x5B, 0x67, 0x78),
            Text = arguments.Uninstall
                ? $"Version {_installed?.Version ?? SetupRunner.Version}"
                : _source == PackageSource.Online
                    ? $"Online setup · {SetupRunner.ChannelName} channel"
                    : $"Version {SetupRunner.Version} · {SetupRunner.ChannelName} channel"
        };
        var footer = new Panel
        {
            Dock = DockStyle.Bottom,
            Padding = new Padding(20, 13, 16, 13),
            Height = 58,
            BackColor = Color.FromArgb(0xF4, 0xF6, 0xF9)
        };
        footer.Controls.Add(version);
        footer.Controls.Add(buttons);
        _back.Click += (_, _) => Show(Page.Welcome);
        _next.Click += (_, _) => Advance();
        _cancel.Click += (_, _) => Close();
        AcceptButton = _next;

        var header = new BrandHeader(arguments.Uninstall ? "Uninstall" : _installed is null ? "Setup" : "Update")
        {
            Dock = DockStyle.Top,
            Height = 84
        };

        Controls.Add(_body);
        Controls.Add(footer);
        Controls.Add(header);

        Show(arguments.Uninstall ? Page.Confirm : Page.Welcome);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_working)
        {
            e.Cancel = true; // half an installation is worse than a slow one
            return;
        }

        if (_page == Page.Finish && _succeeded && !_arguments.Uninstall && _openNow.Checked)
        {
            Process.Start(new ProcessStartInfo($"http://localhost:{(int)_port.Value}") { UseShellExecute = true });
        }

        base.OnFormClosing(e);
    }

    private void Advance()
    {
        switch (_page)
        {
            case Page.Welcome:
                Show(Page.Options);
                break;
            case Page.Options:
                if (Validate(out var problem))
                {
                    Run();
                }
                else
                {
                    MessageBox.Show(this, problem, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }

                break;
            case Page.Confirm:
                Run();
                break;
            case Page.Finish:
                Close();
                break;
        }
    }

    private bool Validate(out string problem)
    {
        problem = string.Empty;
        if (!Path.IsPathFullyQualified(_programFolder.Text) || !Path.IsPathFullyQualified(_dataFolder.Text))
        {
            problem = "Please choose full paths for the program and the data folder.";
            return false;
        }

        // The port must be free - unless it is the one this installation already uses (it is stopped during the update).
        var port = (int)_port.Value;
        if (port != _installed?.Port && !IsPortFree(port))
        {
            problem = $"Port {port} is already in use on this computer. Please choose another one.";
            return false;
        }

        return true;
    }

    private static bool IsPortFree(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private void Run()
    {
        Show(Page.Progress);
        _working = true;
        var options = new WindowsInstallOptions(_programFolder.Text.Trim(), _dataFolder.Text.Trim(), (int)_port.Value, _firewall.Checked, _tray.Checked);
        var removeData = _removeData.Checked;
        var uninstall = _arguments.Uninstall;
        Task.Run(() =>
        {
            void Report(string line)
            {
                SetupRunner.Log(line);
                BeginInvoke(() =>
                {
                    _step.Text = line;
                    _log.Items.Add(line);
                    _log.TopIndex = _log.Items.Count - 1;
                });
            }

            try
            {
                if (uninstall)
                {
                    SetupRunner.Uninstall(removeData, Report);
                }
                else
                {
                    SetupRunner.Install(_arguments, options, Report, share => BeginInvoke(() =>
                    {
                        // While the online setup downloads, the bar shows how far it is; afterwards it goes back to "busy".
                        _progress.Style = share >= 1 ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
                        _progress.Value = (int)(share * 100);
                        _step.Text = share >= 1 ? _step.Text : $"Downloading Matmon ... {share * 100:0} %";
                    }));
                }

                return (string?)null;
            }
            catch (Exception ex)
            {
                SetupRunner.Log($"FAILED: {ex}");
                return ex.Message;
            }
        }).ContinueWith(task => BeginInvoke(() =>
        {
            _working = false;
            _failure = task.Result;
            _succeeded = _failure is null;
            ExitCode = _succeeded ? 0 : 1;
            Show(Page.Finish);
        }));
    }

    private void Show(Page page)
    {
        _page = page;
        _body.SuspendLayout();
        _body.Controls.Clear();
        var content = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };

        _back.Visible = page == Page.Options;
        _cancel.Visible = page is not (Page.Progress or Page.Finish);
        _next.Enabled = page != Page.Progress;
        _next.Text = page switch
        {
            Page.Options => _installed is null ? "Install" : "Update",
            Page.Confirm => "Uninstall",
            Page.Finish => "Finish",
            _ => "Next >"
        };

        switch (page)
        {
            case Page.Welcome:
                content.Controls.Add(Heading(_installed is null ? "Welcome to Matmon" : "Update Matmon"));
                var target = _source == PackageSource.Online ? $"the newest Matmon ({SetupRunner.ChannelName} channel)" : $"Matmon {SetupRunner.Version}";
                content.Controls.Add(Paragraph(_installed is null
                    ? $"This installs {target} on this computer. Matmon runs as a Windows service in the background, starts with Windows and is used in the browser."
                    : $"Matmon {_installed.Version ?? "(installed)"} is installed. Setup updates it to {target} - the configuration, the history and the sensors are kept."));
                if (_source == PackageSource.Online)
                {
                    content.Controls.Add(Paragraph("Matmon is downloaded during the installation (about 190 MB) and checked before anything is changed: its checksum and its signature must match the release."));
                }
                else if (_source == PackageSource.File && !File.Exists(_arguments.PackagePath))
                {
                    content.Controls.Add(Paragraph($"The package {_arguments.PackagePath} does not exist.", Color.Firebrick));
                    _next.Enabled = false;
                }

                content.Controls.Add(Paragraph("Once installed, Matmon keeps itself up to date if you want it to (System > Updates)."));

                break;

            case Page.Options:
                content.Controls.Add(Heading("Where and how"));
                content.Controls.Add(FolderRow("Program folder", _programFolder, "Replaced as a whole by every update."));
                content.Controls.Add(FolderRow("Data folder", _dataFolder, "Configuration, history and backups. Kept by updates; readable only by administrators."));
                var portRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 6, 0, 6) };
                portRow.Controls.Add(new Label { Text = "Port", AutoSize = true, Margin = new Padding(0, 6, 12, 0) });
                portRow.Controls.Add(_port);
                portRow.Controls.Add(new Label { Text = "Matmon answers on http://<this computer>:<port>", AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(12, 6, 0, 0) });
                content.Controls.Add(portRow);
                content.Controls.Add(_firewall);
                content.Controls.Add(_tray);
                break;

            case Page.Confirm:
                content.Controls.Add(Heading("Uninstall Matmon"));
                content.Controls.Add(Paragraph(_installed is null
                    ? "No Matmon installation was found on this computer. Uninstall still removes whatever is left (service, firewall rule, Installed apps entry)."
                    : $"This removes the Matmon service and the program from {_installed.ProgramFolder}. Without the box below, the data stays where it is and a new installation picks it up again."));
                content.Controls.Add(_removeData);
                break;

            case Page.Progress:
                content.Controls.Add(Heading(_arguments.Uninstall ? "Removing Matmon" : "Installing Matmon"));
                content.Controls.Add(_step);
                _progress.Width = 570;
                content.Controls.Add(_progress);
                _log.Size = new Size(570, 190);
                _log.Margin = new Padding(0, 12, 0, 0);
                content.Controls.Add(_log);
                break;

            case Page.Finish:
                if (_succeeded)
                {
                    content.Controls.Add(Heading(_arguments.Uninstall ? "Matmon has been removed" : "Matmon is ready"));
                    content.Controls.Add(Paragraph(_log.Items.Count > 0 ? (string)_log.Items[^1]! : string.Empty));
                    if (!_arguments.Uninstall)
                    {
                        var address = $"http://localhost:{(int)_port.Value}";
                        var link = new LinkLabel { Text = address, AutoSize = true, Margin = new Padding(0, 4, 0, 12) };
                        link.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo(address) { UseShellExecute = true });
                        content.Controls.Add(link);
                        content.Controls.Add(Paragraph("On the first visit Matmon asks for the e-mail address and password of its administrator."));
                        content.Controls.Add(_openNow);
                    }
                }
                else
                {
                    content.Controls.Add(Heading(_arguments.Uninstall ? "Uninstall did not finish" : "Setup did not finish"));
                    content.Controls.Add(Paragraph(_failure ?? "Unknown error.", Color.Firebrick));
                    var log = new LinkLabel { Text = "Open the setup log", AutoSize = true };
                    log.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo("notepad.exe", SetupRunner.LogPath) { UseShellExecute = true });
                    content.Controls.Add(log);
                }

                break;
        }

        // Added once it holds its controls and laid out at its final size: added empty first, it measured them at its default
        // 200 px width and kept a scroll range for a page that fits (a scrollbar on the options page).
        content.Size = _body.DisplayRectangle.Size;
        _body.Controls.Add(content);
        _body.ResumeLayout();
        content.PerformLayout();

        // Enter presses the focused button: left on Cancel it closed the setup.
        ActiveControl = _next.Enabled && _next.Visible ? _next : _cancel.Visible ? _cancel : null;
    }

    private static Label Heading(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font("Segoe UI Semibold", 14f),
        ForeColor = Color.FromArgb(0x1B, 0x26, 0x38),
        Margin = new Padding(0, 0, 0, 12)
    };

    private static Label Paragraph(string text, Color? color = null) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(560, 0),
        ForeColor = color ?? Color.FromArgb(0x3A, 0x45, 0x55),
        Margin = new Padding(0, 0, 0, 10)
    };

    private Control FolderRow(string label, TextBox box, string hint)
    {
        var panel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Margin = new Padding(0, 0, 0, 8) };
        panel.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 0, 0, 2) });
        var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = Padding.Empty };
        var browse = new Button { Text = "Browse ...", AutoSize = true };
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { SelectedPath = box.Text, UseDescriptionForTitle = true, Description = label };
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                box.Text = dialog.SelectedPath;
            }
        };
        row.Controls.Add(box);
        row.Controls.Add(browse);
        panel.Controls.Add(row);
        panel.Controls.Add(new Label { Text = hint, AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(560, 0) });
        return panel;
    }

    /// <summary>The top band, like the header of matmon.eu: the flat M mark beside the name on a light ground.</summary>
    private sealed class BrandHeader : Control
    {
        private readonly string _subtitle;

        public BrandHeader(string subtitle)
        {
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

            var markHeight = 40f;
            var markWidth = markHeight * BrandMark.Width / BrandMark.Height;
            var top = (Height - markHeight) / 2f;
            BrandMark.Draw(graphics, new RectangleF(24, top, markWidth, markHeight));

            using var titleFont = new Font("Segoe UI Semibold", 20f);
            using var subtitleFont = new Font("Segoe UI", 12f);
            using var title = new SolidBrush(Color.FromArgb(0x0F, 0x1B, 0x2D));
            using var soft = new SolidBrush(Color.FromArgb(0x5B, 0x67, 0x78));
            var left = 24 + markWidth + 14;
            var titleSize = graphics.MeasureString("Matmon", titleFont);
            var subtitleSize = graphics.MeasureString(_subtitle, subtitleFont);
            graphics.DrawString("Matmon", titleFont, title, left, (Height - titleSize.Height) / 2f);
            graphics.DrawString(_subtitle, subtitleFont, soft, left + titleSize.Width + 6, (Height - subtitleSize.Height) / 2f + 2);

            using var line = new Pen(Color.FromArgb(0xE3, 0xE8, 0xEF));
            graphics.DrawLine(line, 0, Height - 1, Width, Height - 1);
        }
    }

    /// <summary>The flat Matmon mark (brand/matmon-mark-flat.svg), drawn as a vector so it stays sharp at any DPI.</summary>
    private static class BrandMark
    {
        public const float Width = 444;
        public const float Height = 352;

        // The SVG's path, unchanged - M, L, Q and Z only.
        private const string PathData = "M74.6 2Q78 0 82 0L90 0Q93.7 0 96.8 2.5L224.6 107.3Q228.5 110.5 232.4 107.4L366.3 2.5Q369.4 0 372.8 2.1L440.6 44.4Q444 46.5 444 51.5L444 290.7Q444 295.7 440.5 298.2L350 352Q328 352 328 322L328 160L229.4 247.3Q226.4 250 222.4 250L215.1 250Q211.1 250 208.2 247.2L116 158.3L116 322Q116 352 94 352L4.3 294.9Q0 292.2 0 287.2L0 51.4Q0 46.4 4 43.9Z";

        public static void Draw(Graphics graphics, RectangleF bounds)
        {
            using var path = BuildPath();
            using var transform = new Matrix();
            transform.Translate(bounds.X, bounds.Y);
            transform.Scale(bounds.Width / Width, bounds.Height / Height);
            path.Transform(transform);

            // The SVG's gradient: user space (0,0) -> (444,200), three stops.
            var from = new PointF(bounds.X, bounds.Y);
            var to = new PointF(bounds.X + bounds.Width, bounds.Y + 200 * bounds.Height / Height);
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

        private static GraphicsPath BuildPath()
        {
            var path = new GraphicsPath();
            var tokens = System.Text.RegularExpressions.Regex.Matches(PathData, @"[MLQZ]|-?\d*\.?\d+")
                .Select(match => match.Value).ToArray();
            var current = PointF.Empty;
            var index = 0;
            float Next() => float.Parse(tokens[index++], System.Globalization.CultureInfo.InvariantCulture);
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
    }
}
