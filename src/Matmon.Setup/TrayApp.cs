using System.ComponentModel;
using System.Diagnostics;
using System.ServiceProcess;
using System.Text.Json;
using Matmon.Agent.Tray;
using Matmon.Host.Services.SelfUpdate;

namespace Matmon.Setup;

/// <summary>What the tray icon can say about the Matmon service - by colour, so it reads at 16 px.</summary>
internal enum InstanceState
{
    NotInstalled,
    Running,
    Starting,
    NotAnswering,
    /// <summary>A probe whose service runs but which cannot reach its primary.</summary>
    Unreachable,
    Stopped
}

/// <summary>What a probe's <c>/api/probe/status</c> says (loopback only, never the token) - the facts of its /probe page.</summary>
internal sealed record ProbeTrayStatus(
    string? ProbeName,
    string? ProbeId,
    string? PrimaryUrl,
    bool Connected,
    string? StatusMessage,
    DateTimeOffset? LastHeartbeatUtc,
    DateTimeOffset? LastResultPostUtc,
    int AssignedSensorCount,
    int BufferedObservations,
    string? Update);

/// <summary>
/// The tray icon of the Windows instance - or of a Windows probe installed by the same setup: <c>%ProgramData%\Matmon\app\Matmon.exe /tray</c>,
/// started at every sign-in (HKLM Run) in the user's own session - the service runs in session 0 and can show nothing. It looks
/// at the service (status, which any user may read), at <c>/healthz</c> and, for a probe, at <c>/api/probe/status</c>; opens the
/// web interface (a probe: its status page and its primary), shows Info, and starts / stops / restarts the service through an
/// ELEVATED copy of itself (<c>/service=start|stop|restart</c>, one UAC prompt each) - it never holds administrator rights itself.
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    private const string MutexName = @"Local\MatmonInstanceTray";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly NotifyIcon _icon;
    private readonly Dictionary<InstanceState, Icon> _icons;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 5000 };
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly ToolStripMenuItem _titleItem = new() { Enabled = false };
    private readonly ToolStripMenuItem _stateItem = new() { Enabled = false };
    private readonly ToolStripMenuItem _openItem;
    private readonly ToolStripMenuItem _primaryItem;
    private readonly ToolStripMenuItem _logsItem;
    private readonly ToolStripMenuItem _updatesItem;
    private readonly ToolStripMenuItem _startItem;
    private readonly ToolStripMenuItem _stopItem;
    private readonly ToolStripMenuItem _restartItem;
    private InstanceState? _lastState;
    private bool _controlling;
    private InstanceState? _expected;
    private DateTime _expectedUntil;
    private bool _refreshing;
    private int _port = 8099;
    private string? _version;
    private WindowsInstallationInfo? _installation;
    private ServiceControllerStatus? _service;
    private ProbeTrayStatus? _probe;
    private BrandInfoForm? _infoForm;
    private bool _infoForProbe;
    private readonly string _startedFrom;
    private readonly (DateTime Written, long Length)? _startedStamp;
    private (DateTime Written, long Length)? _pendingStamp;
    private bool _restart;

    private TrayApp()
    {
        _icons = Enum.GetValues<InstanceState>().ToDictionary(state => state, state => TrayIcons.Create(ColorOf(state)));

        var shield = SystemIcons.Shield.ToBitmap();
        var menu = new ContextMenuStrip();
        _openItem = new ToolStripMenuItem("Open Matmon", null, (_, _) => OpenPage(IsProbe ? "/probe" : "/")) { Font = new Font(menu.Font, FontStyle.Bold) };
        _primaryItem = new ToolStripMenuItem("Open primary", null, (_, _) => OpenPrimary());
        _logsItem = new ToolStripMenuItem("Logs", null, (_, _) => OpenPage("/Logs"));
        _updatesItem = new ToolStripMenuItem("Updates", null, (_, _) => OpenPage("/system/updates"));
        _startItem = new ToolStripMenuItem("Start service", shield, (_, _) => ControlService("start"));
        _stopItem = new ToolStripMenuItem("Stop service", shield, (_, _) => ControlService("stop"));
        _restartItem = new ToolStripMenuItem("Restart service", shield, (_, _) => ControlService("restart"));

        menu.Items.Add(_titleItem);
        menu.Items.Add(_stateItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_openItem);
        menu.Items.Add(_primaryItem);
        menu.Items.Add(new ToolStripMenuItem("Info…", null, (_, _) => ShowInfo()));
        menu.Items.Add(_logsItem);
        menu.Items.Add(_updatesItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_startItem);
        menu.Items.Add(_stopItem);
        menu.Items.Add(_restartItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Close this icon", null, (_, _) => ExitThread()));

        _icon = new NotifyIcon { ContextMenuStrip = menu, Icon = _icons[InstanceState.NotInstalled], Text = "Matmon", Visible = true };
        _icon.DoubleClick += (_, _) => OpenDefault();
        _icon.BalloonTipClicked += (_, _) => OpenDefault();

        _startedFrom = Path.GetFullPath(SetupRunner.SelfPath);
        _startedStamp = Stamp(_startedFrom);

        _timer.Tick += async (_, _) =>
        {
            if (NewBuildInPlace())
            {
                _restart = true;
                ExitThread();
                return;
            }

            await RefreshAsync();
        };
        _timer.Start();
        _ = RefreshAsync();
    }

    private bool IsProbe => _installation?.Role == WindowsInstallationRole.Probe;

    private string Product => IsProbe ? "Matmon probe" : "Matmon";

    private static Color ColorOf(InstanceState state) => state switch
    {
        InstanceState.Running => TrayIcons.Ok,
        InstanceState.Starting or InstanceState.NotAnswering or InstanceState.Unreachable => TrayIcons.Warning,
        InstanceState.Stopped => TrayIcons.Error,
        _ => TrayIcons.Neutral
    };

    public static int Run()
    {
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var first);
        if (!first)
        {
            // The Start menu entry when the icon runs already: open Matmon (a probe: its status page) instead of doing nothing.
            try
            {
                var installed = WindowsInstallation.Find();
                var path = installed?.Role == WindowsInstallationRole.Probe ? "/probe" : "/";
                Process.Start(new ProcessStartInfo($"http://localhost:{installed?.Port ?? 8099}{path}") { UseShellExecute = true })?.Dispose();
            }
            catch (Win32Exception)
            {
            }

            return 0;
        }

        ApplicationConfiguration.Initialize();
        var tray = new TrayApp();
        Application.Run(tray);
        if (tray._restart)
        {
            // Hand over to the new build: the mutex first, or the new tray would see this one and quit.
            mutex.ReleaseMutex();
            Process.Start(new ProcessStartInfo(tray._startedFrom, "/tray") { UseShellExecute = false })?.Dispose();
        }

        return 0;
    }

    /// <summary>
    /// A self-update puts the new build's setup program where this tray was started from (the running file is renamed aside).
    /// The file must have changed AND look the same on two ticks in a row, so a copy still being written is never started.
    /// </summary>
    private bool NewBuildInPlace()
    {
        var stamp = Stamp(_startedFrom);
        if (stamp is null || stamp == _startedStamp)
        {
            _pendingStamp = null;
            return false;
        }

        if (stamp != _pendingStamp)
        {
            _pendingStamp = stamp;
            return false;
        }

        return true;
    }

    private static (DateTime Written, long Length)? Stamp(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? (file.LastWriteTimeUtc, file.Length) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public InstanceState State { get; private set; } = InstanceState.NotInstalled;

    private async Task RefreshAsync()
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        try
        {
            _installation = WindowsInstallation.Find();
            _port = _installation?.Port ?? 8099;
            _service = ReadServiceStatus();
            var answers = _service is ServiceControllerStatus.Running && await ReadHealthAsync();
            _probe = answers && IsProbe ? await ReadProbeStatusAsync() : null;
            State = _service switch
            {
                null => InstanceState.NotInstalled,
                ServiceControllerStatus.Running when !answers => InstanceState.NotAnswering,
                ServiceControllerStatus.Running when IsProbe && _probe is not { Connected: true } => InstanceState.Unreachable,
                ServiceControllerStatus.Running => InstanceState.Running,
                ServiceControllerStatus.StartPending or ServiceControllerStatus.ContinuePending => InstanceState.Starting,
                _ => InstanceState.Stopped
            };

            var version = Version;
            _titleItem.Text = string.IsNullOrWhiteSpace(version) ? Product : $"{Product} {version}";
            var text = Describe();
            _stateItem.Text = text;
            _icon.Icon = _icons[State];
            var tooltip = $"{Product} - {text}";
            _icon.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;

            var running = _service is ServiceControllerStatus.Running;
            _openItem.Text = IsProbe ? "Open status page" : "Open Matmon";
            _openItem.Enabled = running;
            _primaryItem.Visible = IsProbe;
            _primaryItem.Enabled = PrimaryAddress is not null;
            _logsItem.Visible = _updatesItem.Visible = !IsProbe;
            _logsItem.Enabled = _updatesItem.Enabled = running;
            _startItem.Enabled = _service is ServiceControllerStatus.Stopped or ServiceControllerStatus.Paused;
            _stopItem.Enabled = _restartItem.Enabled = running || _service is ServiceControllerStatus.StartPending;

            if (_infoForm is { IsDisposed: false, Visible: true })
            {
                ShowInfo(activate: false);
            }

            Announce();
            _lastState = State;
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>The running build (from /healthz), else the one the installation registered.</summary>
    private string? Version => _version ?? _installation?.Version;

    private string? PrimaryAddress => WindowsProbeSettings.DisplayAddress(_probe?.PrimaryUrl) ?? _installation?.PrimaryUrl;

    private string Describe() => State switch
    {
        InstanceState.Running => IsProbe ? $"connected to {PrimaryAddress ?? "its primary"}" : $"running on port {_port}",
        InstanceState.Starting => "starting ...",
        InstanceState.NotAnswering => $"the service runs, but Matmon does not answer on port {_port}",
        InstanceState.Unreachable => $"cannot reach {PrimaryAddress ?? "its primary"}",
        InstanceState.Stopped => _service is ServiceControllerStatus.StopPending ? "stopping ..." : "the service is stopped",
        _ => "not installed on this computer"
    };

    // A balloon when Matmon goes away or comes back on its own - not for the change the user just asked for.
    private void Announce()
    {
        if (_controlling)
        {
            return;
        }

        if (_expected is { } expected && (State == expected || DateTime.UtcNow > _expectedUntil))
        {
            _expected = null;
            return;
        }

        if (_expected is not null || _lastState is not { } previous || previous == State)
        {
            return;
        }

        if (State is InstanceState.Running && previous is InstanceState.Stopped or InstanceState.NotAnswering or InstanceState.Unreachable)
        {
            _icon.ShowBalloonTip(5000, $"{Product} is running again",
                IsProbe ? $"Connected to {PrimaryAddress ?? "its primary"}." : $"The web interface answers on port {_port}.", ToolTipIcon.Info);
        }
        else if (previous is InstanceState.Running && State is InstanceState.Stopped or InstanceState.NotAnswering or InstanceState.Unreachable)
        {
            _icon.ShowBalloonTip(8000, IsProbe ? "The Matmon probe has a problem" : "Matmon is not running",
                State switch
                {
                    InstanceState.Stopped => "The Matmon service has stopped.",
                    InstanceState.Unreachable => $"It cannot reach {PrimaryAddress ?? "its primary"} - results are kept and sent once it can.",
                    _ => $"Matmon does not answer on port {_port}."
                }, ToolTipIcon.Warning);
        }
    }

    private void ShowInfo(bool activate = true)
    {
        // The window is built for one role; a probe installed over (after an uninstall of) an instance gets a new one.
        if (_infoForm is null || _infoForm.IsDisposed || _infoForProbe != IsProbe)
        {
            _infoForm?.Dispose();
            _infoForProbe = IsProbe;
            var buttons = new List<Button>();
            if (IsProbe)
            {
                var primary = BrandChrome.FooterButton("Open primary", width: 112);
                primary.Click += (_, _) => OpenPrimary();
                var status = BrandChrome.FooterButton("Status page", width: 104);
                status.Click += (_, _) => OpenPage("/probe");
                buttons.AddRange([primary, status]);
            }
            else
            {
                var open = BrandChrome.FooterButton("Open Matmon", width: 124);
                open.Click += (_, _) => OpenPage("/");
                buttons.Add(open);
            }

            _infoForm = new BrandInfoForm(Product, null, VersionNote, [.. buttons]);
        }

        _infoForm.Note = VersionNote;
        _infoForm.ShowInfo(BuildInfo());
        if (activate)
        {
            _infoForm.Show();
            _infoForm.Activate();
        }
    }

    private string VersionNote
    {
        get
        {
            var version = Version ?? SetupRunner.Version;
            // The channel is a row of the window - beside three buttons the bar has room for the version only.
            return $"Version {version}";
        }
    }

    private static string ChannelOf(string version) =>
        version.StartsWith("nightly", StringComparison.OrdinalIgnoreCase) ? "nightly"
        : version.StartsWith("local", StringComparison.OrdinalIgnoreCase) ? "local"
        : "release";

    private InfoView BuildInfo() => BuildInfo(_installation, _service, State, Version, _probe);

    /// <summary>What the Info window says - static, so it can be rendered without a running service (see RenderCheck).</summary>
    internal static InfoView BuildInfo(WindowsInstallationInfo? installation, ServiceControllerStatus? service, InstanceState state, string? version, ProbeTrayStatus? probe)
    {
        var isProbe = installation?.Role == WindowsInstallationRole.Probe;
        var port = installation?.Port ?? 8099;
        var primaryAddress = WindowsProbeSettings.DisplayAddress(probe?.PrimaryUrl) ?? installation?.PrimaryUrl;
        var rows = new List<KeyValuePair<string, string>>();
        void Row(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                rows.Add(new(key, value));
            }
        }

        var serviceText = service switch
        {
            null => "not installed",
            ServiceControllerStatus.Running => "running",
            ServiceControllerStatus.StartPending => "starting",
            ServiceControllerStatus.StopPending => "stopping",
            _ => "stopped"
        };

        string title;
        string detail;
        if (isProbe)
        {
            (title, detail) = state switch
            {
                InstanceState.Running => ("Connected", $"{probe?.ProbeName ?? "This probe"} reports to {primaryAddress ?? "its primary"}."),
                InstanceState.Unreachable => (probe?.LastHeartbeatUtc is null ? "Not connected yet" : "Primary unreachable",
                    string.IsNullOrWhiteSpace(probe?.StatusMessage) ? $"It cannot reach {primaryAddress ?? "its primary"}." : probe.StatusMessage!),
                InstanceState.Starting => ("Starting", "The Matmon service is starting."),
                InstanceState.NotAnswering => ("Not answering", $"The service runs, but the probe does not answer on port {port}."),
                _ => ("Service not running", "The Matmon service is stopped - start it from the tray menu.")
            };
            Row("Primary", primaryAddress ?? "-");
            Row("Probe", probe is null ? null : probe.ProbeId is { Length: > 0 } id ? $"{probe.ProbeName} ({id})" : probe.ProbeName);
            Row("Last heartbeat", probe is null ? null
                : probe.LastHeartbeatUtc is { } beat ? $"{beat.ToLocalTime():g} ({BrandInfoForm.Ago(beat)})" : "none yet");
            Row("Sensors", probe?.AssignedSensorCount.ToString());
            Row("Results sent", probe?.LastResultPostUtc is { } sent ? $"{sent.ToLocalTime():g} ({BrandInfoForm.Ago(sent)})" : null);
            Row("Waiting to send", probe is { BufferedObservations: > 0 } waiting ? $"{waiting.BufferedObservations} results (sent once the primary answers)" : null);
            Row("Version", version);
            Row("Channel", ChannelOf(version ?? SetupRunner.Version));
            Row("Update", probe?.Update);
            Row("Service", serviceText);
            Row("Status page", $"http://localhost:{port}/probe");
            Row("Data folder", installation?.DataFolder);
        }
        else
        {
            (title, detail) = state switch
            {
                InstanceState.Running => ("Running", $"Matmon answers on http://localhost:{port}."),
                InstanceState.Starting => ("Starting", "The Matmon service is starting."),
                InstanceState.NotAnswering => ("Not answering", $"The service runs, but Matmon does not answer on port {port}."),
                InstanceState.Stopped => ("Service not running", "The Matmon service is stopped - start it from the tray menu."),
                _ => ("Not installed", "Matmon is not installed on this computer.")
            };
            Row("Address", installation is null ? null : $"http://localhost:{port}");
            Row("Version", version ?? "-");
            Row("Channel", ChannelOf(version ?? SetupRunner.Version));
            Row("Service", serviceText);
            Row("Program folder", installation?.ProgramFolder);
            Row("Data folder", installation?.DataFolder);
        }

        return new InfoView(ColorOf(state), title, detail, rows);
    }

    private static ServiceControllerStatus? ReadServiceStatus()
    {
        try
        {
            using var controller = new ServiceController(WindowsInstallation.ServiceName);
            return controller.Status;
        }
        catch (InvalidOperationException)
        {
            return null; // no such service
        }
    }

    private async Task<bool> ReadHealthAsync()
    {
        try
        {
            using var response = await _http.GetAsync($"http://127.0.0.1:{_port}/healthz");
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            try
            {
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String)
                {
                    _version = version.GetString();
                }
            }
            catch (JsonException)
            {
            }

            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private async Task<ProbeTrayStatus?> ReadProbeStatusAsync()
    {
        try
        {
            using var response = await _http.GetAsync($"http://127.0.0.1:{_port}/api/probe/status");
            return response.IsSuccessStatusCode
                ? JsonSerializer.Deserialize<ProbeTrayStatus>(await response.Content.ReadAsStringAsync(), Json)
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    private void OpenDefault()
    {
        if (IsProbe)
        {
            ShowInfo();
        }
        else
        {
            OpenPage("/");
        }
    }

    private void OpenPrimary()
    {
        if (PrimaryAddress is { } primary)
        {
            Open($"{primary}/Probes");
        }
    }

    private void OpenPage(string path) => Open($"http://localhost:{_port}{path}");

    private static void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
        }
    }

    /// <summary>The service needs administrator rights: an elevated copy of this program does it (one UAC prompt).</summary>
    private async void ControlService(string action)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(SetupRunner.SelfPath, $"/service={action}")
            {
                UseShellExecute = true,
                Verb = "runas"
            });
            if (process is null)
            {
                return;
            }

            // No balloon for what the user asked for: none while it happens, and none until the service got where it was sent.
            _controlling = true;
            _timer.Interval = 1500;
            await process.WaitForExitAsync();
            _controlling = false;
            if (process.ExitCode != 0)
            {
                _icon.ShowBalloonTip(8000, Product, $"The service could not be {Past(action)}. See {SetupRunner.LogPath}.", ToolTipIcon.Error);
            }
            else
            {
                _expected = action == "stop" ? InstanceState.Stopped : InstanceState.Running;
                _expectedUntil = DateTime.UtcNow.AddMinutes(3);
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // The UAC prompt was cancelled.
        }
        finally
        {
            await RefreshAsync();
            // Back to the calm cadence once the service has settled (it can take a while to start).
            _ = Task.Delay(TimeSpan.FromSeconds(60)).ContinueWith(_ => _timer.Interval = 5000, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }

    private static string Past(string action) => action switch
    {
        "start" => "started",
        "stop" => "stopped",
        _ => "restarted"
    };

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _icon.Visible = false;
        _icon.Dispose();
        foreach (var icon in _icons.Values)
        {
            icon.Dispose();
        }

        _infoForm?.Dispose();
        _http.Dispose();
        base.ExitThreadCore();
    }
}
