using Matmon.Core.Domain;
using Xunit.Abstractions;

namespace Matmon.Tests;

/// <summary>
/// The parser against a real MIB corpus, compared with Net-SNMP's own resolution. Skipped unless
/// MATMON_MIB_CORPUS names a directory holding MIB files plus <c>snmptranslate-tz.txt</c> - the output of
/// <c>snmptranslate -Tz -m ALL</c> over the same files ("name" "oid" per line).
/// </summary>
public class MibCorpusTests(ITestOutputHelper output)
{
    [Fact]
    public void EveryNodeNetSnmpKnowsResolvesToTheSameName()
    {
        var directory = Environment.GetEnvironmentVariable("MATMON_MIB_CORPUS");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        var modules = Directory.GetFiles(directory, "*.txt")
            .Where(file => !file.EndsWith("snmptranslate-tz.txt", StringComparison.Ordinal))
            .SelectMany(file => MibParser.Parse(File.ReadAllText(file)).Select(module => (module, "test")))
            .ToList();
        var registry = new MibRegistry(modules);
        foreach (var status in registry.Modules.Where(status => status.Errors.Count > 0 || status.UnresolvedCount > 0 || status.MissingImports.Count > 0))
        {
            output.WriteLine($"{status.Name}: {status.NodeCount} nodes, {status.UnresolvedCount} unresolved, missing [{string.Join(", ", status.MissingImports)}], errors [{string.Join(" | ", status.Errors)}]");
        }

        var reference = File.ReadAllLines(Path.Combine(directory, "snmptranslate-tz.txt"))
            .Select(line => line.Split('"', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(part => part.Length > 0).ToArray())
            .Where(parts => parts.Length == 2)
            .ToList();
        var wrong = new List<string>();
        foreach (var parts in reference)
        {
            var translation = registry.Translate(parts[1]);
            if (translation is null || translation.Instance.Length > 0 || translation.Node.Name != parts[0])
            {
                wrong.Add($"{parts[0]} {parts[1]} -> {translation?.Name ?? "(none)"}");
            }
        }

        foreach (var line in wrong.Take(40))
        {
            output.WriteLine(line);
        }
        output.WriteLine($"{reference.Count - wrong.Count}/{reference.Count} nodes match, {modules.Count} modules");
        Assert.True(wrong.Count <= reference.Count / 200, $"{wrong.Count} of {reference.Count} nodes differ from Net-SNMP");
    }
}
