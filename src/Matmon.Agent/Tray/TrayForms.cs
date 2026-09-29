#if WINDOWS
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;

namespace Matmon.Agent.Tray;

/// <summary>What the agent is doing right now - read from the service, refreshed with the tray.</summary>
internal sealed class StatusForm : Form
{
    private readonly TrayApplication _tray;
    private readonly Label _headline = new() { AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 11f, FontStyle.Bold) };
    private readonly TableLayoutPanel _rows = new() { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(0, 8, 0, 8) };
    private readonly Button _openInstance = new() { Text = "Open instance", AutoSize = true };

    public StatusForm(TrayApplication tray)
    {
        _tray = tray;
        Text = "Matmon Agent";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);
        Font = SystemFonts.MessageBoxFont!;

        _rows.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _rows.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, FlowDirection = FlowDirection.LeftToRight };
        _openInstance.Click += (_, _) => _tray.OpenInstance();
        var logs = new Button { Text = "Open log folder", AutoSize = true };
        logs.Click += (_, _) => _tray.OpenLogFolder();
        var close = new Button { Text = "Close", AutoSize = true };
        close.Click += (_, _) => Hide();
        buttons.Controls.AddRange([_openInstance, logs, close]);
        CancelButton = close;

        var layout = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Dock = DockStyle.Fill };
        layout.Controls.AddRange([_headline, _rows, buttons]);
        Controls.Add(layout);
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
        _headline.Text = char.ToUpperInvariant(description[0]) + description[1..];
        _headline.ForeColor = state switch
        {
            TrayState.Connected => Color.FromArgb(0x1E, 0x8E, 0x55),
            TrayState.Unreachable => Color.FromArgb(0xB0, 0x74, 0x10),
            TrayState.Stopped => Color.FromArgb(0xC0, 0x2F, 0x44),
            _ => SystemColors.ControlText
        };
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
    }

    private void Row(string label, string value)
    {
        _rows.Controls.Add(new Label { Text = label, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 3, 16, 3) });
        _rows.Controls.Add(new Label { Text = value, AutoSize = true, MaximumSize = new Size(460, 0), Margin = new Padding(0, 3, 0, 3) });
    }

    private static string Ago(DateTimeOffset when)
    {
        var elapsed = DateTimeOffset.UtcNow - when;
        return elapsed.TotalSeconds < 90 ? $"{Math.Max(0, (int)elapsed.TotalSeconds)} s ago"
            : elapsed.TotalMinutes < 90 ? $"{(int)elapsed.TotalMinutes} min ago"
            : $"{(int)elapsed.TotalHours} h ago";
    }
}

/// <summary>
/// "Set up": the instance URL and a code from its Agents page. Runs <c>setup</c> elevated (one UAC prompt),
/// which enrols, installs to Program Files, creates or restarts the service and registers this tray.
/// </summary>
internal sealed class SetupForm : Form
{
    private readonly TrayApplication _tray;
    private readonly TextBox _url = new() { Width = 380, PlaceholderText = "https://matmon.example" };
    private readonly TextBox _code = new() { Width = 380, PlaceholderText = "XXXX-XXXX-XXXX-XXXX-XXXX", CharacterCasing = CharacterCasing.Upper };
    private readonly Label _replaceWarning = new() { AutoSize = true, MaximumSize = new Size(380, 0), ForeColor = Color.FromArgb(0xB0, 0x74, 0x10), Visible = false };
    private readonly Label _result = new() { AutoSize = true, MaximumSize = new Size(380, 0) };
    private readonly Button _setUp = new() { Text = "Set up", AutoSize = true };

    public SetupForm(TrayApplication tray)
    {
        _tray = tray;
        Text = "Set up Matmon Agent";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);
        Font = SystemFonts.MessageBoxFont!;

        var intro = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(380, 0),
            Text = "Create an enrolment code under Agents on your Matmon instance, then enter it here. " +
                "Setting up needs administrator rights once."
        };

        var cancel = new Button { Text = "Cancel", AutoSize = true };
        cancel.Click += (_, _) => Close();
        _setUp.Click += async (_, _) => await SetUpAsync();
        AcceptButton = _setUp;
        CancelButton = cancel;

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 12, 0, 0) };
        buttons.Controls.AddRange([_setUp, cancel]);

        var layout = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Dock = DockStyle.Fill };
        layout.Controls.AddRange(
        [
            intro,
            Caption("Instance URL"), _url,
            Caption("Enrolment code"), _code,
            _replaceWarning,
            buttons,
            _result
        ]);
        Controls.Add(layout);
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
        var arguments = $"setup --url \"{url}\" --code \"{code}\" --result \"{resultFile}\"" + (_tray.Status is not null ? " --force" : string.Empty);

        _setUp.Enabled = false;
        ShowResult(null, "Setting up…");
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
            _setUp.Enabled = true;
        }
    }

    private void ShowResult(bool? success, string message)
    {
        _result.Text = message;
        _result.ForeColor = success switch
        {
            true => Color.FromArgb(0x1E, 0x8E, 0x55),
            false => Color.FromArgb(0xC0, 0x2F, 0x44),
            null => SystemColors.ControlText
        };
    }

    private static Label Caption(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(0, 10, 0, 2) };
}
#endif
