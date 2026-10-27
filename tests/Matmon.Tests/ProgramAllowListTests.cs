using System.Text;
using Matmon.Core.Domain;
using Matmon.Core.Telemetry;
using Matmon.Host.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

/// <summary>The Local Program allow-list kept on the instance (System > General) instead of only in an environment variable.</summary>
[Collection(ProgramAllowListCollection.Name)]
public sealed class ProgramAllowListTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "matmon-allowlist-" + Guid.NewGuid().ToString("N"));

    public ProgramAllowListTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        LocalProgramSensorExecutor.SetConfiguredAllowList([]);
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private InMemoryMonitoringWorkspaceStore NewStore(SqliteTelemetryRepository telemetry)
    {
        var store = new InMemoryMonitoringWorkspaceStore(
            new AllowListHostEnvironment(_dir), new MatmonRuntimeOptions { WorkspacePath = Path.Combine(_dir, "ws.json") },
            new MatmonAuthOptions(), new EphemeralDataProtectionProvider(), telemetry, NullLogger<InMemoryMonitoringWorkspaceStore>.Instance);
        store.CompleteInitialSetup("admin@example.com", "password123");
        return store;
    }

    private static string Folder => OperatingSystem.IsWindows() ? @"C:\Tools\zz-allowtest" : "/opt/zz-allowtest";

    [Fact]
    public void A_saved_list_applies_at_once_to_the_sensor_and_its_program_choice()
    {
        using var telemetry = new SqliteTelemetryRepository(Path.Combine(_dir, "t.db"));
        using var store = NewStore(telemetry);

        store.SetAllowedProgramPaths([Folder, " ", Folder]);

        Assert.Equal([Folder], store.GetAllowedProgramPaths());
        Assert.Contains(Folder, LocalProgramSensorExecutor.ReadAllowList());
        Assert.True(LocalProgramSensorExecutor.IsAllowed(Path.Combine(Folder, "check"), LocalProgramSensorExecutor.ReadAllowList()));

        var choice = store.GetSensorDefinitions()
            .Single(definition => definition.Key == "local-program")
            .Parameters.Single(parameter => parameter.Key == "program.base")
            .Options;
        Assert.Contains(choice, option => option.Value == Folder);

        store.SetAllowedProgramPaths([]);
        Assert.DoesNotContain(Folder, LocalProgramSensorExecutor.ReadAllowList());
    }

    [Fact]
    public void The_list_survives_a_restart_but_never_rides_in_a_backup()
    {
        using (var telemetry = new SqliteTelemetryRepository(Path.Combine(_dir, "t.db")))
        using (var store = NewStore(telemetry))
        {
            store.SetAllowedProgramPaths([Folder]);
            var package = Encoding.UTF8.GetString(store.CreateBackupBytes(WorkspaceBackupSection.All));
            Assert.DoesNotContain("zz-allowtest", package);
        }

        LocalProgramSensorExecutor.SetConfiguredAllowList([]);
        using var reopenedTelemetry = new SqliteTelemetryRepository(Path.Combine(_dir, "t.db"));
        using var reopened = new InMemoryMonitoringWorkspaceStore(
            new AllowListHostEnvironment(_dir), new MatmonRuntimeOptions { WorkspacePath = Path.Combine(_dir, "ws.json") },
            new MatmonAuthOptions(), new EphemeralDataProtectionProvider(), reopenedTelemetry, NullLogger<InMemoryMonitoringWorkspaceStore>.Instance);
        Assert.Equal([Folder], reopened.GetAllowedProgramPaths());
        Assert.Contains(Folder, LocalProgramSensorExecutor.ReadAllowList()); // loading puts it into effect
    }

    [Fact]
    public void Probes_get_the_list_with_their_assignments()
    {
        using var telemetry = new SqliteTelemetryRepository(Path.Combine(_dir, "t.db"));
        using var store = NewStore(telemetry);
        var root = store.GetAllElements().OfType<ProbeElement>().Single(probe => probe.ParentId is null);
        var remote = store.CreateProbe(root.Id, "Branch", null);
        store.UpdateElement(remote.Id, element => ((ProbeElement)element).ProbeId = "branch-01");
        store.SetAllowedProgramPaths([Folder]);

        var assignments = new ProbeSensorAssignmentProvider(store).BuildAssignments("branch-01");

        Assert.Equal([Folder], assignments.AllowedProgramPaths);
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProgramAllowListCollection
{
    // The configured list is process-wide and every store sets it when it loads: these tests run alone, not beside the
    // others (which build stores of their own all the time).
    public const string Name = "program-allow-list";
}

file sealed class AllowListHostEnvironment(string contentRoot) : IHostEnvironment
{
    public string ApplicationName { get; set; } = "Matmon.Tests";
    public string EnvironmentName { get; set; } = "Development";
    public string ContentRootPath { get; set; } = contentRoot;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
