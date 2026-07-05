namespace Matmon.Core.Domain;

public enum NotificationChannelKind
{
    Email = 0,
    Webhook = 1
}

public enum NotificationEndpointKind
{
    Email = 0,
    Webhook = 1,

    /// <summary>Delivered via the Matmon.Cloud notification gateway (no local SMTP). Enabled from
    /// System → Cloud; recipients come from the rule's receiver, like any other sender.</summary>
    Cloud = 2
}

public sealed class NotificationWorkspaceConfiguration
{
    public EmailNotificationSettings Email { get; set; } = new();

    public WebhookNotificationSettings Webhook { get; set; } = new();
}

public sealed class NotificationSender
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public NotificationEndpointKind Kind { get; set; } = NotificationEndpointKind.Email;

    public EmailNotificationSettings Email { get; set; } = new();

    public WebhookNotificationSettings Webhook { get; set; } = new();
}

public sealed class NotificationReceiver
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public NotificationEndpointKind Kind { get; set; } = NotificationEndpointKind.Email;

    public string Target { get; set; } = string.Empty;

    public string? Secret { get; set; }

    public int? TimeoutSeconds { get; set; } = 10;
}

public sealed class EmailNotificationSettings
{
    public string SenderName { get; set; } = "Matmon";

    public string SenderEmail { get; set; } = string.Empty;

    public string SmtpHost { get; set; } = string.Empty;

    public int? SmtpPort { get; set; } = 587;

    public bool UseSsl { get; set; } = true;

    public string? Username { get; set; }

    public string? Password { get; set; }
}

public sealed class WebhookNotificationSettings
{
    public string EndpointUrl { get; set; } = string.Empty;

    public string? Secret { get; set; }

    public int? TimeoutSeconds { get; set; } = 10;
}
