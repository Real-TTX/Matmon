using Matmon.Core.Domain;
using Matmon.Core.Telemetry;
using Matmon.Host.Services;
using Matmon.Host.Ui;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

/// <summary>
/// The instance name an installation suggests when it links to Matmon.Cloud. Every fresh install's root probe is called
/// "Primary Probe", so suggesting that made every installation under one account collide with the previous one.
/// </summary>
public class CloudInstanceNameSuggestionTests
{
    [Theory]
    [InlineData("nas", "nas")]
    [InlineData("nas.fritz.box", "nas")]
    [InlineData("Matmon-Test.local", "Matmon-Test")]
    [InlineData("nas.", "nas")]                 // a fully-qualified name's trailing dot
    [InlineData("  nas  ", "nas")]
    public void AHostNameYieldsItsFirstLabel(string host, string expected) =>
        Assert.Equal(expected, CloudInstanceNameSuggestion.NameFromHost(host));

    [Theory]
    [InlineData("192.168.1.50")]
    [InlineData("10.0.0.7")]
    [InlineData("[::1]")]
    [InlineData("::1")]
    [InlineData("[fe80::1]")]
    [InlineData("localhost")]
    [InlineData("LOCALHOST")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void AnAddressLocalhostOrNothingSaysNothingAboutTheMachine(string? host) =>
        Assert.Null(CloudInstanceNameSuggestion.NameFromHost(host));

    [Fact]
    public void AnOverlongLabelIsNotAHostName() =>
        Assert.Null(CloudInstanceNameSuggestion.NameFromHost(new string('x', 64) + ".example"));

    [Fact]
    public void ANameTheAdminChoseForTheRootProbeWins() =>
        Assert.Equal("Berlin Office", CloudInstanceNameSuggestion.Suggest("nas.fritz.box", "Berlin Office", "ab12cd34ef56"));

    [Fact]
    public void WithTheDefaultProbeNameTheHostTheAdminUsedIsBetter() =>
        Assert.Equal("nas", CloudInstanceNameSuggestion.Suggest("nas.fritz.box", ProbeElement.DefaultRootName, "ab12cd34ef56"));

    [Fact]
    public void WithoutAUsableHostTheDefaultProbeNameIsKept() =>
        Assert.Equal("Primary Probe", CloudInstanceNameSuggestion.Suggest("192.168.1.50", ProbeElement.DefaultRootName, "ab12cd34ef56"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WithNothingElseTheMachineNameIsUsed(string? probeName) =>
        Assert.Equal("ab12cd34ef56", CloudInstanceNameSuggestion.Suggest("localhost", probeName, "ab12cd34ef56"));

    [Fact]
    public void TheDefaultNameIsWhatAFreshInstallationReallyCallsItsRootProbe()
    {
        // The suggestion recognises "the admin did not choose this name" by comparing with the constant - so a fresh
        // store must seed exactly that, or every fresh install would look as if its admin had picked "Primary Probe".
        var directory = Path.Combine(Path.GetTempPath(), "matmon-default-name-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var telemetry = new SqliteTelemetryRepository(Path.Combine(directory, "telemetry.db"));
            using var store = new InMemoryMonitoringWorkspaceStore(
                new NameTestHostEnvironment(directory),
                new MatmonRuntimeOptions { WorkspacePath = Path.Combine(directory, "workspace.json") },
                new MatmonAuthOptions(),
                new EphemeralDataProtectionProvider(),
                telemetry,
                NullLogger<InMemoryMonitoringWorkspaceStore>.Instance);

            var root = store.GetAllElements().OfType<ProbeElement>().Single(probe => probe.ParentId is null);

            Assert.Equal(ProbeElement.DefaultRootName, root.Name);
            Assert.Equal("Primary Probe", root.Name);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { /* a SQLite handle may linger on Windows */ }
        }
    }
}

file sealed class NameTestHostEnvironment : IHostEnvironment
{
    public NameTestHostEnvironment(string contentRoot)
    {
        ContentRootPath = contentRoot;
        ContentRootFileProvider = new NullFileProvider();
    }

    public string ApplicationName { get; set; } = "Matmon.Tests";
    public string EnvironmentName { get; set; } = "Development";
    public string ContentRootPath { get; set; }
    public IFileProvider ContentRootFileProvider { get; set; }
}
