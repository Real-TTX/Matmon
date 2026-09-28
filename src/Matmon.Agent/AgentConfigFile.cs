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

        return OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Matmon Agent", "agent.json")
            : "/etc/matmon-agent/agent.json";
    }

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
