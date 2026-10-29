using System.Security;

namespace Matmon.Agent;

/// <summary>
/// What the agent's installer writes for the service manager of a Linux or macOS machine - pure text, so it is tested
/// rather than discovered on a customer's machine.
/// </summary>
public static class AgentServiceFiles
{
    /// <summary>Where <c>install</c> puts the executable on Linux and macOS.</summary>
    public const string UnixInstallPath = "/usr/local/bin/matmon-agent";

    public const string SystemdUnitPath = "/etc/systemd/system/matmon-agent.service";

    /// <summary>The launchd job's label - also the service "name" the auto-update stops and starts.</summary>
    public const string LaunchdLabel = "eu.matmon.agent";

    public const string LaunchdPlistPath = "/Library/LaunchDaemons/eu.matmon.agent.plist";

    /// <summary>Set in the plist's environment so the agent knows it runs under launchd (and as which job). launchd
    /// sets XPC_SERVICE_NAME too, but an explicit variable of our own cannot be confused with an interactive shell
    /// that happens to inherit one.</summary>
    public const string LaunchdLabelVariable = "MATMON_LAUNCHD_LABEL";

    public const string MacLogDirectory = "/Library/Logs/Matmon Agent";

    /// <summary>The systemd unit. <c>Type=notify</c>: the agent tells systemd when it is up (AddSystemd).</summary>
    public static string SystemdUnit(string executable = UnixInstallPath) =>
        $"""
        [Unit]
        Description=Matmon Agent
        After=network-online.target
        Wants=network-online.target

        [Service]
        Type=notify
        ExecStart={executable}
        Restart=always
        RestartSec=10

        [Install]
        WantedBy=multi-user.target

        """.ReplaceLineEndings("\n");

    /// <summary>
    /// The launchd job: started at boot (RunAtLoad), restarted when it exits (KeepAlive), as root (a LaunchDaemon).
    /// <c>AbandonProcessGroup</c> matters for the auto-update: launchd otherwise kills every process of the job's group
    /// when the job stops - the updater the agent started included, half-way through the swap.
    /// </summary>
    public static string LaunchdPlist(string executable = UnixInstallPath, string logDirectory = MacLogDirectory)
    {
        static string X(string value) => SecurityElement.Escape(value) ?? string.Empty;
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Label</key>
                <string>{X(LaunchdLabel)}</string>
                <key>ProgramArguments</key>
                <array>
                    <string>{X(executable)}</string>
                </array>
                <key>RunAtLoad</key>
                <true/>
                <key>KeepAlive</key>
                <true/>
                <key>ThrottleInterval</key>
                <integer>10</integer>
                <key>AbandonProcessGroup</key>
                <true/>
                <key>EnvironmentVariables</key>
                <dict>
                    <key>{X(LaunchdLabelVariable)}</key>
                    <string>{X(LaunchdLabel)}</string>
                </dict>
                <key>StandardOutPath</key>
                <string>{X(Path.Combine(logDirectory, "agent.log").Replace('\\', '/'))}</string>
                <key>StandardErrorPath</key>
                <string>{X(Path.Combine(logDirectory, "agent.log").Replace('\\', '/'))}</string>
            </dict>
            </plist>

            """.ReplaceLineEndings("\n");
    }
}
