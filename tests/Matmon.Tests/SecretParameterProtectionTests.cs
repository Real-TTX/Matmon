using System.Text;
using Matmon.Core.Domain;
using Matmon.Core.Telemetry;
using Matmon.Host.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

// A password typed straight into a sensor's own parameters (not a credential bundle) used to be written to
// workspace.json in the clear - one was found that way in a real workspace. These pin that it is sealed on
// disk, comes back after a restart, and still travels through a portable backup.
public sealed class SecretParameterProtectionTests : IDisposable
{
    private const string Password = "Wx-9!inline-winrm-password";
    private readonly string _dir = Directory.CreateTempSubdirectory("matmon-secret-params-").FullName;

    private InMemoryMonitoringWorkspaceStore NewStore(string tag, IDataProtectionProvider protection, SqliteTelemetryRepository telemetry) =>
        new(new SecretParamsHostEnvironment(_dir),
            new MatmonRuntimeOptions { WorkspacePath = Path.Combine(_dir, $"workspace-{tag}.json") },
            new MatmonAuthOptions(),
            protection,
            telemetry,
            NullLogger<InMemoryMonitoringWorkspaceStore>.Instance);

    private static Guid SeedSensor(InMemoryMonitoringWorkspaceStore store)
    {
        var rootId = store.GetAllElements().OfType<ProbeElement>().First().Id;
        var sensor = store.CreateSensor(rootId, "Windows box", "powershell", "pc-01", null);
        store.UpdateElement(sensor.Id, element =>
        {
            element.Settings.Parameters["winrm.password"] = Password;   // declared Secret by the sensor type
            element.Settings.Parameters["custom.apiToken"] = "tok-123"; // undeclared, secret by name
            element.Settings.Parameters["winrm.port"] = "5985";         // not a secret
        });
        return sensor.Id;
    }

    [Fact]
    public void SecretParametersAreSealedOnDiskAndComeBackAfterARestart()
    {
        var protection = new EphemeralDataProtectionProvider();
        Guid sensorId;
        using (var telemetry = new SqliteTelemetryRepository(Path.Combine(_dir, "t1.db")))
        {
            using var store = NewStore("a", protection, telemetry);
            sensorId = SeedSensor(store);

            // In memory the executors, the probe assignment path and the editors keep reading plaintext.
            Assert.Equal(Password, store.FindElement(sensorId)!.Settings.Parameters["winrm.password"]);
        }

        var json = File.ReadAllText(Path.Combine(_dir, "workspace-a.json"));
        Assert.DoesNotContain(Password, json);
        Assert.DoesNotContain("tok-123", json);
        Assert.Contains(InMemoryMonitoringWorkspaceStore.ProtectedParameterPrefix, json);
        Assert.Contains("5985", json);

        using var telemetry2 = new SqliteTelemetryRepository(Path.Combine(_dir, "t2.db"));
        using var reopened = NewStore("a", protection, telemetry2);
        var parameters = reopened.FindElement(sensorId)!.Settings.Parameters;
        Assert.Equal(Password, parameters["winrm.password"]);
        Assert.Equal("tok-123", parameters["custom.apiToken"]);
        Assert.Equal("5985", parameters["winrm.port"]);
    }

    [Fact]
    public void AnUndecryptableValueKeepsItsCiphertextInsteadOfBeingLost()
    {
        using (var telemetry = new SqliteTelemetryRepository(Path.Combine(_dir, "t1.db")))
        {
            using var store = NewStore("a", new EphemeralDataProtectionProvider(), telemetry);
            SeedSensor(store);
        }

        var sealedBefore = File.ReadAllText(Path.Combine(_dir, "workspace-a.json"));

        // A different key ring (e.g. the DataProtection folder was lost): the value cannot be read - but it must
        // not be re-saved as an encryption of garbage either.
        using var telemetry2 = new SqliteTelemetryRepository(Path.Combine(_dir, "t2.db"));
        using (var foreign = NewStore("a", new EphemeralDataProtectionProvider(), telemetry2))
        {
            var value = foreign.GetAllElements().OfType<SensorElement>().Single(s => s.Name == "Windows box")
                .Settings.Parameters["winrm.password"];
            Assert.StartsWith(InMemoryMonitoringWorkspaceStore.ProtectedParameterPrefix, value);
            foreign.Save();
        }

        var cipherBefore = ExtractCipher(sealedBefore);
        Assert.Equal(cipherBefore, ExtractCipher(File.ReadAllText(Path.Combine(_dir, "workspace-a.json"))));
    }

    [Fact]
    public void APortableBackupCarriesThemToADifferentInstance()
    {
        byte[] blob;
        using (var telemetryA = new SqliteTelemetryRepository(Path.Combine(_dir, "ta.db")))
        using (var storeA = NewStore("a", new EphemeralDataProtectionProvider(), telemetryA))
        {
            SeedSensor(storeA);
            blob = storeA.CreateBackupBytes(WorkspaceBackupSection.Topology, "test", "correct horse battery staple");
        }

        Assert.DoesNotContain(Password, Encoding.UTF8.GetString(blob));

        using var telemetryB = new SqliteTelemetryRepository(Path.Combine(_dir, "tb.db"));
        using var storeB = NewStore("b", new EphemeralDataProtectionProvider(), telemetryB);
        storeB.RestoreBackupBytes(blob, WorkspaceBackupSection.Topology, "correct horse battery staple");

        var restored = storeB.GetAllElements().OfType<SensorElement>().Single(s => s.Name == "Windows box");
        Assert.Equal(Password, restored.Settings.Parameters["winrm.password"]);
    }

    [Theory]
    [InlineData("winrm.password", true)]
    [InlineData("mail.smtpPassword", true)]
    [InlineData("unifi.apiKey", true)]
    [InlineData("generic.token", true)]
    [InlineData("backup.passphrase", true)]
    [InlineData("webhook.secret", true)]
    [InlineData("winrm.port", false)]
    [InlineData("script", false)]
    [InlineData("snmp.community", false)]
    public void SecretByName(string key, bool expected)
    {
        Assert.Equal(expected, InMemoryMonitoringWorkspaceStore.IsSecretParameter(key, new HashSet<string>()));
    }

    [Fact]
    public void ADeclaredSecretIsSecretWhateverItsName()
    {
        Assert.True(InMemoryMonitoringWorkspaceStore.IsSecretParameter("snmp.community", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SNMP.community" }));
    }

    private static string ExtractCipher(string json)
    {
        var start = json.IndexOf(InMemoryMonitoringWorkspaceStore.ProtectedParameterPrefix, StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = json.IndexOf('"', start);
        return json[start..end];
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

file sealed class SecretParamsHostEnvironment(string contentRoot) : IHostEnvironment
{
    public string ApplicationName { get; set; } = "Matmon.Tests";
    public string EnvironmentName { get; set; } = "Development";
    public string ContentRootPath { get; set; } = contentRoot;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
