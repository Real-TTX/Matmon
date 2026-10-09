using Matmon.Core.Domain;
using Matmon.Core.Telemetry;
using Matmon.Host.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

/// <summary>
/// Migration 5. A scheduled local job saved through the editor was stored as the list of the eleven sections the
/// editor offered, so when "everything" grew (map images, MIBs) such a job kept backing up the old eleven while
/// looking like an "All" job. It is brought up to date once; a job the admin narrowed down is left alone, and so is
/// one that is already past the migration.
/// </summary>
public sealed class BackupJobSectionMigrationTests : IDisposable
{
    private const string Eleven = "Topology, Templates, SensorDefinitions, Notifications, Maps, Users, Alerts, SensorHistory, Events, Statistics, BackupJobs";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "matmon-jobmig-" + Guid.NewGuid().ToString("N"));

    public BackupJobSectionMigrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    private static string Job(string id, string name, string sections, string destination = "Local") => $$"""
        { "id": "{{id}}", "name": "{{name}}", "sections": "{{sections}}", "destination": "{{destination}}" }
        """;

    private IReadOnlyList<WorkspaceBackupJob> BootWith(int schemaVersion, params string[] jobs)
    {
        var path = Path.Combine(_dir, $"ws-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, $$"""
            {
              "schemaVersion": {{schemaVersion}},
              "rootProbe": { "$kind": "probe", "probeId": "primary", "id": "11111111-1111-1111-1111-111111111111", "name": "Primary Probe", "children": [] },
              "backupJobs": [ {{string.Join(",", jobs)}} ]
            }
            """);

        using var telemetry = new SqliteTelemetryRepository(Path.Combine(_dir, $"t-{Guid.NewGuid():N}.db"));
        using var store = new InMemoryMonitoringWorkspaceStore(
            new JobMigrationHostEnvironment(_dir),
            new MatmonRuntimeOptions { WorkspacePath = path },
            new MatmonAuthOptions(),
            new EphemeralDataProtectionProvider(),
            telemetry,
            NullLogger<InMemoryMonitoringWorkspaceStore>.Instance);
        return store.GetBackupJobs();
    }

    private static WorkspaceBackupJob Named(IReadOnlyList<WorkspaceBackupJob> jobs, string name) => jobs.Single(job => job.Name == name);

    [Fact]
    public void A_job_that_selected_all_eleven_sections_now_means_everything()
    {
        var jobs = BootWith(4, Job("aaaaaaaa-0000-0000-0000-000000000001", "all eleven", Eleven));

        Assert.Equal(WorkspaceBackupSection.All, Named(jobs, "all eleven").Sections);
    }

    [Fact]
    public void A_job_that_already_had_the_map_images_gets_the_mibs_too()
    {
        var jobs = BootWith(4, Job("aaaaaaaa-0000-0000-0000-000000000002", "twelve", Eleven + ", MapAssets"));

        Assert.Equal(WorkspaceBackupSection.All, Named(jobs, "twelve").Sections);
    }

    [Fact]
    public void A_job_the_admin_narrowed_down_is_left_alone()
    {
        var jobs = BootWith(4, Job("aaaaaaaa-0000-0000-0000-000000000003", "narrow", "Topology, Maps"));

        Assert.Equal(WorkspaceBackupSection.Topology | WorkspaceBackupSection.Maps, Named(jobs, "narrow").Sections);
    }

    [Fact]
    public void A_job_missing_even_one_of_the_eleven_is_left_alone()
    {
        var withoutUsers = Eleven.Replace("Users, ", string.Empty);
        var jobs = BootWith(4, Job("aaaaaaaa-0000-0000-0000-000000000004", "no users", withoutUsers));

        Assert.Equal(WorkspaceBackupSection.All & ~(WorkspaceBackupSection.Users | WorkspaceBackupSection.MapAssets | WorkspaceBackupSection.Mibs), Named(jobs, "no users").Sections);
    }

    [Fact]
    public void A_cloud_job_is_not_touched_because_it_ignores_its_sections()
    {
        var jobs = BootWith(4, Job("aaaaaaaa-0000-0000-0000-000000000005", "cloud", Eleven, "Cloud"));

        Assert.Equal(WorkspaceBackupSection.All & ~(WorkspaceBackupSection.MapAssets | WorkspaceBackupSection.Mibs), Named(jobs, "cloud").Sections);
    }

    [Fact]
    public void A_job_stored_as_the_word_all_picks_the_new_sections_up_by_itself()
    {
        var jobs = BootWith(4, Job("aaaaaaaa-0000-0000-0000-000000000006", "by name", "All"));

        Assert.Equal(WorkspaceBackupSection.All, Named(jobs, "by name").Sections);
    }

    [Fact]
    public void The_migration_runs_once_so_an_admin_who_unticks_the_mibs_afterwards_keeps_it_that_way()
    {
        // Already at version 5: the twelve sections without MIBs is a choice the admin made in the editor, not a
        // leftover of the old list.
        var jobs = BootWith(5, Job("aaaaaaaa-0000-0000-0000-000000000007", "no mibs", Eleven + ", MapAssets"));

        Assert.Equal(WorkspaceBackupSection.All & ~WorkspaceBackupSection.Mibs, Named(jobs, "no mibs").Sections);
    }
}

file sealed class JobMigrationHostEnvironment(string root) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Test";
    public string ApplicationName { get; set; } = "Matmon.Tests";
    public string ContentRootPath { get; set; } = root;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
