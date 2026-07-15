using Matmon.Core.Domain;
using Matmon.Core.Telemetry;
using Matmon.Host.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

/// <summary>Portable (passphrase-sealed) cloud backups: a config backup made with a passphrase must restore its
/// credentials onto a DIFFERENT instance (a different DataProtection key ring), which is the DR gap the plain
/// instance-bound backup can't cover. Each <see cref="Run"/> spins up a store with its own ephemeral DP provider,
/// so two runs stand in for two independent instances.</summary>
public sealed class PortableBackupTests : IDisposable
{
    private const string Passphrase = "correct horse battery staple";
    private const string Secret = "s3cret-across-instances";

    private readonly string _dir;

    public PortableBackupTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "matmon-portable-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    // A fresh store == a distinct instance: its own workspace file, telemetry db AND DataProtection key ring.
    private T Run<T>(string tag, Func<InMemoryMonitoringWorkspaceStore, T> body)
    {
        using var telemetry = new SqliteTelemetryRepository(Path.Combine(_dir, $"telemetry-{tag}.db"));
        using var store = new InMemoryMonitoringWorkspaceStore(
            new PortableTestHostEnvironment(_dir),
            new MatmonRuntimeOptions { WorkspacePath = Path.Combine(_dir, $"workspace-{tag}.json") },
            new MatmonAuthOptions(),
            new EphemeralDataProtectionProvider(),
            telemetry,
            NullLogger<InMemoryMonitoringWorkspaceStore>.Instance);
        return body(store);
    }

    private static void SeedCredential(InMemoryMonitoringWorkspaceStore store)
    {
        var rootId = store.GetAllElements().OfType<ProbeElement>().First().Id;
        store.UpdateElement(rootId, element => element.Settings.Credentials.Add(new MonitoringCredentialBundle
        {
            Name = "SSH box",
            Kind = MonitoringCredentialKind.Generic,
            Values = { ["generic.password"] = Secret },
        }));
    }

    private static MonitoringCredentialBundle? RestoredBundle(InMemoryMonitoringWorkspaceStore store) =>
        store.GetAllElements().OfType<ProbeElement>().First()
            .Settings.Credentials.FirstOrDefault(c => c.Name == "SSH box");

    [Fact]
    public void Portable_backup_restores_credentials_on_a_different_instance()
    {
        var blob = Run("a", storeA =>
        {
            SeedCredential(storeA);
            return storeA.CreateBackupBytes(WorkspaceBackupSection.Topology, "test", Passphrase);
        });

        var recovered = Run("b", storeB =>
        {
            storeB.RestoreBackupBytes(blob, WorkspaceBackupSection.Topology, Passphrase);
            return RestoredBundle(storeB)?.Values.GetValueOrDefault("generic.password");
        });

        Assert.Equal(Secret, recovered);
    }

    [Fact]
    public void Wrong_passphrase_is_rejected()
    {
        var blob = Run("a", storeA =>
        {
            SeedCredential(storeA);
            return storeA.CreateBackupBytes(WorkspaceBackupSection.Topology, "test", Passphrase);
        });

        Run("b", storeB =>
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => storeB.RestoreBackupBytes(blob, WorkspaceBackupSection.Topology, "wrong passphrase"));
            Assert.Contains("passphrase", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Null(RestoredBundle(storeB)); // rejected before anything was applied
            return true;
        });
    }

    [Fact]
    public void Corrupt_portable_metadata_is_rejected_before_deriving_the_key()
    {
        var blob = Run("a", storeA =>
        {
            SeedCredential(storeA);
            return storeA.CreateBackupBytes(WorkspaceBackupSection.Topology, "test", Passphrase);
        });

        // Tamper the (untrusted) iteration count to an absurd value - a naive restore would spin PBKDF2 for
        // minutes; the guard must reject it fast with a clean "corrupt" message instead.
        var node = System.Text.Json.Nodes.JsonNode.Parse(blob)!.AsObject();
        var key = node.Select(kv => kv.Key).First(k => string.Equals(k, "SecretsIterations", StringComparison.OrdinalIgnoreCase));
        node[key] = int.MaxValue;
        var tampered = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(node);

        Run("b", storeB =>
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => storeB.RestoreBackupBytes(tampered, WorkspaceBackupSection.Topology, Passphrase));
            Assert.Contains("corrupt", ex.Message, StringComparison.OrdinalIgnoreCase);
            return true;
        });
    }

    [Fact]
    public void Non_portable_backup_cannot_recover_credentials_on_another_instance()
    {
        var blob = Run("a", storeA =>
        {
            SeedCredential(storeA);
            return storeA.CreateBackupBytes(WorkspaceBackupSection.Topology, "test"); // no passphrase = instance-bound
        });

        var value = Run("b", storeB =>
        {
            // Config still restores, but the instance-bound secret can't be decrypted with B's key ring.
            storeB.RestoreBackupBytes(blob, WorkspaceBackupSection.Topology);
            var bundle = RestoredBundle(storeB);
            Assert.NotNull(bundle);
            return bundle!.Values.ContainsKey("generic.password");
        });

        Assert.False(value);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}

file sealed class PortableTestHostEnvironment : IHostEnvironment
{
    public PortableTestHostEnvironment(string contentRoot)
    {
        ContentRootPath = contentRoot;
        ContentRootFileProvider = new NullFileProvider();
    }

    public string ApplicationName { get; set; } = "Matmon.Tests";
    public string EnvironmentName { get; set; } = "Development";
    public string ContentRootPath { get; set; }
    public IFileProvider ContentRootFileProvider { get; set; }
}
