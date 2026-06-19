using Matmon.Core.Domain;

namespace Matmon.Tests;

public sealed class MonitoringTagResolverTests
{
    [Fact]
    public void Normalize_trims_dedupes_case_insensitively_keeps_first_spelling()
    {
        var result = MonitoringTagResolver.Normalize([" Prod ", "prod", "Network", "", "  ", "network"]);

        Assert.Equal(["Prod", "Network"], result);
    }

    [Fact]
    public void Parse_splits_on_commas_newlines_and_semicolons()
    {
        var result = MonitoringTagResolver.Parse("prod, network\n berlin; prod");

        Assert.Equal(["prod", "network", "berlin"], result);
    }

    [Fact]
    public void ResolveEffective_unions_own_and_ancestor_tags()
    {
        var probe = new ProbeElement("Probe") { Tags = ["site-a"] };
        var folder = new FolderElement("Folder") { Tags = ["network"] };
        var sensor = new SensorElement("Ping", "ping", "1.1.1.1") { Tags = ["critical", "network"] };

        var effective = MonitoringTagResolver.ResolveEffective([probe, folder, sensor]);

        Assert.Equal(["site-a", "network", "critical"], effective);
    }
}
