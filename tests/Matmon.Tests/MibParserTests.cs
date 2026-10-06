using Matmon.Core.Domain;

namespace Matmon.Tests;

public class MibParserTests
{
    private static string ShippedMibDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Matmon.slnx")))
        {
            directory = directory.Parent;
        }
        return Path.Combine(directory!.FullName, "src", "Matmon.Host", "mibs");
    }

    private static MibRegistry Shipped(params string[] extra) => new(
        Directory.GetFiles(ShippedMibDirectory())
            .SelectMany(file => MibParser.Parse(File.ReadAllText(file)))
            .Concat(extra.SelectMany(MibParser.Parse))
            .Select(module => (module, "built-in")));

    [Fact]
    public void TheShippedSetIsCompleteAndClean()
    {
        var registry = Shipped();

        Assert.All(registry.Modules, status =>
        {
            Assert.True(status.MissingImports.Count == 0 && status.Errors.Count == 0 && status.UnresolvedCount == 0,
                $"{status.Name}: missing [{string.Join(", ", status.MissingImports)}] errors [{string.Join(" | ", status.Errors)}] unresolved {status.UnresolvedCount}");
            Assert.Empty(status.MissingImports);
            Assert.Empty(status.Errors);
            Assert.Equal(0, status.UnresolvedCount);
        });
        Assert.True(registry.NodeCount > 1000, $"{registry.NodeCount} nodes");
    }

    [Theory]
    [InlineData("1.3.6.1.2.1.1.3.0", "sysUpTime.0", "SNMPv2-MIB")]
    [InlineData(".1.3.6.1.2.1.2.2.1.10.3", "ifInOctets.3", "IF-MIB")]
    [InlineData("1.3.6.1.2.1.31.1.1.1.6.12", "ifHCInOctets.12", "IF-MIB")]
    [InlineData("1.3.6.1.2.1.25.2.3.1.6.31", "hrStorageUsed.31", "HOST-RESOURCES-MIB")]
    [InlineData("1.3.6.1.4.1.2021.10.1.3.1", "laLoad.1", "UCD-SNMP-MIB")]
    public void WalkedOidsGetTheirNameAndInstance(string oid, string name, string module)
    {
        var translation = Shipped().Translate(oid);

        Assert.NotNull(translation);
        Assert.Equal(name, translation!.Name);
        Assert.Equal(module, translation.Node.Module);
    }

    [Fact]
    public void EnumsResolveDirectlyAndThroughTextualConventions()
    {
        var registry = Shipped();

        // Inline enum on the OBJECT-TYPE.
        Assert.Equal("up (1)", registry.Translate("1.3.6.1.2.1.2.2.1.8.3")!.FormatValue("1"));
        // ifType -> IANAifType (a TEXTUAL-CONVENTION in another module).
        Assert.Equal("ethernetCsmacd (6)", registry.Translate("1.3.6.1.2.1.2.2.1.3.3")!.FormatValue("6"));
        // A value the enum does not name, and a non-number, pass through untouched.
        Assert.Equal("99", registry.Translate("1.3.6.1.2.1.2.2.1.8.3")!.FormatValue("99"));
        Assert.Equal("eth0", registry.Translate("1.3.6.1.2.1.2.2.1.2.3")!.FormatValue("eth0"));
    }

    [Fact]
    public void UnitsAndDescriptionsAreRead()
    {
        var node = Shipped().Find("ifSpeed")!;

        Assert.Equal("1.3.6.1.2.1.2.2.1.5", node.Oid);
        Assert.Contains("bits per second", node.Description);
        Assert.Equal("read-only", node.Access);
        Assert.Equal("ports", Shipped().Find("dot1dBaseNumPorts")!.Units);
    }

    [Fact]
    public void AnUnknownEnterpriseFallsBackToTheNearestKnownNode()
    {
        var translation = Shipped().Translate("1.3.6.1.4.1.6574.1.2.0");

        Assert.Equal("enterprises.6574.1.2.0", translation!.Name);
    }

    [Fact]
    public void AVendorMibWithMissingImportsStillResolvesAndReportsWhatIsMissing()
    {
        // Sloppy on purpose: TruthValue is used without importing SNMPv2-TC, and an import names a module that
        // is not loaded.
        const string vendor = """
            ACME-MIB DEFINITIONS ::= BEGIN
            IMPORTS
                MODULE-IDENTITY, OBJECT-TYPE, Integer32, enterprises FROM SNMPv2-SMI
                AcmeThing FROM ACME-TC-MIB;

            acme MODULE-IDENTITY
                LAST-UPDATED "202401010000Z"
                ORGANIZATION "Acme"
                CONTACT-INFO "none"
                DESCRIPTION "Acme devices."
                REVISION "202401010000Z"
                DESCRIPTION "First revision -- not the module description."
                ::= { enterprises 99999 }

            acmeSystem OBJECT IDENTIFIER ::= { acme 1 }

            acmeTemperature OBJECT-TYPE
                SYNTAX      Integer32 (-40..125)
                UNITS       "degrees Celsius"
                MAX-ACCESS  read-only
                STATUS      current
                DESCRIPTION "Board temperature."
                ::= { acmeSystem 1 }

            acmeFanOk OBJECT-TYPE
                SYNTAX      TruthValue
                MAX-ACCESS  read-only
                STATUS      current
                DESCRIPTION "Is the fan spinning?"
                ::= { acmeSystem 2 }

            acmeStatus OBJECT-TYPE
                SYNTAX      INTEGER { normal(1), degraded(2), failed(3) }
                MAX-ACCESS  read-only
                STATUS      current
                DESCRIPTION "Overall state."
                ::= { acmeSystem 3 }
            END
            """;
        var registry = Shipped(vendor);

        var temperature = registry.Translate("1.3.6.1.4.1.99999.1.1.0")!;
        Assert.Equal("acmeTemperature.0", temperature.Name);
        Assert.Equal("degrees Celsius", temperature.Node.Units);
        Assert.Equal("true (1)", registry.Translate("1.3.6.1.4.1.99999.1.2.0")!.FormatValue("1"));
        Assert.Equal("degraded (2)", registry.Translate("1.3.6.1.4.1.99999.1.3.0")!.FormatValue("2"));
        Assert.Equal("Acme devices.", registry.Find("acme")!.Description);

        var status = registry.Modules.Single(module => module.Name == "ACME-MIB");
        Assert.Equal(["ACME-TC-MIB"], status.MissingImports);
        Assert.Equal("202401010000Z", status.LastUpdated);
    }

    [Fact]
    public void ABrokenAssignmentIsReportedAndTheRestStillLoads()
    {
        const string broken = """
            BROKEN-MIB DEFINITIONS ::= BEGIN
            IMPORTS OBJECT-TYPE, enterprises FROM SNMPv2-SMI;
            broken OBJECT IDENTIFIER ::= { enterprises 88888 }
            garbled OBJECT-TYPE
                SYNTAX ::= { broken 1 }
            fine OBJECT-TYPE
                SYNTAX INTEGER
                MAX-ACCESS read-only
                STATUS current
                DESCRIPTION "Still here."
                ::= { broken 2 }
            END
            """;
        var registry = Shipped(broken);

        Assert.Equal("fine.0", registry.Translate("1.3.6.1.4.1.88888.2.0")!.Name);
        Assert.NotEmpty(registry.Modules.Single(module => module.Name == "BROKEN-MIB").Errors);
    }

    [Fact]
    public void SmiV1TrapTypesAndCommentsDoNotConfuseTheParser()
    {
        const string v1 = """
            OLD-MIB DEFINITIONS ::= BEGIN
            IMPORTS enterprises FROM RFC1155-SMI
                    OBJECT-TYPE FROM RFC-1212
                    TRAP-TYPE FROM RFC-1215;
            old OBJECT IDENTIFIER ::= { enterprises 77777 } -- a comment -- oldIgnored OBJECT IDENTIFIER ::= { old 9 }
            oldValue OBJECT-TYPE
                SYNTAX INTEGER (0..100)
                ACCESS read-only
                STATUS mandatory
                DESCRIPTION "A ""quoted"" word."
                ::= { old 1 }
            oldTrap TRAP-TYPE
                ENTERPRISE old
                VARIABLES { oldValue }
                ::= 1
            END
            """;
        var registry = Shipped(v1);

        Assert.Equal("oldValue.0", registry.Translate("1.3.6.1.4.1.77777.1.0")!.Name);
        Assert.Contains("\"quoted\"", registry.Find("oldValue")!.Description);
        Assert.Null(registry.Find("oldTrap"));
        Assert.Empty(registry.Modules.Single(module => module.Name == "OLD-MIB").MissingImports);
    }
}
