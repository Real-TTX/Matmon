using Matmon.Host.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

/// <summary>"From a MIB" beside the walk's Root OID: each loaded MIB offers its branch and the groups its objects hang under.</summary>
public sealed class MibWalkRootTests
{
    private static IReadOnlyList<Matmon.Core.Domain.MibWalkRoot> ShippedRoots()
    {
        var dir = Path.Combine(Path.GetTempPath(), "matmon-mibroots-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var library = new MibLibrary(dir, Path.Combine(AppContext.BaseDirectory, "mibs"), NullLogger<MibLibrary>.Instance);
            return library.Registry.WalkRoots;
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void IF_MIB_offers_its_groups_but_never_all_of_mib_2()
    {
        var roots = ShippedRoots();
        Assert.Contains(roots, root => root.Module == "IF-MIB" && root.Group == "interfaces" && root.Oid == "1.3.6.1.2.1.2");
        Assert.Contains(roots, root => root.Module == "IF-MIB" && root.Oid == "1.3.6.1.2.1.31.1");
        Assert.DoesNotContain(roots, root => root.Oid == "1.3.6.1.2.1");
        // a group is where scalars and tables hang - never a table's columns
        Assert.DoesNotContain(roots, root => root.Module == "IF-MIB" && root.Oid.StartsWith("1.3.6.1.2.1.2.2.1", StringComparison.Ordinal));
    }

    [Fact]
    public void A_module_without_objects_offers_nothing_and_no_root_is_shorter_than_four_arcs()
    {
        var roots = ShippedRoots();
        Assert.DoesNotContain(roots, root => root.Module == "SNMPv2-SMI");
        Assert.All(roots, root => Assert.True(root.Oid.Split('.').Length >= 4, root.Oid));
    }

    [Fact]
    public void The_UPS_MIB_points_at_its_own_branch()
    {
        var roots = ShippedRoots();
        var ups = Assert.Single(roots, root => root.Module == "UPS-MIB" && root.Group is null);
        Assert.StartsWith("1.3.6.1.2.1.33", ups.Oid);
    }
}
