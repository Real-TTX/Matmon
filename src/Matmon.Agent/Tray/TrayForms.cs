#if WINDOWS
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;
using Matmon.Probe;

namespace Matmon.Agent.Tray;

/// <summary>The agent's windows: the shared frame (<see cref="BrandWindow"/>) with the agent's violet-to-blue mark.</summary>
internal abstract class AgentWindow(string title, string subtitle) : BrandWindow(title, "Matmon Agent", subtitle, MarkPalette.Agent)
{
    /// <summary>What the bar says on the left - the same words in every agent window.</summary>
    public static string VersionNote => $"Version {MatmonVersion.Current} · {MatmonVersion.Channel.ToLowerInvariant()} channel";

    protected void Complete(params Button[] buttons) => Complete(VersionNote, buttons);
}

/// <summary>What the agent's Info window says - from the service's status pipe. Pure, so it is tested without a window.</summary>
internal static class AgentInfo
{
    public static InfoView Build(AgentStatusSnapshot? status, TrayState state, string description)
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

        var rows = new List<KeyValuePair<string, string>>();
        void Row(string key, string value) => rows.Add(new(key, value));
        if (status is null)
        {
            Row("Version", MatmonVersion.Current);
            Row("Channel", MatmonVersion.Channel.ToLowerInvariant());
            Row("Service", state == TrayState.Stopped ? "installed, not running" : "not installed");
            Row("Next step", state == TrayState.Stopped ? "start the \"Matmon Agent\" service" : "Set up… from the tray menu");
        }
        else
        {
            Row("Instance", status.PrimaryUrl ?? "-");
            Row("Probe", status.ProbeId is { Length: > 0 } id ? $"{status.ProbeName} ({id})" : status.ProbeName ?? "-");
            Row("Last heartbeat", status.LastHeartbeatUtc is { } beat ? $"{beat.ToLocalTime():g} ({BrandInfoForm.Ago(beat)})" : "none yet");
            Row("Sensors", status.AssignedSensorCount.ToString());
            if (status.BufferedObservations > 0)
            {
                Row("Waiting to send", $"{status.BufferedObservations} results (sent once the instance answers)");
            }

            Row("Version", status.Version);
            Row("Channel", MatmonVersion.Channel.ToLowerInvariant());
            Row("Last update", status.UpdateStatus ?? "none");
            Row("Running since", status.StartedUtc.ToLocalTime().ToString("g"));
            Row("Config", status.ConfigPath);
        }

        return new InfoView(color, title, detail, rows);
    }

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
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
    private readonly LinkLabel _change = new() { Text = "Change", AutoSize = true, Margin = new Padding(0, 0, 0, 10), LinkColor = Color.FromArgb(0x1A, 0x6F, 0xD0) };

    /// <summary>The values came from the download and "Change" was not pressed.</summary>
    private bool IsLocked => _locked;

    private bool _locked;

    /// <summary>What a "ready to install" download carries (instance URL + code), or null for a plain package.</summary>
    public static AgentInstallerSettings? Embedded { get; } = AgentInstallerPayload.ReadFile(Environment.ProcessPath);

    public SetupForm(TrayApplication tray) : this(tray, Embedded)
    {
    }

    internal SetupForm(TrayApplication? tray, AgentInstallerSettings? embedded) : base("Set up Matmon Agent", embedded is null ? "Set up" : "Install")
    {
        _tray = tray!;

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

        if (embedded is null)
        {
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
        }
        else
        {
            // A download from the Agents page carries both values: one click. They stay editable behind "Change" - a
            // code that was used meanwhile is replaced by typing a new one rather than downloading again.
            _url.Text = embedded.Url;
            _code.Text = embedded.Code;
            _url.ReadOnly = true;
            _code.ReadOnly = true;
            _setUp.Text = "Install";
            _locked = true;
            _change.LinkClicked += (_, _) =>
            {
                _url.ReadOnly = false;
                _code.ReadOnly = false;
                _locked = false;
                _change.Visible = false;
                FitHeight();
                _code.Focus();
                _code.SelectAll();
            };
            Content.Controls.AddRange(
            [
                BrandChrome.Heading("Ready to install"),
                BrandChrome.Paragraph(
                    "This download connects the machine to the Matmon instance below. Installing creates the \"Matmon Agent\" " +
                    "service and needs administrator rights once.", width: ContentWidth),
                FieldLabel("Instance URL"), _url,
                Hint("Taken from the download."),
                FieldLabel("Enrolment code"), _code,
                Hint("Single use. If it was used or has expired, create a new one under Probes > Agents and download again."),
                _change,
                _replaceWarning,
                _busy,
                _result
            ]);
        }

        Complete(_cancel, _setUp);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        var status = _tray?.Status;
        if (string.IsNullOrWhiteSpace(_url.Text) && !string.IsNullOrWhiteSpace(status?.PrimaryUrl))
        {
            _url.Text = status.PrimaryUrl;
        }

        _replaceWarning.Visible = status is not null;
        _replaceWarning.Text = status is null
            ? string.Empty
            : $"This machine is already set up as '{status.ProbeName}'. Setting it up again replaces that identity with a new probe.";
        FitHeight();
        (_locked ? (Control)_setUp : string.IsNullOrWhiteSpace(_url.Text) ? _url : _code).Focus();
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

            ShowResult(result.Success, !result.Success && IsLocked && result.ExitCode == 5
                ? $"{result.Message} This download's code was already used or has expired - create a new one under Probes > Agents and download again, or press Change and enter a new code."
                : result.Message);
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
        _url.ReadOnly = busy || IsLocked;
        _code.ReadOnly = busy || IsLocked;
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
