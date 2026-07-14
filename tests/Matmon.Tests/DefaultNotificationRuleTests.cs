using Matmon.Core.Domain;
using Matmon.Core.Telemetry;
using Matmon.Host.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

/// <summary>
/// A fresh instance should alert out of the box once e-mail is configured: setup seeds one default rule
/// (all sensors, Warning+Critical, to every user via the built-in "All users" receiver, no fixed sender so it
/// falls back to the workspace SMTP), and that rule must survive the load-time fixup without losing the
/// virtual receiver.
/// </summary>
public sealed class DefaultNotificationRuleTests : IDisposable
{
    private readonly string _dir;
    private readonly string _workspacePath;
    private readonly string _dbPath;

    public DefaultNotificationRuleTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "matmon-notif-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _workspacePath = Path.Combine(_dir, "workspace.json");
        _dbPath = Path.Combine(_dir, "telemetry.db");
    }

    private InMemoryMonitoringWorkspaceStore NewStore(ITelemetryRepository telemetry) =>
        new(
            new TestHostEnvironment(_dir),
            new MatmonRuntimeOptions { WorkspacePath = _workspacePath },
            new MatmonAuthOptions(),
            new EphemeralDataProtectionProvider(),
            telemetry,
            NullLogger<InMemoryMonitoringWorkspaceStore>.Instance);

    [Fact]
    public void Fresh_setup_seeds_a_default_all_users_email_rule()
    {
        using var telemetry = new SqliteTelemetryRepository(_dbPath);
        using var store = NewStore(telemetry);

        Assert.True(store.IsSetupRequired());
        Assert.Empty(store.Workspace.NotificationRules);

        store.CompleteInitialSetup("admin@example.com", "password123");

        var rule = Assert.Single(store.Workspace.NotificationRules);
        Assert.True(rule.Enabled);
        Assert.Equal(NotificationChannelKind.Email, rule.ChannelKind);
        Assert.Null(rule.SenderId); // no fixed sender -> falls back to the workspace default SMTP
        Assert.Equal(NotificationReceiverDefaults.AllUsersReceiverId, rule.ReceiverId);
        Assert.Null(rule.TargetElementId); // whole topology
        Assert.True(rule.IncludeDescendants);
        Assert.Contains(SensorState.Warning, rule.TriggerStates);
        Assert.Contains(SensorState.Critical, rule.TriggerStates);
    }

    [Fact]
    public void Default_rule_survives_reload_with_its_built_in_receiver_intact()
    {
        Guid ruleId;
        using (var telemetry = new SqliteTelemetryRepository(_dbPath))
        using (var store = NewStore(telemetry))
        {
            store.CompleteInitialSetup("admin@example.com", "password123");
            ruleId = store.Workspace.NotificationRules.Single().Id;
        } // Dispose flushes the workspace to disk

        using var telemetry2 = new SqliteTelemetryRepository(_dbPath);
        using var reloaded = NewStore(telemetry2);

        var rule = reloaded.Workspace.NotificationRules.Single();
        Assert.Equal(ruleId, rule.Id);
        // The load-time fixup must NOT clobber the built-in "All users" receiver (it isn't in the
        // receivers list) nor invent a sender - otherwise the default rule silently stops working.
        Assert.Equal(NotificationReceiverDefaults.AllUsersReceiverId, rule.ReceiverId);
        Assert.Null(rule.SenderId);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception)
        {
            // Best-effort temp cleanup.
        }
    }
}

file sealed class TestHostEnvironment : IHostEnvironment
{
    public TestHostEnvironment(string contentRoot)
    {
        ContentRootPath = contentRoot;
        ContentRootFileProvider = new NullFileProvider();
    }

    public string ApplicationName { get; set; } = "Matmon.Tests";
    public string EnvironmentName { get; set; } = "Development";
    public string ContentRootPath { get; set; }
    public IFileProvider ContentRootFileProvider { get; set; }
}
