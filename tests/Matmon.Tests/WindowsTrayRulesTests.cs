using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Matmon.Agent;
using MatDesktop;

namespace Matmon.Tests;

/// <summary>
/// What the Windows trays and the setup owe the desktop rules of MatDesign (DESK-02, AGENT-03, AGENT-04, AGENT-05) that can be
/// held without a window: the colours come from the tokens, the copy of the tokens is MatDesign's, the status pipe carries no
/// secret, and the tray's service actions have a command.
/// </summary>
public sealed class WindowsTrayRulesTests
{
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Matmon.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Matmon.slnx not found above the test folder.");
    }

    [Theory]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("restart")]
    public void The_tray_service_actions_are_a_command(string action) =>
        Assert.Equal(action, ServiceCommand.ParseAction(["service", action]));

    [Theory]
    [InlineData("service")]
    [InlineData("service reboot")]
    [InlineData("service start now")]
    [InlineData("start")]
    public void Anything_else_is_not_a_service_command(string line) =>
        Assert.Null(ServiceCommand.ParseAction(line.Split(' ')));

    [Fact]
    public void The_status_pipe_carries_no_secret()
    {
        // AGENT-03: any local user may read the pipe, so the snapshot never carries the token (or anything like it).
        var names = typeof(AgentStatusSnapshot).GetProperties().Select(property => property.Name).ToArray();
        Assert.DoesNotContain(names, name => Regex.IsMatch(name, "token|secret|password|key", RegexOptions.IgnoreCase));
    }

    [Fact]
    public void The_copy_of_the_tokens_is_MatDesigns()
    {
        // A copy from MatDesign (tools/matdesktop-copy.js --tokens-only), never edited here: every file still has the hash the
        // copy wrote. A new version is copied again, not changed by hand.
        var folder = Path.Combine(RepoRoot(), "src", "Matmon.Agent", "MatDesktop");
        var manifest = File.ReadAllLines(Path.Combine(folder, "matdesktop.version")).Skip(1).Where(line => line.Length > 0).ToArray();
        Assert.Equal(3, manifest.Length);
        foreach (var line in manifest)
        {
            var hash = line[..64];
            var file = line[66..];
            var text = File.ReadAllText(Path.Combine(folder, file)).ReplaceLineEndings("\n");
            Assert.True(hash == Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))), $"{file} differs from the copy MatDesign wrote.");
        }
    }

    [Fact]
    public void The_accent_is_Matmons_and_reaches_contrast()
    {
        // Matmon's accent (MatDesign's AppRegistry) as the primary button: white on it is readable (AA for its 9.5 pt semibold).
        var palette = MatPalette.For("#295cff", dark: false);
        Assert.Equal("#295cff", palette.Accent.Hex);
        Assert.True(MatColor.Contrast(palette.OnAccent, palette.Accent) >= 4.5);
    }

    [Fact]
    public void The_trays_and_the_setup_paint_no_colour_of_their_own()
    {
        // DESK-02 / AGENT-05: a window takes its colours from the tokens (BrandChrome); the one exception is the gradient of the
        // logo (MarkPalette), which is the artwork of the mark, not a colour of the window.
        var root = RepoRoot();
        var files = Directory.GetFiles(Path.Combine(root, "src", "Matmon.Agent", "Tray"), "*.cs")
            .Concat(Directory.GetFiles(Path.Combine(root, "src", "Matmon.Setup"), "*.cs"));
        var literal = new Regex(@"Color\.FromArgb\(0x|Color\.(?!Transparent\b|Empty\b|FromArgb\b)[A-Z]\w*|SystemColors\.");
        var found = new List<string>();
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var logo = text.IndexOf("internal sealed record MarkPalette", StringComparison.Ordinal);
            if (logo >= 0)
            {
                text = text[..logo];
            }

            found.AddRange(literal.Matches(text).Select(match => $"{Path.GetFileName(file)}: {match.Value}"));
        }

        Assert.Empty(found);
    }
}
