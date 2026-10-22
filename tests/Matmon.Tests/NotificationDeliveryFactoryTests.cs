using System.Text.Json;
using Matmon.Core.Domain;
using Matmon.Core.Telemetry;
using Matmon.Host.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

/// <summary>
/// The sender picks the channel, the receiver is the one source of the recipient - and a rule that cannot be delivered
/// says why instead of vanishing. Two bugs lived here: a rule with a webhook sender was dropped without a word (the
/// dispatcher only let "Email" rules through), and a rule switched to a built-in receiver kept mailing the address of
/// the receiver it had before (the legacy Recipient copy won).
/// </summary>
public sealed class NotificationDeliveryFactoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "matmon-delivery-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteTelemetryRepository _telemetry;
    private readonly InMemoryMonitoringWorkspaceStore _store;

    public NotificationDeliveryFactoryTests()
    {
        Directory.CreateDirectory(_dir);
        _telemetry = new SqliteTelemetryRepository(Path.Combine(_dir, "telemetry.db"));
        _store = new InMemoryMonitoringWorkspaceStore(
            new DeliveryTestHostEnvironment(_dir),
            new MatmonRuntimeOptions { WorkspacePath = Path.Combine(_dir, "workspace.json") },
            new MatmonAuthOptions(),
            new EphemeralDataProtectionProvider(),
            _telemetry,
            NullLogger<InMemoryMonitoringWorkspaceStore>.Instance);
        _store.CompleteInitialSetup("admin@example.com", "password123");
    }

    public void Dispose()
    {
        _store.Dispose();
        _telemetry.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    private static NotificationTemplateContext Context()
    {
        var context = new NotificationTemplateContext();
        context.SetValue("state.label", "Critical");
        context.SetValue("state.key", "critical");
        context.SetValue("element.path", "Main / UPS");
        context.SetValue("message", "on battery");
        NotificationRendering.SetStateValues(context, SensorState.Critical, recovered: false);
        return context;
    }

    private NotificationRoute Route(Guid? senderId, Guid? receiverId, string? legacyRecipient = null) =>
        new("Test rule", senderId, receiverId, legacyRecipient, null, null, null, null);

    [Fact]
    public void A_webhook_sender_builds_its_request_with_the_receiver_as_the_target()
    {
        var sender = _store.CreateNotificationSender("ntfy");
        sender.Kind = NotificationEndpointKind.Webhook;
        var preset = WebhookPresets.Find("ntfy")!;
        sender.Webhook.EndpointUrl = preset.Url;
        sender.Webhook.BodyTemplate = preset.Body;
        var receiver = _store.CreateNotificationReceiver("Phone");
        receiver.Kind = NotificationEndpointKind.Webhook;
        receiver.Target = "ups-alerts";

        var (delivery, problem) = new NotificationDeliveryFactory(_store).Create(Route(sender.Id, receiver.Id), Context(), _store.Workspace);

        Assert.Null(problem);
        Assert.Equal(NotificationTransport.Webhook, delivery!.Transport);
        using var body = JsonDocument.Parse(delivery.Webhook!.Body!);
        Assert.Equal("ups-alerts", body.RootElement.GetProperty("topic").GetString());
        Assert.Equal("Critical: Main / UPS", body.RootElement.GetProperty("title").GetString());
        Assert.Equal(5, body.RootElement.GetProperty("priority").GetInt32());
    }

    [Fact]
    public void A_named_receiver_wins_over_the_legacy_recipient_copy()
    {
        _store.Workspace.NotificationConfiguration.Email.SmtpHost = "smtp.example.com";

        var (delivery, problem) = new NotificationDeliveryFactory(_store).Create(
            Route(null, NotificationReceiverDefaults.AllUsersReceiverId, legacyRecipient: "previous-receiver@example.com"),
            Context(),
            _store.Workspace);

        Assert.Null(problem);
        Assert.Equal(NotificationTransport.Email, delivery!.Transport);
        Assert.Equal("admin@example.com", delivery.Recipient);
    }

    [Fact]
    public void A_rule_from_before_receivers_still_mails_its_own_recipient()
    {
        _store.Workspace.NotificationConfiguration.Email.SmtpHost = "smtp.example.com";

        var (delivery, _) = new NotificationDeliveryFactory(_store).Create(Route(null, null, "ops@example.com"), Context(), _store.Workspace);

        Assert.Equal("ops@example.com", delivery!.Recipient);
    }

    [Fact]
    public void An_email_sender_with_a_webhook_value_receiver_says_why_nothing_goes_out()
    {
        var sender = _store.CreateNotificationSender("SMTP");
        sender.Kind = NotificationEndpointKind.Email;
        sender.Email.SmtpHost = "smtp.example.com";
        var receiver = _store.CreateNotificationReceiver("Topic");
        receiver.Kind = NotificationEndpointKind.Webhook;
        receiver.Target = "ups-alerts";

        var (delivery, problem) = new NotificationDeliveryFactory(_store).Create(Route(sender.Id, receiver.Id), Context(), _store.Workspace);

        Assert.Null(delivery);
        Assert.Contains("e-mail address", problem);
    }

    [Fact]
    public void A_switched_off_sender_says_so()
    {
        var sender = _store.CreateNotificationSender("Off");
        sender.Enabled = false;

        var (delivery, problem) = new NotificationDeliveryFactory(_store).Create(Route(sender.Id, NotificationReceiverDefaults.AllUsersReceiverId), Context(), _store.Workspace);

        Assert.Null(delivery);
        Assert.Contains("switched off", problem);
    }

    [Fact]
    public void The_cloud_sender_without_a_cloud_relay_says_where_to_switch_it_on()
    {
        var sender = _store.CreateNotificationSender("Matmon Cloud");
        sender.Kind = NotificationEndpointKind.Cloud;

        var (delivery, problem) = new NotificationDeliveryFactory(_store).Create(Route(sender.Id, NotificationReceiverDefaults.AllUsersReceiverId), Context(), _store.Workspace);

        Assert.Null(delivery);
        Assert.Contains("System > Cloud", problem);
    }

    // --- SMS and calls through the cloud -------------------------------------------------------------------------

    private (NotificationSender Sender, NotificationReceiver Receiver) CloudPhone(NotificationEndpointKind kind)
    {
        _store.SetCloudConnectionSettings("https://cloud.example", Guid.NewGuid().ToString(), "instance-token", enabled: true);
        _store.SetCloudRelaySettings(true);
        var sender = _store.Workspace.NotificationSenders.Single(candidate => candidate.Kind == NotificationEndpointKind.Cloud);
        var receiver = _store.CreateNotificationReceiver("On call");
        receiver.Kind = kind;
        receiver.Target = "+491701234567";
        return (sender, receiver);
    }

    [Theory]
    [InlineData(NotificationEndpointKind.Sms, "sms")]
    [InlineData(NotificationEndpointKind.Voice, "voice")]
    public void A_critical_alert_to_a_phone_receiver_goes_to_the_cloud_as_an_sms_or_a_call(NotificationEndpointKind kind, string channel)
    {
        var (sender, receiver) = CloudPhone(kind);

        var (delivery, problem) = new NotificationDeliveryFactory(_store).Create(Route(sender.Id, receiver.Id), Context(), _store.Workspace);

        Assert.Null(problem);
        Assert.Equal(NotificationTransport.Cloud, delivery!.Transport);
        Assert.Equal(channel, delivery.CloudChannel);
        Assert.Equal("critical", delivery.Severity);
        Assert.Equal("+491701234567", delivery.Recipient);
        Assert.DoesNotContain("1701234567", delivery.RecipientLabel); // log lines carry a masked number
        Assert.True(NotificationDeliveryFactory.IsPhoneRoute(Route(sender.Id, receiver.Id), _store.Workspace));
    }

    [Fact]
    public void A_warning_or_a_recovery_never_becomes_an_sms()
    {
        var (sender, receiver) = CloudPhone(NotificationEndpointKind.Sms);
        var warning = Context();
        NotificationRendering.SetStateValues(warning, SensorState.Warning, recovered: false);
        var recovered = Context();
        NotificationRendering.SetStateValues(recovered, SensorState.Critical, recovered: true);

        var factory = new NotificationDeliveryFactory(_store);
        Assert.Contains("critical problems only", factory.Create(Route(sender.Id, receiver.Id), warning, _store.Workspace).Problem);
        Assert.Contains("critical problems only", factory.Create(Route(sender.Id, receiver.Id), recovered, _store.Workspace).Problem);
    }

    [Fact]
    public void A_phone_receiver_without_the_cloud_sender_says_what_it_needs()
    {
        var receiver = _store.CreateNotificationReceiver("On call");
        receiver.Kind = NotificationEndpointKind.Sms;
        receiver.Target = "+491701234567";

        var (delivery, problem) = new NotificationDeliveryFactory(_store).Create(Route(null, receiver.Id), Context(), _store.Workspace);

        Assert.Null(delivery);
        Assert.Contains("Matmon Cloud sender", problem);
    }

    [Theory]
    [InlineData("+49 170 1234567", "+491701234567")]
    [InlineData("0049 (170) 123-4567", "+491701234567")]
    [InlineData("0170 1234567", null)]
    [InlineData("+0170", null)]
    [InlineData("ops@example.com", null)]
    public void A_phone_number_is_kept_in_international_format(string input, string? expected) =>
        Assert.Equal(expected, PhoneNumbers.Normalize(input));

    [Fact]
    public void A_webhook_that_cannot_be_built_names_the_sender_and_the_reason()
    {
        var sender = _store.CreateNotificationSender("Broken");
        sender.Kind = NotificationEndpointKind.Webhook;
        sender.Webhook.EndpointUrl = "not a url";

        var (delivery, problem) = new NotificationDeliveryFactory(_store).Create(Route(sender.Id, null), Context(), _store.Workspace);

        Assert.Null(delivery);
        Assert.Contains("Broken", problem);
        Assert.Contains("http", problem);
    }
}

file sealed class DeliveryTestHostEnvironment(string contentRoot) : IHostEnvironment
{
    public string ApplicationName { get; set; } = "Matmon.Tests";
    public string EnvironmentName { get; set; } = "Development";
    public string ContentRootPath { get; set; } = contentRoot;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
