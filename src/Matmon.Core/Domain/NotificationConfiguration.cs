using System.Text.Json.Serialization;

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
    Cloud = 2,

    /// <summary>A RECEIVER kind: a mobile number that gets an SMS through the Matmon Cloud sender - for critical
    /// problems only, within the cloud's limits (it costs money per message).</summary>
    Sms = 3,

    /// <summary>A RECEIVER kind: a phone number the cloud calls and reads the alert to - critical problems only.</summary>
    Voice = 4,

    /// <summary>A SENDER kind: browser notifications (Web Push) to the devices the users switched them on in (Account page).
    /// Built in, one per instance; the receiver names the users - a built-in user group, or a receiver with a user's
    /// e-mail address.</summary>
    Push = 5
}

/// <summary>Phone numbers of SMS / call receivers: international format, the same rule the cloud applies.</summary>
public static class PhoneNumbers
{
    /// <summary>+4917012345678 from "+49 170 1234567", "0049-170/1234567" and the like; null for anything else - a
    /// national number cannot be dialled from a gateway in another country.</summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var compact = new string(input.Where(character => !char.IsWhiteSpace(character) && character is not ('-' or '/' or '(' or ')' or '.')).ToArray());
        if (compact.StartsWith("00", StringComparison.Ordinal))
        {
            compact = "+" + compact[2..];
        }

        return compact.Length is >= 9 and <= 16 && compact[0] == '+' && compact[1] is >= '1' and <= '9' && compact.Skip(1).All(char.IsAsciiDigit)
            ? compact
            : null;
    }

    /// <summary>"+49••••••67" - a log line says which number without printing it whole.</summary>
    public static string Mask(string number) =>
        number.Length <= 6 ? number : number[..3] + new string('•', number.Length - 5) + number[^2..];

    public static bool IsPhoneKind(NotificationEndpointKind kind) => kind is NotificationEndpointKind.Sms or NotificationEndpointKind.Voice;
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

    /// <summary>In-memory plaintext (cleared before serialization; the ciphertext lives in
    /// <see cref="ProtectedSecret"/>). Never written to workspace.json in the clear.</summary>
    public string? Secret { get; set; }

    /// <summary>DataProtection-encrypted <see cref="Secret"/> - this is what persists.</summary>
    public string? ProtectedSecret { get; set; }

    /// <summary>Set when decryption failed on load, so save doesn't clobber good ciphertext with empty.</summary>
    [JsonIgnore] public bool SecretHydrationFailed { get; set; }

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

    /// <summary>In-memory plaintext SMTP password (cleared before serialization; ciphertext lives in
    /// <see cref="ProtectedPassword"/>). Never written to workspace.json in the clear.</summary>
    public string? Password { get; set; }

    /// <summary>DataProtection-encrypted <see cref="Password"/> - this is what persists.</summary>
    public string? ProtectedPassword { get; set; }

    /// <summary>Set when decryption failed on load, so save doesn't clobber good ciphertext with empty.</summary>
    [JsonIgnore] public bool PasswordHydrationFailed { get; set; }
}

/// <summary>
/// An outbound HTTP request per notification - push services (ntfy, Pushover, Gotify, Telegram), chat (Teams, Slack,
/// Discord) or anything else that takes a request. The URL, the headers and the body are templates with the same
/// placeholders as the e-mail templates, plus <c>{{receiver.target}}</c> (the rule's receiver: a topic, a user key, a
/// chat id) and <c>{{secret}}</c> (<see cref="Secret"/>, encrypted at rest). <see cref="WebhookRequestBuilder"/> turns
/// them into the request; <see cref="WebhookPresets"/> fill the fields for the common services.
/// </summary>
public sealed class WebhookNotificationSettings
{
    /// <summary>Which preset the fields were last filled from - only remembered for the editor. The request is built
    /// from the fields, so a preset that was edited afterwards sends what was edited.</summary>
    public string? Preset { get; set; }

    /// <summary>The address, a template: placeholders are URL-encoded, <c>{{{key}}}</c> inserts a value as it is.</summary>
    public string EndpointUrl { get; set; } = string.Empty;

    public string Method { get; set; } = "POST";

    /// <summary>JSON, form or plain text - it also decides how a placeholder is escaped in the body.</summary>
    public string ContentType { get; set; } = WebhookContentTypes.Json;

    /// <summary>One <c>Name: value</c> per line; the values are templates. A token belongs in <see cref="Secret"/>
    /// and is written here as <c>{{secret}}</c>, so it is never stored in the clear.</summary>
    public string? Headers { get; set; }

    /// <summary>The body, a template. Empty = a generic JSON document (<see cref="WebhookRequestBuilder.DefaultJsonBody"/>),
    /// which is also what a webhook sender from before the templates sends.</summary>
    public string? BodyTemplate { get; set; }

    /// <summary>Adds <c>X-Matmon-Signature: sha256=&lt;HMAC-SHA256 of the body with the secret&gt;</c>, so the
    /// receiving end can check that the request came from this instance.</summary>
    public bool SignRequests { get; set; }

    /// <summary>In-memory plaintext (cleared before serialization; ciphertext lives in
    /// <see cref="ProtectedSecret"/>). Never written to workspace.json in the clear.</summary>
    public string? Secret { get; set; }

    /// <summary>DataProtection-encrypted <see cref="Secret"/> - this is what persists.</summary>
    public string? ProtectedSecret { get; set; }

    /// <summary>Set when decryption failed on load, so save doesn't clobber good ciphertext with empty.</summary>
    [JsonIgnore] public bool SecretHydrationFailed { get; set; }

    public int? TimeoutSeconds { get; set; } = 10;
}
