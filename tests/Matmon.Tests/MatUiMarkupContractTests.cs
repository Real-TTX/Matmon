using System.Text.RegularExpressions;
using Xunit;

namespace Matmon.Tests;

/// <summary>
/// Holds every page of the instance to the MatUi rules a test can see in the markup (docs/ui-conventions.md): none of the
/// old hand-written chrome classes, no inline script or event attribute and no inline style except CSS custom properties
/// for geometry that is data (CODE-02 and the documented exception), a label on every table cell (TABLE-02), no browser
/// dialogs (DIALOG-01), and no mat- class or icon the toolkit does not ship - a typo there is a control that silently
/// looks wrong.
/// </summary>
public sealed partial class MatUiMarkupContractTests
{
    private static readonly string HostRoot = FindHostRoot();

    // The hand-written chrome of the old look. A feature's own components keep feature names (tree-, map-, sensor-chart-,
    // discovery-, ...) - those are not chrome and are not listed.
    private static readonly string[] LegacyClasses =
    [
        "panel", "panel-header", "panel-hint", "section-title", "page-header", "page-title", "state-pill", "form-control",
        "form-select", "btn", "ui-icon", "container-fluid", "matmon-shell", "monitoring-filter-bar", "monitoring-filter-field",
        "monitoring-controls", "breadcrumb-bar", "breadcrumb-list"
    ];

    private static readonly string[] LegacyPrefixes = ["workspace-", "metric-", "sensor-tab", "sidebar-", "topnav", "account-menu", "anonymous-", "btn-"];

    [Fact]
    public void Pages_use_no_inline_script_event_attributes_or_styles_other_than_geometry()
    {
        var problems = new List<string>();
        foreach (var (file, text) in Views())
        {
            foreach (Match style in StyleAttribute().Matches(text))
            {
                var declarations = RazorExpression().Replace(style.Groups[1].Value, "0").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (declarations.Any(d => !d.StartsWith("--", StringComparison.Ordinal)))
                {
                    problems.Add($"{file}: style=\"{style.Groups[1].Value}\"");
                }
            }

            if (EventAttribute().Match(text) is { Success: true } ev) { problems.Add($"{file}: {ev.Value}…"); }
            foreach (Match script in ScriptTag().Matches(text))
            {
                var tag = script.Value;
                if (!tag.Contains("src=", StringComparison.Ordinal) && !tag.Contains("application/json", StringComparison.Ordinal))
                {
                    problems.Add($"{file}: inline <script>");
                }
            }
        }

        Assert.True(problems.Count == 0, "CODE-02:\n" + string.Join("\n", problems));
    }

    [Fact]
    public void Pages_carry_none_of_the_old_chrome_classes()
    {
        var problems = new List<string>();
        foreach (var (file, text) in Views())
        {
            foreach (var token in ClassTokens(text))
            {
                if (LegacyClasses.Contains(token) || LegacyPrefixes.Any(prefix => token.StartsWith(prefix, StringComparison.Ordinal)))
                {
                    problems.Add($"{file}: class \"{token}\"");
                }
            }
        }

        Assert.True(problems.Count == 0, "Old classes left:\n" + string.Join("\n", problems.Distinct()));
    }

    [Fact]
    public void Every_toolkit_class_and_icon_exists()
    {
        var css = File.ReadAllText(Path.Combine(HostRoot, "wwwroot", "matui", "matui.css"));
        var known = MatClassInCss().Matches(css).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var sprite = File.ReadAllText(Path.Combine(HostRoot, "wwwroot", "matui", "matui-icons.svg"));
        var icons = SpriteSymbol().Matches(sprite).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

        var problems = new List<string>();
        foreach (var (file, text) in Views())
        {
            foreach (var token in ClassTokens(text).Where(t => t.StartsWith("mat-", StringComparison.Ordinal)))
            {
                if (!known.Contains(token)) { problems.Add($"{file}: unknown class \"{token}\""); }
            }

            foreach (Match icon in IconCall().Matches(text))
            {
                if (!icons.Contains(icon.Groups[1].Value)) { problems.Add($"{file}: unknown icon \"{icon.Groups[1].Value}\""); }
            }
        }

        Assert.True(problems.Count == 0, "Not in MatUi:\n" + string.Join("\n", problems.Distinct()));
    }

    [Fact]
    public void Every_table_cell_has_a_label()
    {
        // TABLE-02 is about a mat-table, which turns into cards on a phone. A table of the app's own (mm-) is laid out by
        // its feature; any other table is unconverted.
        var problems = new List<string>();
        foreach (var (file, text) in Views())
        {
            foreach (Match table in TableTag().Matches(text))
            {
                if (!table.Value.Contains("mat-table", StringComparison.Ordinal) && !table.Value.Contains("\"mm-", StringComparison.Ordinal))
                {
                    problems.Add($"{file}: {table.Value} (neither mat-table nor an mm- table of the app's own)");
                }
            }

            foreach (Match cell in TableCell().Matches(OwnTable().Replace(text, string.Empty)))
            {
                var tag = cell.Value;
                if (!tag.Contains("data-label", StringComparison.Ordinal) && !tag.Contains("mat-table__cell-actions", StringComparison.Ordinal)
                    && !tag.Contains("colspan", StringComparison.Ordinal) && !tag.Contains("mat-group__cell", StringComparison.Ordinal))
                {
                    problems.Add($"{file}: {tag}");
                }
            }
        }

        Assert.True(problems.Count == 0, "TABLE-02 (a <td> without data-label):\n" + string.Join("\n", problems));
    }

