using Matmon.Core.Domain;
using Matmon.Core.Telemetry;
using Matmon.Host.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

/// <summary>A cloud backup job's optional passphrase is DataProtection-encrypted at rest (never plaintext in
/// workspace.json) and hydrates back on reload - so a scheduled cloud backup can be portable across a restart.
/// A shared DataProtection provider across two stores stands in for one instance restarting.</summary>
public sealed class BackupJobPassphraseTests : IDisposable
{
    private const string Passphrase = "portable-secret-123";
    private readonly string _dir;

    public BackupJobPassphraseTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "matmon-jobpass-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    private InMemoryMonitoringWorkspaceStore NewStore(IDataProtectionProvider dp, string wsPath, ITelemetryRepository telemetry) =>
        new(new JobPassHostEnvironment(_dir),
            new MatmonRuntimeOptions { WorkspacePath = wsPath },
            new MatmonAuthOptions(),
            dp,
            telemetry,
            NullLogger<InMemoryMonitoringWorkspaceStore>.Instance);

    [Fact]
    public void Cloud_job_passphrase_is_encrypted_at_rest_and_survives_a_restart()
    {
        var dp = new EphemeralDataProtectionProvider(); // one instance's DP ring, shared across the "restart"
        var wsPath = Path.Combine(_dir, "ws.json");
        Guid jobId;

        using (var t1 = new SqliteTelemetryRepository(Path.Combine(_dir, "t1.db")))
        using (var s1 = NewStore(dp, wsPath, t1))
        {
            var created = s1.CreateBackupJob(new WorkspaceBackupJob
            {
                Name = "Nightly cloud",
                Destination = BackupDestination.Cloud,
                Passphrase = Passphrase,
                Schedule = new MonitoringSchedule { Mode = MonitoringScheduleMode.Daily, TimeOfDay = TimeSpan.FromHours(2) }
            });
            jobId = created.Id;
            Assert.Equal(Passphrase, created.Passphrase);
        } // dispose flushes to disk (encrypted)

        var json = File.ReadAllText(wsPath);
        Assert.DoesNotContain(Passphrase, json); // never at rest in the clear

        using (var t2 = new SqliteTelemetryRepository(Path.Combine(_dir, "t2.db")))
        using (var s2 = NewStore(dp, wsPath, t2)) // same DP ring = same instance after a restart
        {
            var reloaded = s2.FindBackupJob(jobId)!;
            Assert.Equal(Passphrase, reloaded.Passphrase); // hydrated back from the ciphertext
        }
    }

    [Fact]
    public void Blank_passphrase_on_update_keeps_the_stored_one()
    {
        var dp = new EphemeralDataProtectionProvider();
        var wsPath = Path.Combine(_dir, "ws2.json");
        using var t = new SqliteTelemetryRepository(Path.Combine(_dir, "t.db"));
        using var store = NewStore(dp, wsPath, t);

        var created = store.CreateBackupJob(new WorkspaceBackupJob
        {
            Name = "Nightly cloud",
            Destination = BackupDestination.Cloud,
            Passphrase = Passphrase,
            Schedule = new MonitoringSchedule { Mode = MonitoringScheduleMode.Daily, TimeOfDay = TimeSpan.FromHours(2) }
        });

        // Editing the job without re-entering the passphrase (blank) must keep it.
        var edit = store.FindBackupJob(created.Id)!;
        edit.Passphrase = null;
        edit.Description = "edited";
        store.UpdateBackupJob(edit);

        Assert.Equal(Passphrase, store.FindBackupJob(created.Id)!.Passphrase);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}

file sealed class JobPassHostEnvironment : IHostEnvironment
{
    public JobPassHostEnvironment(string contentRoot)
    {
        ContentRootPath = contentRoot;
        ContentRootFileProvider = new NullFileProvider();
    }

    public string ApplicationName { get; set; } = "Matmon.Tests";
    public string EnvironmentName { get; set; } = "Development";
    public string ContentRootPath { get; set; }
    public IFileProvider ContentRootFileProvider { get; set; }
}
