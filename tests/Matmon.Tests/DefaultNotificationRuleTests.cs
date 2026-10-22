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

    [Theory]
    [InlineData(NotificationEndpointKind.Webhook)]
    [InlineData(NotificationEndpointKind.Email)]
    public void A_sender_created_later_is_not_forced_onto_the_default_rule(NotificationEndpointKind kind)
    {
        // The fix-up used to give every sender-less rule "the first sender there is": the default rule became a webhook
        // rule as soon as a webhook sender existed, and its e-mail alerts stopped.
        using (var telemetry = new SqliteTelemetryRepository(_dbPath))
        using (var store = NewStore(telemetry))
        {
            store.CompleteInitialSetup("admin@example.com", "password123");
            var sender = store.CreateNotificationSender("Push");
            sender.Kind = kind;
            store.Save();
        }

        using var telemetry2 = new SqliteTelemetryRepository(_dbPath);
        using var reloaded = NewStore(telemetry2);

        Assert.Null(reloaded.Workspace.NotificationRules.Single().SenderId);
    }

    [Fact]
    public void Migration_8_takes_the_guessed_webhook_sender_off_a_rule_that_mails_a_user_group()
    {
        var path = Path.Combine(_dir, "ws-mig8.json");
        File.WriteAllText(path, """
            {
              "schemaVersion": 7,
              "rootProbe": { "$kind": "probe", "probeId": "primary", "id": "11111111-1111-1111-1111-111111111111", "name": "Primary Probe", "children": [] },
              "notificationSenders": [ { "id": "aaaaaaaa-0000-0000-0000-000000000001", "name": "ntfy", "kind": "Webhook", "webhook": { "endpointUrl": "https://ntfy.sh" } } ],
              "notificationReceivers": [ { "id": "bbbbbbbb-0000-0000-0000-000000000001", "name": "topic", "kind": "Webhook", "target": "alerts" } ],
              "notificationRules": [
                { "id": "cccccccc-0000-0000-0000-000000000001", "name": "Default alerts", "channelKind": "Webhook", "senderId": "aaaaaaaa-0000-0000-0000-000000000001", "receiverId": "00000000-0000-0000-0000-00000000a11c", "triggerStates": [ "Warning", "Critical" ] },
                { "id": "cccccccc-0000-0000-0000-000000000002", "name": "Push", "channelKind": "Webhook", "senderId": "aaaaaaaa-0000-0000-0000-000000000001", "receiverId": "bbbbbbbb-0000-0000-0000-000000000001", "triggerStates": [ "Critical" ] }
              ]
            }
            """);

        using var telemetry = new SqliteTelemetryRepository(_dbPath);
        using var store = new InMemoryMonitoringWorkspaceStore(
            new TestHostEnvironment(_dir),
            new MatmonRuntimeOptions { WorkspacePath = path },
            new MatmonAuthOptions(),
            new EphemeralDataProtectionProvider(),
            telemetry,
            NullLogger<InMemoryMonitoringWorkspaceStore>.Instance);

        var rules = store.Workspace.NotificationRules;
        var repaired = rules.Single(rule => rule.Name == "Default alerts");
        Assert.Null(repaired.SenderId);
        Assert.Equal(NotificationChannelKind.Email, repaired.ChannelKind);
        // A real webhook rule (a webhook sender with a webhook value as the receiver) is left alone.
        Assert.Equal(new Guid("aaaaaaaa-0000-0000-0000-000000000001"), rules.Single(rule => rule.Name == "Push").SenderId);
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
