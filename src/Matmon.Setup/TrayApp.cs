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
    Stopped
}

/// <summary>
/// The tray icon of the Windows instance: <c>Matmon Setup.exe /tray</c>, started at every sign-in (HKLM Run) in the user's
/// own session - the service runs in session 0 and can show nothing. It looks at the service (status, which any user may
/// read) and at <c>/healthz</c>, opens the web interface, and starts / stops / restarts the service through an ELEVATED
/// copy of itself (<c>/service=start|stop|restart</c>, one UAC prompt each) - it never holds administrator rights itself.
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    private const string MutexName = @"Local\MatmonInstanceTray";

    private readonly NotifyIcon _icon;
    private readonly Dictionary<InstanceState, Icon> _icons;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 5000 };
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly ToolStripMenuItem _titleItem = new() { Enabled = false };
    private readonly ToolStripMenuItem _stateItem = new() { Enabled = false };
    private readonly ToolStripMenuItem _openItem;
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
    private readonly string _startedFrom;
    private readonly (DateTime Written, long Length)? _startedStamp;
    private (DateTime Written, long Length)? _pendingStamp;
    private bool _restart;

    private TrayApp()
    {
        _icons = Enum.GetValues<InstanceState>().ToDictionary(state => state, state => TrayIcons.Create(state switch
        {
            InstanceState.Running => TrayIcons.Ok,
            InstanceState.Starting or InstanceState.NotAnswering => TrayIcons.Warning,
            InstanceState.Stopped => TrayIcons.Error,
            _ => TrayIcons.Neutral
        }));

        var shield = SystemIcons.Shield.ToBitmap();
        var menu = new ContextMenuStrip();
        _openItem = new ToolStripMenuItem("Open Matmon", null, (_, _) => OpenPage("/")) { Font = new Font(menu.Font, FontStyle.Bold) };
        _logsItem = new ToolStripMenuItem("Logs", null, (_, _) => OpenPage("/Logs"));
        _updatesItem = new ToolStripMenuItem("Updates", null, (_, _) => OpenPage("/system/updates"));
        _startItem = new ToolStripMenuItem("Start service", shield, (_, _) => ControlService("start"));
        _stopItem = new ToolStripMenuItem("Stop service", shield, (_, _) => ControlService("stop"));
        _restartItem = new ToolStripMenuItem("Restart service", shield, (_, _) => ControlService("restart"));

        menu.Items.Add(_titleItem);
        menu.Items.Add(_stateItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_openItem);
        menu.Items.Add(_logsItem);
        menu.Items.Add(_updatesItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_startItem);
        menu.Items.Add(_stopItem);
        menu.Items.Add(_restartItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Close this icon", null, (_, _) => ExitThread()));

        _icon = new NotifyIcon { ContextMenuStrip = menu, Icon = _icons[InstanceState.NotInstalled], Text = "Matmon", Visible = true };
        _icon.DoubleClick += (_, _) => OpenPage("/");
        _icon.BalloonTipClicked += (_, _) => OpenPage("/");

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

    public static int Run()
    {
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var first);
        if (!first)
        {
            // The Start menu entry when the icon runs already: open Matmon instead of doing nothing.
            try
            {
                Process.Start(new ProcessStartInfo($"http://localhost:{WindowsInstallation.Find()?.Port ?? 8099}/") { UseShellExecute = true })?.Dispose();
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
            var installation = WindowsInstallation.Find();
            _port = installation?.Port ?? 8099;
            var service = ReadServiceStatus();
            State = service switch
            {
                null => InstanceState.NotInstalled,
                ServiceControllerStatus.Running => await ReadHealthAsync() ? InstanceState.Running : InstanceState.NotAnswering,
                ServiceControllerStatus.StartPending or ServiceControllerStatus.ContinuePending => InstanceState.Starting,
                _ => InstanceState.Stopped
            };

            var version = _version ?? installation?.Version;
            _titleItem.Text = string.IsNullOrWhiteSpace(version) ? "Matmon" : $"Matmon {version}";
            var text = Describe(service);
            _stateItem.Text = text;
            _icon.Icon = _icons[State];
            var tooltip = $"Matmon - {text}";
            _icon.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;

            var running = service is ServiceControllerStatus.Running;
            _openItem.Enabled = _logsItem.Enabled = _updatesItem.Enabled = running;
            _startItem.Enabled = service is ServiceControllerStatus.Stopped or ServiceControllerStatus.Paused;
            _stopItem.Enabled = _restartItem.Enabled = running || service is ServiceControllerStatus.StartPending;

            Announce();
            _lastState = State;
        }
        finally
        {
            _refreshing = false;
        }
    }

    private string Describe(ServiceControllerStatus? service) => State switch
    {
        InstanceState.Running => $"running on port {_port}",
        InstanceState.Starting => "starting ...",
        InstanceState.NotAnswering => $"the service runs, but Matmon does not answer on port {_port}",
        InstanceState.Stopped => service is ServiceControllerStatus.StopPending ? "stopping ..." : "the service is stopped",
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

        if (State is InstanceState.Running && previous is InstanceState.Stopped or InstanceState.NotAnswering)
        {
            _icon.ShowBalloonTip(5000, "Matmon is running again", $"The web interface answers on port {_port}.", ToolTipIcon.Info);
        }
        else if (previous is InstanceState.Running && State is InstanceState.Stopped or InstanceState.NotAnswering)
        {
            _icon.ShowBalloonTip(8000, "Matmon is not running",
                State is InstanceState.Stopped ? "The Matmon service has stopped." : $"Matmon does not answer on port {_port}.", ToolTipIcon.Warning);
        }
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

    private void OpenPage(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo($"http://localhost:{_port}{path}") { UseShellExecute = true });
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
                _icon.ShowBalloonTip(8000, "Matmon", $"The service could not be {Past(action)}. See {SetupRunner.LogPath}.", ToolTipIcon.Error);
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

        _http.Dispose();
        base.ExitThreadCore();
    }
}
