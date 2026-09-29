using Matmon.Agent;

namespace Matmon.Tests;

// The updater and setup replace the agent's executable while the tray may still run it. On Windows a running
// executable can be renamed but not overwritten, so the swap sets the old file aside - and must never leave
// the service without an executable when something goes wrong halfway.
public sealed class AgentExecutableSwapTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("matmon-agent-swap-").FullName;

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void TheOldExecutableStepsAsideAndTheNewOneTakesItsName()
    {
        var target = Write("matmon-agent.exe", "old");
        var staged = Write("matmon-agent.new.exe", "new");

        ApplyUpdateCommand.Replace(staged, target, move: true);

        Assert.Equal("new", File.ReadAllText(target));
        Assert.False(File.Exists(staged));
        var setAside = Assert.Single(Directory.GetFiles(_dir, "matmon-agent.exe.old-*"));
        Assert.Equal("old", File.ReadAllText(setAside));

        ApplyUpdateCommand.DeleteSetAside(target);
        Assert.Empty(Directory.GetFiles(_dir, "matmon-agent.exe.old-*"));
    }

    [Fact]
    public void CopyingKeepsTheSource()
    {
        var target = Write("matmon-agent.exe", "new");
        var backup = Write("matmon-agent.exe.bak", "old");

        ApplyUpdateCommand.Replace(backup, target, move: false);

        Assert.Equal("old", File.ReadAllText(target));
        Assert.True(File.Exists(backup));
    }

    [Fact]
    public void AFailedSwapPutsTheOldExecutableBack()
    {
        var target = Write("matmon-agent.exe", "old");

        Assert.ThrowsAny<IOException>(() => ApplyUpdateCommand.Replace(Path.Combine(_dir, "missing.exe"), target, move: true));

        Assert.Equal("old", File.ReadAllText(target));
        Assert.Empty(Directory.GetFiles(_dir, "matmon-agent.exe.old-*"));
    }

    [Fact]
    public void AnInstallWithoutAPreviousExecutableJustPlacesTheNewOne()
    {
        var staged = Write("download.exe", "new");
        var target = Path.Combine(_dir, "Matmon Agent", "matmon-agent.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        ApplyUpdateCommand.Replace(staged, target, move: false);

        Assert.Equal("new", File.ReadAllText(target));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
