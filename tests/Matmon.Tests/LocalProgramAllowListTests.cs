using Matmon.Core.Domain;

namespace Matmon.Tests;

public sealed class LocalProgramAllowListTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "matmon-allow-tests");

    [Fact]
    public void Empty_allow_list_blocks_everything()
    {
        Assert.False(LocalProgramSensorExecutor.IsAllowed(Path.Combine(Root, "tool"), []));
    }

    [Fact]
    public void Exact_file_path_is_allowed()
    {
        var tool = Path.Combine(Root, "bin", "tool");
        Assert.True(LocalProgramSensorExecutor.IsAllowed(tool, [tool]));
    }

    [Fact]
    public void Program_under_an_allowed_directory_is_allowed()
    {
        var dir = Path.Combine(Root, "bin");
        Assert.True(LocalProgramSensorExecutor.IsAllowed(Path.Combine(dir, "sub", "check"), [dir]));
    }

    [Fact]
    public void Path_outside_the_allowed_directory_is_blocked()
    {
        var dir = Path.Combine(Root, "bin");
        Assert.False(LocalProgramSensorExecutor.IsAllowed(Path.Combine(Root, "other", "tool"), [dir]));
    }

    [Fact]
    public void Directory_traversal_that_escapes_the_allowed_directory_is_blocked()
    {
        var dir = Path.Combine(Root, "bin");
        var escaping = Path.Combine(dir, "..", "secret", "tool");
        Assert.False(LocalProgramSensorExecutor.IsAllowed(escaping, [dir]));
    }
}
