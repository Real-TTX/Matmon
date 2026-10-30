using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Matmon.Agent.Tray;
using Matmon.Host.Services.SelfUpdate;

namespace Matmon.Setup;

/// <summary>
/// The setup wizard. Install: Welcome -> [Role] -> [Probe] -> Options -> Progress -> Finish (Role only on a fresh install, Probe
/// only for a remote probe). Uninstall: Confirm -> Progress -> Finish. The work runs on a background task
/// (<see cref="SetupRunner"/>) and reports into the progress page; the window only steers.
/// </summary>
internal sealed class SetupForm : Form
{
    private readonly SetupArguments _arguments;
    private readonly WindowsInstallationInfo? _installed;
    private readonly WindowsProbeConnection? _storedProbe;
    private readonly PackageSource _source;
    private readonly BrandHeader _header;
    private readonly Panel _body = new() { Dock = DockStyle.Fill, Padding = new Padding(28, 22, 28, 12) };
    private readonly Button _back = BrandChrome.FooterButton("< Back");
    private readonly Button _next = BrandChrome.FooterButton("Next >");
    private readonly Button _cancel = BrandChrome.FooterButton("Cancel", gap: 16);

    private readonly TextBox _programFolder = new() { Width = 360 };
    private readonly TextBox _dataFolder = new() { Width = 360 };
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Width = 100 };
    private readonly CheckBox _firewall = new() { Text = "Allow access from other computers (Windows firewall)", AutoSize = true };
    private readonly CheckBox _tray = new() { Text = "Show Matmon in the notification area (open it, start and stop the service)", AutoSize = true };
    private readonly CheckBox _removeData = new() { AutoSize = true };
    private readonly CheckBox _openNow = new() { Text = "Open Matmon in the browser", AutoSize = true, Checked = true };
    private readonly RadioButton _roleInstance = new() { Text = "Matmon - the instance with its web interface, sensors and alerts", AutoSize = true, Checked = true };
    private readonly RadioButton _roleProbe = new() { Text = "Remote probe - runs sensors in this network for a Matmon primary", AutoSize = true };
    private readonly TextBox _primaryUrl = new() { Width = 520, PlaceholderText = "https://matmon.example" };
    private readonly TextBox _probeId = new() { Width = 520 };
    private readonly TextBox _probeToken = new() { Width = 520, UseSystemPasswordChar = true };
    private readonly TextBox _probeName = new() { Width = 520 };
    private readonly Label _step = new() { AutoSize = true };
    private readonly ProgressBar _progress = new() { Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30, Height = 18 };
    private readonly ListBox _log = new() { IntegralHeight = false, BorderStyle = BorderStyle.FixedSingle };
    private readonly Stack<Page> _history = new();

    private Page _page;
    private bool _working;
    private bool _succeeded;
    private string? _failure;
    private WindowsProbeConnection? _probe;

    public int ExitCode { get; private set; } = 1;

    private enum Page
    {
        Welcome,
        Role,
        Probe,
        Options,
        Confirm,
        Progress,
        Finish
    }

    /// <summary>A probe is being installed (chosen, asked for on the command line, or the installed one being updated).</summary>
    private bool IsProbe => _installed is null ? _roleProbe.Checked : _installed.Role == WindowsInstallationRole.Probe;

    private string Product => IsProbe ? "Matmon probe" : "Matmon";

    public SetupForm(SetupArguments arguments)
    {
        _arguments = arguments;
        _installed = WindowsInstallation.Find();
        _storedProbe = _installed?.Role == WindowsInstallationRole.Probe ? WindowsProbeSettings.ReadProbeSettings(_installed.DataFolder) : null;
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
        _roleProbe.Checked = arguments.WantsProbe;
        _roleInstance.Checked = !arguments.WantsProbe;
        _primaryUrl.Text = arguments.PrimaryUrl ?? _storedProbe?.PrimaryUrl ?? string.Empty;
        _probeId.Text = arguments.ProbeId ?? _storedProbe?.ProbeId ?? string.Empty;
        _probeToken.Text = arguments.ProbeToken ?? string.Empty;
        // An update keeps the stored token unless a new one is typed - it is never shown again.
        _probeToken.PlaceholderText = _storedProbe is null ? string.Empty : "(unchanged - leave empty to keep it)";
        _probeName.Text = arguments.ProbeName ?? _storedProbe?.ProbeName ?? string.Empty;
        _probeName.PlaceholderText = Environment.MachineName;
        _removeData.Text = $"Also delete the data - configuration, history and backups in {_installed?.DataFolder ?? WindowsInstallation.DefaultDataFolder}";

        // The bar: version and channel on the left, the buttons on the right, both centred on the same line.
        // The online setup does not know the version it installs until it has asked the feed - so it names the channel instead.
        var footer = BrandChrome.Footer(
            arguments.Uninstall
                ? $"Version {_installed?.Version ?? SetupRunner.Version}"
                : _source == PackageSource.Online
                    ? $"Online setup · {SetupRunner.ChannelName} channel"
                    : $"Version {SetupRunner.Version} · {SetupRunner.ChannelName} channel",
            _cancel, _next, _back);
        _back.Click += (_, _) => GoBack();
        _next.Click += (_, _) => Advance();
        _cancel.Click += (_, _) => Close();
        AcceptButton = _next;

        _header = new BrandHeader("Matmon", HeaderWord()) { Dock = DockStyle.Top, Height = 84 };
        _roleProbe.CheckedChanged += (_, _) => _header.Subtitle = HeaderWord();

        Controls.Add(_body);
        Controls.Add(footer);
        Controls.Add(_header);

        Show(arguments.Uninstall ? Page.Confirm : Page.Welcome);
    }

    private string HeaderWord() =>
        _arguments.Uninstall ? "Uninstall"
        : IsProbe ? _installed is null ? "Probe setup" : "Probe update"
        : _installed is null ? "Setup" : "Update";

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_working)
        {
            e.Cancel = true; // half an installation is worse than a slow one
            return;
        }

        if (_page == Page.Finish && _succeeded && !_arguments.Uninstall && _openNow.Checked)
        {
            Process.Start(new ProcessStartInfo(LocalAddress) { UseShellExecute = true });
        }

        base.OnFormClosing(e);
    }

    /// <summary>The instance's web interface, or a probe's status page.</summary>
    private string LocalAddress => IsProbe ? $"http://localhost:{(int)_port.Value}/probe" : $"http://localhost:{(int)_port.Value}";

    private void Advance()
    {
        switch (_page)
        {
            case Page.Welcome:
                Forward(_installed is null ? Page.Role : IsProbe ? Page.Probe : Page.Options);
                break;
            case Page.Role:
                Forward(IsProbe ? Page.Probe : Page.Options);
                break;
            case Page.Probe:
                if (ValidateProbe(out var probeProblem))
                {
                    Forward(Page.Options);
                }
                else
                {
                    MessageBox.Show(this, probeProblem, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }

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

    private void Forward(Page page)
    {
        _history.Push(_page);
        Show(page);
    }

    private void GoBack()
    {
        if (_history.Count > 0)
        {
            Show(_history.Pop());
        }
    }

    /// <summary>The probe page through the same rules as the silent setup (<see cref="SetupArguments.ResolveProbe"/>).</summary>
    private bool ValidateProbe(out string problem)
    {
        var typed = _arguments with
        {
            Probe = true,
            PrimaryUrl = Blank(_primaryUrl.Text),
            ProbeId = Blank(_probeId.Text),
            ProbeToken = Blank(_probeToken.Text),
            ProbeName = Blank(_probeName.Text)
        };
        var (probe, why) = typed.ResolveProbe(_installed, _storedProbe);
        _probe = probe;
        problem = why ?? string.Empty;
        return why is null;
    }

    private static string? Blank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

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

        if (!IsProbe)
        {
            _probe = null;
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
        var options = new WindowsInstallOptions(_programFolder.Text.Trim(), _dataFolder.Text.Trim(), (int)_port.Value, _firewall.Checked, _tray.Checked,
            IsProbe ? _probe : null);
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
                    SetupRunner.Install(_arguments, options, Report, state => BeginInvoke(() =>
                    {
                        // While the online setup downloads, the bar shows how far it is; afterwards it goes back to "busy".
                        var done = state.Complete || state.Share >= 1;
                        _progress.Style = done ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
                        _progress.Value = (int)(state.Share * 100);
                        var speed = state.BytesPerSecond > 0 ? $", {state.BytesPerSecond / 1048576:0.0} MB/s" : "";
                        _step.Text = done
                            ? _step.Text
                            : $"Downloading Matmon ... {state.Share * 100:0} % ({WindowsUpdateFeed.Megabytes(state.Done)} of {WindowsUpdateFeed.Megabytes(state.Total)} MB{speed})";
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

        _back.Visible = page is Page.Role or Page.Probe or Page.Options;
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
                content.Controls.Add(BrandChrome.Heading(_installed is null ? "Welcome to Matmon" : $"Update {Product}"));
                var target = _source == PackageSource.Online ? $"the newest Matmon ({SetupRunner.ChannelName} channel)" : $"Matmon {SetupRunner.Version}";
                content.Controls.Add(BrandChrome.Paragraph(_installed is null
                    ? $"This installs {target} on this computer - the instance, or a remote probe for a Matmon you already run. It runs as a Windows service in the background and starts with Windows."
                    : IsProbe
                        ? $"The Matmon probe {_installed.Version ?? "(installed)"} reporting to {_installed.PrimaryUrl ?? "its primary"} is installed. Setup updates it to {target} - its connection is kept."
                        : $"Matmon {_installed.Version ?? "(installed)"} is installed. Setup updates it to {target} - the configuration, the history and the sensors are kept."));
                if (_source == PackageSource.Online)
                {
                    content.Controls.Add(BrandChrome.Paragraph("Matmon is downloaded during the installation (about 190 MB) and checked before anything is changed: its checksum and its signature must match the release."));
                }
                else if (_source == PackageSource.File && !File.Exists(_arguments.PackagePath))
                {
                    content.Controls.Add(BrandChrome.Paragraph($"The package {_arguments.PackagePath} does not exist.", Color.Firebrick));
                    _next.Enabled = false;
                }

                if (_installed is not null && _arguments.WantsProbe != IsProbe && WindowsProbeSettings.RoleChangeProblem(_installed, _arguments.WantsProbe) is { } change)
                {
                    content.Controls.Add(Notice(change, NoticeTone.Warning));
                    _next.Enabled = false;
                }

                content.Controls.Add(BrandChrome.Paragraph(IsProbe && _installed is not null
                    ? "A probe is updated by running the newest setup on its machine."
                    : "Once installed, Matmon keeps itself up to date if you want it to (System > Updates)."));
                break;

            case Page.Role:
                content.Controls.Add(BrandChrome.Heading("What should this computer run?"));
                _roleInstance.Margin = new Padding(0, 0, 0, 2);
                content.Controls.Add(_roleInstance);
                content.Controls.Add(Hint("For a network of its own: you sign in to it in the browser."));
                _roleProbe.Margin = new Padding(0, 8, 0, 2);
                content.Controls.Add(_roleProbe);
                content.Controls.Add(Hint("Reaches the devices of this network for a Matmon that runs elsewhere and connects out to it - no port to open. Create the probe on the primary first (Probes > New probe > Install)."));
                content.Controls.Add(BrandChrome.Paragraph("A computer runs one Matmon. To monitor this computer itself for a Matmon elsewhere, the Matmon Agent is the lighter choice (Probes > Agents)."));
                break;

            case Page.Probe:
                content.Controls.Add(BrandChrome.Heading("Connect the probe"));
                content.Controls.Add(BrandChrome.Paragraph("Copy the values from the probe's Install page on the primary (Probes > the probe > Install > Windows)."));
                content.Controls.Add(Field("Primary address", _primaryUrl, "The address the primary is opened at in the browser."));
                content.Controls.Add(Field("Probe ID", _probeId, null));
                content.Controls.Add(Field("Probe token", _probeToken, "Kept in the data folder, readable by administrators only."));
                content.Controls.Add(Field("Name (optional)", _probeName, null));
                break;

            case Page.Options:
                content.Controls.Add(BrandChrome.Heading("Where and how"));
                content.Controls.Add(FolderRow("Program folder", _programFolder, "Replaced as a whole by every update."));
                content.Controls.Add(FolderRow("Data folder", _dataFolder, IsProbe
                    ? "The probe's connection, its results waiting to be sent and its keys. Kept by updates; readable only by administrators."
                    : "Configuration, history and backups. Kept by updates; readable only by administrators."));
                var portRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 6, 0, 6) };
                portRow.Controls.Add(new Label { Text = "Port", AutoSize = true, Margin = new Padding(0, 6, 12, 0) });
                portRow.Controls.Add(_port);
                portRow.Controls.Add(new Label
                {
                    Text = IsProbe ? "The probe's status page: http://<this computer>:<port>/probe" : "Matmon answers on http://<this computer>:<port>",
                    AutoSize = true,
                    ForeColor = Color.DimGray,
                    Margin = new Padding(12, 6, 0, 0)
                });
                content.Controls.Add(portRow);
                content.Controls.Add(_firewall);
                _tray.Text = IsProbe
                    ? "Show the probe in the notification area (its connection, start and stop the service)"
                    : "Show Matmon in the notification area (open it, start and stop the service)";
                content.Controls.Add(_tray);
                break;

            case Page.Confirm:
                content.Controls.Add(BrandChrome.Heading(_installed?.Role == WindowsInstallationRole.Probe ? "Uninstall the Matmon probe" : "Uninstall Matmon"));
                content.Controls.Add(BrandChrome.Paragraph(_installed is null
                    ? "No Matmon installation was found on this computer. Uninstall still removes whatever is left (service, firewall rule, Installed apps entry)."
                    : $"This removes the Matmon service and the program from {_installed.ProgramFolder}. Without the box below, the data stays where it is and a new installation picks it up again."));
                content.Controls.Add(_removeData);
                break;

            case Page.Progress:
                content.Controls.Add(BrandChrome.Heading(_arguments.Uninstall ? "Removing Matmon" : $"Installing {Product}"));
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
                    content.Controls.Add(BrandChrome.Heading(_arguments.Uninstall ? "Matmon has been removed" : $"{Product} is ready"));
                    content.Controls.Add(BrandChrome.Paragraph(_log.Items.Count > 0 ? (string)_log.Items[^1]! : string.Empty));
                    if (!_arguments.Uninstall)
                    {
                        var link = new LinkLabel { Text = LocalAddress, AutoSize = true, Margin = new Padding(0, 4, 0, 12) };
                        link.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo(LocalAddress) { UseShellExecute = true });
                        content.Controls.Add(link);
                        content.Controls.Add(BrandChrome.Paragraph(IsProbe
                            ? "The probe is operated on its primary: its sensors are added there, under the probe. The tray icon shows whether it is connected."
                            : "On the first visit Matmon asks for the e-mail address and password of its administrator."));
                        _openNow.Text = IsProbe ? "Open the probe's status page" : "Open Matmon in the browser";
                        content.Controls.Add(_openNow);
                    }
                }
                else
                {
                    content.Controls.Add(BrandChrome.Heading(_arguments.Uninstall ? "Uninstall did not finish" : "Setup did not finish"));
                    content.Controls.Add(BrandChrome.Paragraph(_failure ?? "Unknown error.", Color.Firebrick));
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
        ActiveControl = page == Page.Probe ? FirstEmpty() : _next.Enabled && _next.Visible ? _next : _cancel.Visible ? _cancel : null;
    }

    private Control FirstEmpty() =>
        new Control[] { _primaryUrl, _probeId, _probeToken }.FirstOrDefault(box => box.Text.Length == 0 && !(box == _probeToken && _storedProbe is not null)) ?? _next;

    private static Label Hint(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = Color.DimGray,
        MaximumSize = new Size(540, 0),
        Margin = new Padding(20, 0, 0, 6)
    };

    private static BrandNotice Notice(string text, NoticeTone tone) => new(560) { Text = text, Tone = tone };

    private static Control Field(string label, TextBox box, string? hint)
    {
        var panel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Margin = new Padding(0, 0, 0, 8), WrapContents = false };
        panel.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 0, 0, 2) });
        box.Margin = Padding.Empty;
        panel.Controls.Add(box);
        if (hint is not null)
        {
            panel.Controls.Add(new Label { Text = hint, AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(540, 0), Margin = new Padding(0, 2, 0, 0) });
        }

        return panel;
    }

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
}
