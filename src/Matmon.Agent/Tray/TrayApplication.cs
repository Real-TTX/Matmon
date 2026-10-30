#if WINDOWS
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Matmon.Probe;

namespace Matmon.Agent.Tray;

/// <summary>The four things the icon can say - by colour, so it reads at 16 px in a crowded tray.</summary>
public enum TrayState
{
    NotSetUp,
    Connected,
    Unreachable,
    Stopped
}

/// <summary>
/// The tray icon: a separate process in the signed-in user's session, because the service runs in session 0
/// and cannot show anything. It only LOOKS at the service (status pipe) and offers setup; it never talks to the
/// instance itself and holds no credentials.
/// </summary>
public sealed class TrayApplication : ApplicationContext
{
    private const string MutexName = @"Local\MatmonAgentTray";
    // A second start (the Start menu entry while the tray runs) asks the running tray to show its Info window.
    private const string ShowInfoEventName = @"Local\MatmonAgentTrayShowInfo";

    private readonly NotifyIcon _icon;
    private readonly Dictionary<TrayState, Icon> _icons;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 5000 };
    private readonly ToolStripMenuItem _stateItem = new() { Enabled = false };
    private readonly ToolStripMenuItem _openInstanceItem;
    private readonly DateTime _installedWriteTimeAtStart;
    private BrandInfoForm? _infoForm;
    private SetupForm? _setupForm;
    private bool _refreshing;

    public AgentStatusSnapshot? Status { get; private set; }

    public TrayState State { get; private set; } = TrayState.NotSetUp;

    private TrayApplication()
    {
        _icons = Enum.GetValues<TrayState>().ToDictionary(state => state, state => TrayIcons.Create(state switch
        {
            TrayState.Connected => TrayIcons.Ok,
            TrayState.Unreachable => TrayIcons.Warning,
            TrayState.Stopped => TrayIcons.Error,
            _ => TrayIcons.Neutral
        }, MarkPalette.Agent));
        _installedWriteTimeAtStart = WriteTime(SetupCommand.InstalledPath);

        _openInstanceItem = new ToolStripMenuItem("Open Matmon", null, (_, _) => OpenInstance());
        var menu = new ContextMenuStrip();
        _openInstanceItem.Font = new Font(menu.Font, FontStyle.Bold);
        menu.Items.Add(new ToolStripMenuItem($"Matmon Agent {MatmonVersion.Current}") { Enabled = false });
        menu.Items.Add(_stateItem);
        menu.Items.Add(new ToolStripSeparator());
        // The same order and the same bold default as the instance's tray: Open Matmon (also a double-click), then Info.
        menu.Items.Add(_openInstanceItem);
        menu.Items.Add(new ToolStripMenuItem("Info…", null, (_, _) => ShowInfo()));
        menu.Items.Add(new ToolStripMenuItem("Set up…", null, (_, _) => ShowSetup()));
        menu.Items.Add(new ToolStripMenuItem("Open log folder", null, (_, _) => OpenLogFolder()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()));

        _icon = new NotifyIcon { ContextMenuStrip = menu, Icon = _icons[State], Text = "Matmon Agent", Visible = true };
        _icon.DoubleClick += (_, _) => { if (_openInstanceItem.Enabled) { OpenInstance(); } else { ShowInfo(); } };

        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        _ = StartAsync();
    }

    public static void Run()
    {
        // Started from a console (double-click, or "matmon-agent.exe tray" in a terminal): let the window go.
        FreeConsole();

        using var showInfo = new EventWaitHandle(false, EventResetMode.AutoReset, ShowInfoEventName);
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var first);
        if (!first)
        {
            // The Start menu entry while the icon runs already: show its Info window instead of doing nothing.
            showInfo.Set();
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        var tray = new TrayApplication();
        var ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        var listener = new Thread(() =>
        {
            try
            {
                while (showInfo.WaitOne())
                {
                    ui.Post(_ => tray.ShowInfo(), null);
                }
            }
            catch (ObjectDisposedException)
            {
            }
        }) { IsBackground = true };
        listener.Start();
        Application.Run(tray);
    }

    private async Task StartAsync()
    {
        await RefreshAsync();
        if (State == TrayState.NotSetUp || SetupForm.Embedded is not null)
        {
            // Nothing to watch yet - the one thing to do is set it up, so say so instead of sitting silently.
            ShowSetup();
        }
    }

    public async Task RefreshAsync()
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        try
        {
            Status = await AgentStatusPipeClient.TryGetAsync();
            State = Status switch
            {
                { Connected: true } => TrayState.Connected,
                not null => TrayState.Unreachable,
                null when SetupCommand.InstalledServiceName() is not null => TrayState.Stopped,
                _ => TrayState.NotSetUp
            };

            _icon.Icon = _icons[State];
            var text = Describe();
            _stateItem.Text = text;
            // NotifyIcon tooltips are capped at 127 characters.
            var tooltip = $"Matmon Agent - {text}";
            _icon.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;
            _openInstanceItem.Enabled = !string.IsNullOrWhiteSpace(Status?.PrimaryUrl);
            _infoForm?.ShowInfo(AgentInfo.Build(Status, State, text));

            RestartIfUpdated();
        }
        finally
        {
            _refreshing = false;
        }
    }

    public string Describe() => State switch
    {
        TrayState.Connected => $"{Status!.ProbeName}: connected",
        TrayState.Unreachable => $"{Status!.ProbeName}: instance not reachable - {Status.StatusMessage}",
        TrayState.Stopped => "the Matmon Agent service is not running",
        _ => "not set up"
    };

    public void OpenInstance()
    {
        if (!string.IsNullOrWhiteSpace(Status?.PrimaryUrl))
        {
            Open(Status.PrimaryUrl.TrimEnd('/') + "/agents");
        }
    }

    public void OpenLogFolder()
    {
        var folder = Status is { } status
            ? Path.Combine(status.StateDirectory, "update")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Matmon Agent");
        Open(Directory.Exists(folder) ? folder : Path.GetDirectoryName(folder)!);
    }

    private void ShowInfo()
    {
        if (_infoForm is null || _infoForm.IsDisposed)
        {
            var openInstance = BrandChrome.FooterButton("Open Matmon", width: 124);
            openInstance.Click += (_, _) => OpenInstance();
            var logs = BrandChrome.FooterButton("Open log folder", width: 120);
            logs.Click += (_, _) => OpenLogFolder();
            _infoForm = new BrandInfoForm("Matmon Agent", MarkPalette.Agent, $"Version {MatmonVersion.Current}", openInstance, logs);
        }

        _infoForm.ShowInfo(AgentInfo.Build(Status, State, Describe()));
        _infoForm.Show();
        _infoForm.Activate();
    }

    private void ShowSetup()
    {
        if (_setupForm is null || _setupForm.IsDisposed)
        {
            _setupForm = new SetupForm(this);
        }

        _setupForm.Show();
        _setupForm.Activate();
    }

    /// <summary>
    /// After the agent updated itself, the executable under Program Files is a new file while this tray
    /// still runs the old one (set aside by the updater). Start the new one and step down - but only when
    /// the installed file really changed, or two trays of different builds would hand over forever.
    /// </summary>
    private void RestartIfUpdated()
    {
        var installed = SetupCommand.InstalledPath;
        if (Status is null ||
            string.Equals(Status.Version, MatmonVersion.Current, StringComparison.OrdinalIgnoreCase) ||
            WriteTime(installed) == _installedWriteTimeAtStart)
        {
            return;
        }

        StartInstalledTrayAndExit();
    }

    /// <summary>Hands over to the tray of the installed copy (after an update, or after setup from a download).</summary>
    public void StartInstalledTrayAndExit()
    {
        if (!File.Exists(SetupCommand.InstalledPath))
        {
            return;
        }

        _icon.Visible = false;
        // The new tray takes the single-instance mutex; ours is released when this process ends, so give it a
        // moment to retry instead of racing it (see TrayApplication.Run - a second instance simply exits).
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 2 /nobreak >nul & start \"\" /b \"{SetupCommand.InstalledPath}\" tray")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        });
        ExitThread();
    }

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _icon.Visible = false;
        _icon.Dispose();
        foreach (var icon in _icons.Values)
        {
            icon.Dispose();
        }

        base.ExitThreadCore();
    }

    private static void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch
        {
        }
    }

    private static DateTime WriteTime(string path) => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;

    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();
}

#endif
