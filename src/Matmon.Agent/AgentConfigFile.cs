using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Matmon.Agent;

/// <summary>
/// The agent's own config file - where <c>enroll</c> writes the identity it received and where the service
/// reads it from. Same shape as the Docker probe's settings (a <c>Matmon</c> section), so environment
/// variables and command-line switches override it exactly the way they override appsettings.
///
/// A machine-wide location rather than next to the executable: the service runs as LocalSystem / root, an
/// update replaces the executable's directory, and the identity has to survive both.
/// </summary>
public static class AgentConfigFile
{
    public const string EnvironmentVariable = "MATMON_AGENT_CONFIG";

    public static string Resolve(string[] args)
    {
        var index = Array.IndexOf(args, "--config");
        if (index >= 0 && index + 1 < args.Length && !string.IsNullOrWhiteSpace(args[index + 1]))
        {
            return Path.GetFullPath(args[index + 1]);
        }

        var configured = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Matmon Agent", "agent.json");
        }

        // macOS has no /etc convention for third-party daemons; machine-wide application state lives here (root-owned,
        // like the LaunchDaemon that reads it). The update records go beside it (ResolveStateDirectory).
        return OperatingSystem.IsMacOS() ? MacConfigPath : "/etc/matmon-agent/agent.json";
    }

    public const string MacConfigPath = "/Library/Application Support/Matmon Agent/agent.json";

    /// <summary>
    /// Where the agent keeps state that is not configuration - the auto-update's hand-shake files and its log.
    /// On Linux that is /var/lib, not /etc; everywhere else (and with an explicit --config) the config's folder.
    /// </summary>
    public static string ResolveStateDirectory(string configPath) =>
        OperatingSystem.IsLinux() && configPath == "/etc/matmon-agent/agent.json"
            ? "/var/lib/matmon-agent"
            : Path.GetDirectoryName(configPath)!;

    /// <summary>The probe this machine is already enrolled as, or null when it is not.</summary>
    public static string? ReadEnrolledProbeName(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            // Enrolled = has a token; the name is only for the message.
            var section = JsonNode.Parse(File.ReadAllText(path))?["Matmon"];
            if (section?["ProbeToken"] is null)
            {
                return null;
            }

            return section["ProbeName"]?.GetValue<string>() ?? section["ProbeId"]?.GetValue<string>() ?? "(unnamed)";
        }
        catch
        {
            return null;
        }
    }

    public static void Write(string path, string primaryUrl, string probeId, string probeToken, string probeName)
    {
        var document = new JsonObject
        {
            ["Matmon"] = new JsonObject
            {
                ["PrimaryUrl"] = primaryUrl,
                ["ProbeId"] = probeId,
                ["ProbeName"] = probeName,
                ["ProbeToken"] = probeToken
            }
        };

        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        if (OperatingSystem.IsWindows())
        {
            SecureDirectoryWindows(directory);
        }

        // The token is the agent's credential for its whole life: only the service account (SYSTEM / root), an
        // administrator and whoever ran enroll may read it. The file is CREATED with those permissions - no
        // "write, then restrict" window in which it exists with inherited, world-readable ones. (Restricting an
        // existing file also locks out a non-elevated caller, whose Administrators membership is deny-only.)
        var content = System.Text.Encoding.UTF8.GetBytes(document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        var temp = path + ".tmp";
        try
        {
            File.Delete(temp);
            using (var stream = CreateRestricted(temp, directory))
            {
                stream.Write(content);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private static FileStream CreateRestricted(string file, string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            return CreateRestrictedWindows(file);
        }

        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return new FileStream(file, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        });
    }

    /// <summary>
    /// The folder holds the identity AND the update hand-shake files the SYSTEM service and its updater read
    /// (pending.json, confirmed, last-update.json). Under ProgramData it would inherit "Users may create files":
    /// a standard user could plant those files, or create the folder before the install and own it. So it gets an
    /// ACL of its own - SYSTEM, Administrators and whoever ran enroll may change it, everyone else may only read
    /// (the tray opens the log folder) - and is owned by Administrators, whoever created it.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void SecureDirectoryWindows(string directory)
    {
        try
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            var writers = new List<SecurityIdentifier>
            {
                new(WellKnownSidType.LocalSystemSid, null),
                new(WellKnownSidType.BuiltinAdministratorsSid, null)
            };
            if (WindowsIdentity.GetCurrent().User is { } caller && !writers.Contains(caller))
            {
                writers.Add(caller);
            }

            foreach (var sid in writers)
            {
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            }

            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(directory).SetAccessControl(security);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or PrivilegeNotHeldException)
        {
            // Not elevated and not the owner: leave the folder as it is - the identity file keeps its own ACL.
            return;
        }

        try
        {
            // A folder a standard user created before the install would stay THEIRS (an owner can always rewrite
            // the ACL). Taking it over needs elevation - which enroll/setup have when they install the service.
            var owner = new DirectorySecurity();
            owner.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
            new DirectoryInfo(directory).SetAccessControl(owner);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or PrivilegeNotHeldException)
        {
        }
    }

    [SupportedOSPlatform("windows")]
    private static FileStream CreateRestrictedWindows(string file)
    {
        // No inheritance from ProgramData (which grants every local user read).
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var sids = new List<SecurityIdentifier>
        {
            new(WellKnownSidType.LocalSystemSid, null),
            new(WellKnownSidType.BuiltinAdministratorsSid, null)
        };
        if (WindowsIdentity.GetCurrent().User is { } caller && !sids.Contains(caller))
        {
            sids.Add(caller);
        }

        foreach (var sid in sids)
        {
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        }

        return new FileInfo(file).Create(FileMode.CreateNew, FileSystemRights.Write, FileShare.None, 4096, FileOptions.None, security);
    }
}