    [Fact]
    public void No_text_runs_into_an_icon_call()
    {
        // Razor reads "server@MatUi.Icon(...)" as an e-mail address and prints it as text: the icon never appears.
        var problems = Views().Where(v => TextIntoIcon().IsMatch(v.Text)).Select(v => v.File).ToList();
        Assert.True(problems.Count == 0, "Text directly before @MatUi (put it in a <span> or add a space):\n" + string.Join("\n", problems));
    }

    [Fact]
    public void Data_attributes_do_not_rely_on_a_null_value_to_disappear()
    {
        // Razor drops an ordinary attribute whose value is null, but writes a data-* attribute as data-x="" - so
        // data-x="@(cond ? "" : null)" is ALWAYS present. Code that tests for presence then always fires (the public
        // wallboard rotated its slides whatever the map said). Give such an attribute a value ("true"/"false") or a class.
        var problems = Views().SelectMany(v => NullableDataAttribute().Matches(v.Text).Select(m => $"{v.File}: {m.Value}")).ToList();
        Assert.True(problems.Count == 0, "data-* attribute with a null branch:\n" + string.Join("\n", problems));
    }

    [Fact]
    public void Scripts_use_no_browser_dialogs()
    {
        var problems = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(HostRoot, "wwwroot", "js"), "*.js", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            if (BrowserDialog().IsMatch(text)) { problems.Add(Path.GetRelativePath(HostRoot, file)); }
        }

        Assert.True(problems.Count == 0, "DIALOG-01 (window.confirm/alert/prompt):\n" + string.Join("\n", problems));
    }

    private static IEnumerable<(string File, string Text)> Views()
        => Directory.EnumerateFiles(Path.Combine(HostRoot, "Pages"), "*.cshtml", SearchOption.AllDirectories)
            .Select(path => (Path.GetRelativePath(HostRoot, path), RazorComment().Replace(File.ReadAllText(path), string.Empty)));

    // Class tokens of literal class attributes. Razor expressions inside the value are dropped; the literal parts are checked.
    private static IEnumerable<string> ClassTokens(string text)
        => ClassAttribute().Matches(text)
            .SelectMany(m => RazorExpression().Replace(m.Groups[1].Value, " ").Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(token => Regex.IsMatch(token, "^[a-z][a-z0-9_-]*$"));

    // From this source file first (a build with --artifacts-path runs far from the repository), then from the binaries.
    private static string FindHostRoot()
    {
        foreach (var start in new[] { Path.GetDirectoryName(SourceFile()), AppContext.BaseDirectory })
        {
            for (var dir = string.IsNullOrEmpty(start) ? null : new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "src", "Matmon.Host");
                if (Directory.Exists(Path.Combine(candidate, "Pages"))) { return candidate; }
            }
        }

        throw new DirectoryNotFoundException("src/Matmon.Host not found above " + SourceFile());
    }

    private static string SourceFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

    [GeneratedRegex(@"\sstyle\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase)] private static partial Regex StyleAttribute();
    [GeneratedRegex(@"\son(click|input|submit|change|keydown|keyup|load|focus|blur)\s*=", RegexOptions.IgnoreCase)] private static partial Regex EventAttribute();
    [GeneratedRegex(@"<script\b[^>]*>", RegexOptions.IgnoreCase)] private static partial Regex ScriptTag();
    [GeneratedRegex(@"\sclass\s*=\s*""([^""]*)""")] private static partial Regex ClassAttribute();
    [GeneratedRegex(@"@\([^)]*\)|@[A-Za-z_][\w.]*(\([^)]*\))?")] private static partial Regex RazorExpression();
    [GeneratedRegex(@"\.(mat-[a-z0-9_-]+)")] private static partial Regex MatClassInCss();
    [GeneratedRegex(@"id=""mat-i-([a-z0-9-]+)""")] private static partial Regex SpriteSymbol();
    [GeneratedRegex(@"MatUi\.Icon\(""([a-z0-9-]+)""")] private static partial Regex IconCall();
    [GeneratedRegex(@"<td\b[^>]*>")] private static partial Regex TableCell();
    [GeneratedRegex(@"<table\b[^>]*>")] private static partial Regex TableTag();
    [GeneratedRegex(@"data-[a-z-]+=""@\((?:[^()""]|""[^""]*""|\([^()]*\))*\bnull\b(?:[^()""]|""[^""]*""|\([^()]*\))*\)""")] private static partial Regex NullableDataAttribute();
    [GeneratedRegex(@"<table\b[^>]*class=""mm-[^>]*>.*?</table>", RegexOptions.Singleline)] private static partial Regex OwnTable();
    [GeneratedRegex(@"window\.(confirm|alert|prompt)\s*\(|(?<![\w.])(confirm|alert|prompt)\s*\(")] private static partial Regex BrowserDialog();
    [GeneratedRegex(@"@\*.*?\*@", RegexOptions.Singleline)] private static partial Regex RazorComment();
    [GeneratedRegex(@"[A-Za-z0-9]@(MatUi|MatmonIcons)\.")] private static partial Regex TextIntoIcon();
}
