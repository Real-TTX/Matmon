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
    // The brand gradient (the tray icon's tile uses the same two colours).
    private static readonly Color BrandFrom = Color.FromArgb(0x2B, 0x7B, 0xD6);
    private static readonly Color BrandTo = Color.FromArgb(0x1F, 0xB5, 0xB0);

    private readonly SetupArguments _arguments;
    private readonly WindowsInstallationInfo? _installed;
    private readonly Panel _body = new() { Dock = DockStyle.Fill, Padding = new Padding(28, 22, 28, 12) };
    private readonly Button _back = new() { Text = "< Back", AutoSize = true, MinimumSize = new Size(96, 30) };
    private readonly Button _next = new() { Text = "Next >", AutoSize = true, MinimumSize = new Size(96, 30) };
    private readonly Button _cancel = new() { Text = "Cancel", AutoSize = true, MinimumSize = new Size(96, 30) };

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

        Text = arguments.Uninstall ? "Uninstall Matmon" : $"Matmon {SetupRunner.Version} Setup";
        Icon = Icon.ExtractAssociatedIcon(SetupRunner.SelfPath);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(640, 470);
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Color.White;

        _programFolder.Text = arguments.ProgramFolder ?? _installed?.ProgramFolder ?? WindowsInstallation.DefaultProgramFolder;
        _dataFolder.Text = arguments.DataFolder ?? _installed?.DataFolder ?? WindowsInstallation.DefaultDataFolder;
        _port.Value = arguments.Port ?? _installed?.Port ?? 8099;
        _firewall.Checked = arguments.OpenFirewall;
        _tray.Checked = !arguments.NoTray;
        _removeData.Text = $"Also delete the data - configuration, history and backups in {_installed?.DataFolder ?? WindowsInstallation.DefaultDataFolder}";

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(16, 12, 16, 12),
            Height = 58,
            BackColor = Color.FromArgb(0xF4, 0xF6, 0xF9)
        };
        footer.Controls.AddRange([_cancel, _next, _back]);
        _cancel.Margin = new Padding(12, 0, 0, 0);
        _back.Click += (_, _) => Show(Page.Welcome);
        _next.Click += (_, _) => Advance();
        _cancel.Click += (_, _) => Close();
        AcceptButton = _next;

        var header = new BrandHeader(arguments.Uninstall ? "Uninstall" : _installed is null ? "Setup" : "Update")
        {
            Dock = DockStyle.Top,
            Height = 96
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
                    SetupRunner.Install(_arguments, options, Report);
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
        _body.Controls.Add(content);

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
                content.Controls.Add(Paragraph(_installed is null
                    ? $"This installs Matmon {SetupRunner.Version} on this computer. Matmon runs as a Windows service in the background, starts with Windows and is used in the browser."
                    : $"Matmon {_installed.Version ?? "(installed)"} is installed. Setup updates it to {SetupRunner.Version} - the configuration, the history and the sensors are kept."));
                content.Controls.Add(Paragraph("Once installed, Matmon keeps itself up to date if you want it to (System > Updates)."));
                if (!SetupRunner.HasPackage(_arguments))
                {
                    content.Controls.Add(Paragraph("This setup carries no Matmon package - it can only uninstall. Download the full setup from the Matmon releases.", Color.Firebrick));
                    _next.Enabled = false;
                }

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

        _body.ResumeLayout();

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
        MaximumSize = new Size(570, 0),
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
        panel.Controls.Add(new Label { Text = hint, AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(570, 0) });
        return panel;
    }

    /// <summary>The top band: the brand gradient, the app icon and the product name.</summary>
    private sealed class BrandHeader : Control
    {
        private readonly string _subtitle;
        private readonly Image? _logo;

        public BrandHeader(string subtitle)
        {
            _subtitle = subtitle;
            DoubleBuffered = true;
            using var stream = typeof(SetupForm).Assembly.GetManifestResourceStream("Matmon.Setup.app-icon.png");
            _logo = stream is null ? null : Image.FromStream(stream);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var graphics = e.Graphics;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (var gradient = new LinearGradientBrush(ClientRectangle, BrandFrom, BrandTo, LinearGradientMode.ForwardDiagonal))
            {
                graphics.FillRectangle(gradient, ClientRectangle);
            }

            var size = Height - 32;
            if (_logo is not null)
            {
                // The icon is a coloured tile; a white plate keeps it from melting into the band.
                var plate = new Rectangle(24, 16, size, size);
                using var path = RoundedRectangle(plate, 12);
                using var white = new SolidBrush(Color.White);
                graphics.FillPath(white, path);
                graphics.DrawImage(_logo, Rectangle.Inflate(plate, -4, -4));
            }

            using var titleFont = new Font("Segoe UI Semibold", 20f);
            using var subtitleFont = new Font("Segoe UI", 10.5f);
            using var text = new SolidBrush(Color.White);
            using var soft = new SolidBrush(Color.FromArgb(225, Color.White));
            var left = 24 + size + 18;
            graphics.DrawString("Matmon", titleFont, text, left, 14);
            graphics.DrawString($"{_subtitle} · {SetupRunner.Version}", subtitleFont, soft, left + 2, 54);
        }

        private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            var diameter = radius * 2;
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _logo?.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
