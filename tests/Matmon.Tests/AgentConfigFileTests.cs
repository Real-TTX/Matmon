using System.Security.AccessControl;
using System.Security.Principal;
using Matmon.Agent;

namespace Matmon.Tests;

/// <summary>
/// The agent's config folder holds its identity and the update hand-shake files the SYSTEM service reads. Under
/// ProgramData it would inherit "Users may create files", so a standard user could plant those files - the folder
/// gets an ACL of its own that lets ordinary users read but not write.
/// </summary>
public sealed class AgentConfigFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "matmon-agentcfg-" + Guid.NewGuid().ToString("N"), "Matmon Agent");

    [Fact]
    public void The_config_folder_lets_users_read_but_not_write()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Path.Combine(_dir, "agent.json");
        AgentConfigFile.Write(path, "https://primary.example", "probe-1", "secret-token", "Office PC");

        var security = new DirectoryInfo(_dir).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);

        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var userRights = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(rule => rule.IdentityReference.Equals(users) && rule.AccessControlType == AccessControlType.Allow)
            .Aggregate((FileSystemRights)0, (all, rule) => all | rule.FileSystemRights);
        Assert.True(userRights.HasFlag(FileSystemRights.ReadAndExecute));
        Assert.False(userRights.HasFlag(FileSystemRights.CreateFiles));
        Assert.False(userRights.HasFlag(FileSystemRights.Write));

        Assert.Equal("Office PC", AgentConfigFile.ReadEnrolledProbeName(path));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_dir)!, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
