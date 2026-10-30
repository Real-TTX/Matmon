namespace Matmon.Host.Services.SelfUpdate;

/// <summary>
/// The Windows setup's command line (Matmon.Setup links this file; it lives here so the tests can reach it):
/// <c>MatmonSetup.exe [/silent] [/dir=&lt;folder&gt;] [/data=&lt;folder&gt;] [/port=8099] [/firewall] [/notray]</c> installs or
/// updates; <c>/probe /primary=&lt;url&gt; /probeid=&lt;id&gt; /token=&lt;token&gt; [/probename=&lt;name&gt;]</c> installs a remote probe
/// instead of the instance; <c>/uninstall [/silent] [/removedata]</c> removes. <c>/package=&lt;zip&gt;</c> installs from a separate
/// package. Without an appended package the setup is the ONLINE setup: it downloads the newest package of its channel
/// (<c>/feed=&lt;url&gt;</c> another feed). The installed copy also runs the tray icon (<c>/tray</c>) and controls the service
/// for it (<c>/service=start|stop|restart</c>).
/// </summary>
public sealed record SetupArguments(
    bool Uninstall,
    bool Silent,
    string? ProgramFolder,
    string? DataFolder,
    int? Port,
    bool OpenFirewall,
    bool RemoveData,
    string? PackagePath,
    bool Relaunched,
    bool Tray,
    bool NoTray,
    string? ServiceAction,
    string? FeedUrl,
    bool Probe = false,
    string? PrimaryUrl = null,
    string? ProbeId = null,
    string? ProbeToken = null,
    string? ProbeName = null)
{
    /// <summary>What needs administrator rights: everything but the tray icon.</summary>
    public bool NeedsElevation => !Tray;

    /// <summary>A probe was asked for: <c>/probe</c>, or any of its values.</summary>
    public bool WantsProbe => Probe || PrimaryUrl is not null || ProbeId is not null || ProbeToken is not null;

    public static SetupArguments Parse(string[] args)
    {
        string? Value(string name) => args
            .Select(arg => arg.Trim())
            .FirstOrDefault(arg => arg.StartsWith($"/{name}=", StringComparison.OrdinalIgnoreCase) || arg.StartsWith($"--{name}=", StringComparison.OrdinalIgnoreCase))
            ?.Split('=', 2)[1].Trim().Trim('"') is { Length: > 0 } value ? value : null;
        bool Flag(params string[] names) => args.Any(arg => names.Any(name =>
            string.Equals(arg, $"/{name}", StringComparison.OrdinalIgnoreCase) || string.Equals(arg, $"--{name}", StringComparison.OrdinalIgnoreCase)));

        return new SetupArguments(
            Flag("uninstall", "u"),
            Flag("silent", "quiet", "s", "q"),
            Value("dir"),
            Value("data"),
            int.TryParse(Value("port"), out var port) && port is > 0 and < 65536 ? port : null,
            Flag("firewall"),
            Flag("removedata"),
            Value("package"),
            Flag("relaunched"),
            Flag("tray"),
            Flag("notray"),
            Value("service")?.ToLowerInvariant() is { } action && action is "start" or "stop" or "restart" ? action : null,
            Value("feed"),
            Flag("probe"),
            Value("primary"),
            Value("probeid"),
            Value("token"),
            Value("probename"));
    }

    /// <summary>
    /// Which role the setup installs, and with which connection: an explicit probe (its values, completed from what an
    /// installed probe already has), an update of an installed probe (its stored connection, unchanged), or the instance.
    /// Refused in words: a role change (<see cref="WindowsProbeSettings.RoleChangeProblem"/>) and an incomplete connection.
    /// </summary>
    /// <param name="stored">The installed probe's connection, read from its data folder (null when there is none).</param>
    public (WindowsProbeConnection? Probe, string? Problem) ResolveProbe(WindowsInstallationInfo? installed, WindowsProbeConnection? stored)
    {
        var installedProbe = installed?.Role == WindowsInstallationRole.Probe;
        if (!WantsProbe && !installedProbe)
        {
            return (null, WindowsProbeSettings.RoleChangeProblem(installed, probe: false));
        }

        if (WantsProbe && WindowsProbeSettings.RoleChangeProblem(installed, probe: true) is { } change)
        {
            return (null, change);
        }

        var connection = new WindowsProbeConnection(
            PrimaryUrl ?? stored?.PrimaryUrl ?? string.Empty,
            ProbeId ?? stored?.ProbeId ?? string.Empty,
            ProbeToken ?? stored?.ProbeToken ?? string.Empty,
            ProbeName ?? stored?.ProbeName);
        if (connection.Problem() is { } problem)
        {
            return (null, installedProbe && !WantsProbe && stored is null
                ? "This computer runs a Matmon probe, but its connection could not be read. Run the setup with /probe /primary= /probeid= /token= (from the probe's Install page on the primary)."
                : problem);
        }

        return (connection, null);
    }
}
