using Matmon.Core.Domain;
using Xunit.Abstractions;

namespace Matmon.Tests;

/// <summary>The standard MIBs shipped in src/Matmon.Host/mibs must stand on their own: a switch vendor's MIBs import
/// LLDP-MIB, Q-BRIDGE-MIB, RMON-MIB and friends, and a shipped module whose own imports are missing would show up on
/// System > SNMP MIBs as "Missing dependencies" before the admin has uploaded anything.</summary>
public sealed class ShippedMibTests(ITestOutputHelper output)
{
    private static string MibDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Matmon.slnx")))
        {
            directory = directory.Parent;
        }
        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "src", "Matmon.Host", "mibs");
    }

    private static MibRegistry LoadShipped() =>
        new(Directory.GetFiles(MibDirectory(), "*.txt")
            .SelectMany(file => MibParser.Parse(File.ReadAllText(file)).Select(module => (module, Path.GetFileName(file)))));

    [Fact]
    public void Every_shipped_module_has_its_imports_shipped_too()
    {
        var missing = LoadShipped().Modules
            .Where(status => status.MissingImports.Count > 0)
            .Select(status => $"{status.Name}: {string.Join(", ", status.MissingImports)}")
            .ToList();
        missing.ForEach(output.WriteLine);
        Assert.Empty(missing);
    }

    [Theory]
    [InlineData("LLDP-MIB")]
    [InlineData("LLDP-V2-TC-MIB")]
    [InlineData("IEEE8021-PAE-MIB")]
    [InlineData("P-BRIDGE-MIB")]
    [InlineData("Q-BRIDGE-MIB")]
    [InlineData("RMON-MIB")]
    [InlineData("RMON2-MIB")]
    [InlineData("SNMP-TARGET-MIB")]
    [InlineData("ENTITY-MIB")]
    [InlineData("POWER-ETHERNET-MIB")]
    public void The_modules_switch_mibs_import_are_shipped_and_read_cleanly(string name)
    {
        var status = LoadShipped().Modules.FirstOrDefault(module => module.Name == name);
        Assert.NotNull(status);
        Assert.True(status!.NodeCount > 0, $"{name} has no nodes");
        Assert.Empty(status.Errors);
    }

    [Theory]
    [InlineData("1.0.8802.1.1.2.1.4.1.1.9", "lldpRemSysName")]
    [InlineData("1.3.6.1.2.1.17.7.1.4.3.1.1", "dot1qVlanStaticName")]
    [InlineData("1.3.6.1.2.1.105.1.1.1.6", "pethPsePortDetectionStatus")]
    public void Their_objects_get_names(string oid, string name) =>
        Assert.Equal(name, LoadShipped().Translate(oid)?.Node.Name);
}
