using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;

namespace Matmon.Agent;

/// <summary>
/// <c>matmon-agent.exe setup --url … --code … [--force] [--result &lt;file&gt;]</c> - Windows only, elevated.
///
/// Everything the tray's "Set up" dialog means, in one elevated run (one UAC prompt): enrol with the code,
/// install this executable to <c>%ProgramFiles%\Matmon Agent</c>, create the service (or restart the one that
/// is there), and register the tray to start at sign-in. So a user who downloaded the agent from the
/// Agents page double-clicks it, enters the code, and has a running agent - no PowerShell recipe.
///
/// The tray cannot read this run's console (an elevated process is started through the shell), so the
/// outcome goes to <c>--result</c> as a small JSON file.
/// </summary>
public static class SetupCommand
{
    public const string ServiceName = "Matmon Agent";
    private const string TrayRunValue = "Matmon Agent Tray";

    public static async Task<int> RunAsync(string[] args, string configPath)
    {
        var resultFile = EnrollCommand.ReadOption(args, "--result");
        SetupResult result;
        if (!OperatingSystem.IsWindows())
        {
            result = new SetupResult(false, 2, "setup is for Windows. On Linux use 'matmon-agent enroll' and the systemd unit from the Agents page.");
        }
        else
        {
            try
            {
                result = await SetUpWindowsAsync(args, configPath);
            }
            catch (Exception ex)
            {
                result = new SetupResult(false, 1, $"Setup failed: {ex.Message}");
            }
        }

        (result.Success ? Console.Out : Console.Error).WriteLine(result.Message);
        if (!string.IsNullOrWhiteSpace(resultFile))
        {
            File.WriteAllText(resultFile, JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }

        return result.ExitCode;
    }

    [SupportedOSPlatform("windows")]
    private static async Task<SetupResult> SetUpWindowsAsync(string[] args, string configPath)
    {
        var url = EnrollCommand.ReadOption(args, "--url");
        var code = EnrollCommand.ReadOption(args, "--code");
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(code))
        {
            return new SetupResult(false, 2, "Usage: matmon-agent setup --url <instance url> --code <enrolment code> [--force]");
        }

        // Enrol FIRST: a wrong code must not leave a half-installed service behind.
        var enrolled = await EnrollCommand.EnrollAsync(url, code, args.Contains("--force"), args.Contains("--allow-http"), configPath);
        if (!enrolled.Success)
        {
            return new SetupResult(false, enrolled.ExitCode, enrolled.Message);
        }

        var self = Environment.ProcessPath ?? throw new InvalidOperationException("the agent does not know its own executable");
        var target = InstalledPath;
        var installDirectory = Path.GetDirectoryName(target)!;
        var service = AgentServiceControl.ResolveWindowsServiceName(target);

        if (service is not null)
        {
            AgentServiceControl.Stop(service, TimeSpan.FromSeconds(60));
        }

        if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(installDirectory);
            // The same set-aside swap as the updater: a tray still running the installed copy must not block it.
            ApplyUpdateCommand.Replace(self, target, move: false);
        }

        if (service is null)
        {
            service = ServiceName;
            Sc("create", $"\"{service}\"", $"binPath= \"\\\"{target}\\\"\"", "start= auto", $"DisplayName= \"{service}\"");
            Sc("description", $"\"{service}\"", "\"Matmon monitoring agent - runs sensors on this machine for its Matmon instance.\"");
            // Restart after a crash (10 s, then 60 s), forget failures after a day.
            Sc("failure", $"\"{service}\"", "reset= 86400", "actions= restart/10000/restart/60000/restart/60000");
        }

        AgentServiceControl.Start(service, TimeSpan.FromSeconds(60));

        // The tray for every user who signs in on this machine. conhost --headless runs the (console) executable
        // without flashing a console window at sign-in.
        using (var run = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"))
        {
            run.SetValue(TrayRunValue, $"conhost.exe --headless \"{target}\" tray");
        }

        return new SetupResult(true, 0, $"{enrolled.Message.Split(". Identity")[0]}. The '{service}' service is running.", target);
    }

    /// <summary>Where setup installs the agent.</summary>
    public static string InstalledPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Matmon Agent", "matmon-agent.exe");

    /// <summary>The installed service's name, or null when setup has not run on this machine.</summary>
    [SupportedOSPlatform("windows")]
    public static string? InstalledServiceName() => AgentServiceControl.ResolveWindowsServiceName(InstalledPath);

    private static void Sc(params string[] arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("sc.exe", string.Join(' ', arguments))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true
        }) ?? throw new InvalidOperationException("sc.exe did not start");
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"sc.exe {arguments[0]} failed ({process.ExitCode}): {output.Trim()}");
        }
    }
}

public sealed record SetupResult(bool Success, int ExitCode, string Message, string? InstalledPath = null);
