using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Matmon.Core.Domain;

public static class WebhookContentTypes
{
    public const string Json = "application/json";
    public const string Form = "application/x-www-form-urlencoded";
    public const string Text = "text/plain";

    public static readonly IReadOnlyList<string> All = [Json, Form, Text];

    /// <summary>One of <see cref="All"/>; anything else is treated as JSON, the common case.</summary>
    public static string Normalize(string? contentType)
    {
        var value = contentType?.Split(';')[0].Trim().ToLowerInvariant();
        return value is Form or Text ? value : Json;
    }
}

/// <summary>The request a webhook notification sends - built by <see cref="WebhookRequestBuilder"/>, sent by the Host.</summary>
public sealed record WebhookRequest(
    string Method,
    string Url,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    string? ContentType,
    string? Body)
{
    /// <summary>Scheme and host only - what may go into a log. The path and the query can carry a token (Telegram's
    /// bot token is part of the path), so the full address is never logged.</summary>
    public string SafeTarget => Uri.TryCreate(Url, UriKind.Absolute, out var uri) ? $"{uri.Scheme}://{uri.Authority}" : "(invalid address)";
}

/// <summary>
/// Turns a webhook sender's templates into a request. Pure, so the escaping - the part that is easy to get wrong - is
/// tested: a placeholder is URL-encoded in the address, JSON-escaped in a JSON body, URL-encoded per field in a form
/// body, inserted as it is in plain text, and stripped of line breaks in a header (a value with a new line would start
/// a header of its own). <c>{{{key}}}</c> inserts a value without any escaping, for the rare template that needs that.
/// </summary>
public static class WebhookRequestBuilder
{
    public const string SignatureHeader = "X-Matmon-Signature";

    public static readonly IReadOnlyList<string> Methods = ["POST", "PUT", "PATCH", "GET"];

    /// <summary>The body of a webhook with no template - and of every webhook sender from before the templates existed.</summary>
    public const string DefaultJsonBody = """
{
  "state": "{{state.key}}",
  "stateLabel": "{{state.label}}",
  "subject": "{{subject}}",
  "summary": "{{summary}}",
  "element": "{{element.path}}",
  "sensor": "{{sensor.name}}",
  "sensorType": "{{sensor.type}}",
  "message": "{{message}}",
  "value": "{{sensor.value_with_unit}}",
  "since": "{{problem.since}}",
  "probe": "{{probe.name}}",
  "rule": "{{rule.name}}"
}
""";

    public const string DefaultFormBody = """
subject={{subject}}
message={{summary}}
state={{state.key}}
""";

    public const string DefaultTextBody = "{{summary}}";

    private static readonly JavaScriptEncoder JsonEncoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

    /// <param name="values">The notification's placeholder values (<see cref="NotificationTemplateContext.Values"/>).</param>
    /// <param name="receiverTarget">The rule receiver's target - a topic, a user key, a chat id; empty when the rule has none.</param>
    /// <exception cref="InvalidOperationException">The address is not an absolute http(s) URL, or a JSON body is not
    /// valid JSON once the placeholders are filled in - said in words the sender editor can show.</exception>
    public static WebhookRequest Build(WebhookNotificationSettings settings, IReadOnlyDictionary<string, string> values, string? receiverTarget)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(values);

        var all = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase)
        {
            ["receiver.target"] = receiverTarget?.Trim() ?? string.Empty,
            ["secret"] = settings.Secret ?? string.Empty
        };

        var url = NotificationTemplateRenderer.Render(settings.EndpointUrl, all, Uri.EscapeDataString).Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("The webhook address is not an absolute http:// or https:// URL.");
        }

        var method = NormalizeMethod(settings.Method);
        var headers = BuildHeaders(settings.Headers, all);
        if (method == "GET")
        {
            return new WebhookRequest(method, url, headers, null, null);
        }

        var contentType = WebhookContentTypes.Normalize(settings.ContentType);
        var body = contentType switch
        {
            WebhookContentTypes.Form => BuildFormBody(Template(settings.BodyTemplate, DefaultFormBody), all),
            WebhookContentTypes.Text => NotificationTemplateRenderer.Render(Template(settings.BodyTemplate, DefaultTextBody), all, value => value),
            _ => BuildJsonBody(Template(settings.BodyTemplate, DefaultJsonBody), all)
        };

        if (settings.SignRequests && !string.IsNullOrEmpty(settings.Secret))
        {
            var signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(settings.Secret), Encoding.UTF8.GetBytes(body));
            headers = [.. headers, new(SignatureHeader, "sha256=" + Convert.ToHexStringLower(signature))];
        }

        return new WebhookRequest(method, url, headers, contentType, body);
    }

    public static string NormalizeMethod(string? method)
    {
        var value = method?.Trim().ToUpperInvariant();
        return value is not null && Methods.Contains(value) ? value : "POST";
    }

    private static string Template(string? template, string fallback) =>
        string.IsNullOrWhiteSpace(template) ? fallback : template;

    private static string BuildJsonBody(string template, IReadOnlyDictionary<string, string> values)
    {
        var body = NotificationTemplateRenderer.Render(template, values, value => JsonEncodedText.Encode(value, JsonEncoder).ToString());
        try
        {
            using var _ = JsonDocument.Parse(body);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"The webhook body is not valid JSON once the placeholders are filled in: {exception.Message}");
        }

        return body;
    }

    /// <summary>One <c>name=value</c> per line; every value is filled in and URL-encoded on its own, the name as it is.</summary>
    private static string BuildFormBody(string template, IReadOnlyDictionary<string, string> values)
    {
        var fields = new List<string>();
        foreach (var rawLine in template.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOf('=');
            var name = separator < 0 ? line : line[..separator].Trim();
            var value = separator < 0 ? string.Empty : line[(separator + 1)..];
            if (name.Length == 0)
            {
                continue;
            }

            fields.Add(Uri.EscapeDataString(name) + "=" + NotificationTemplateRenderer.Render(value, values, Uri.EscapeDataString));
        }

        return string.Join('&', fields);
    }

    private static IReadOnlyList<KeyValuePair<string, string>> BuildHeaders(string? template, IReadOnlyDictionary<string, string> values)
    {
        var headers = new List<KeyValuePair<string, string>>();
        if (string.IsNullOrWhiteSpace(template))
        {
            return headers;
        }

        foreach (var rawLine in template.Split('\n'))
        {
            var line = rawLine.Trim();
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var name = line[..separator].Trim();
            if (!IsHeaderName(name) || string.Equals(name, SignatureHeader, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = NotificationTemplateRenderer.Render(line[(separator + 1)..], values, StripLineBreaks).Trim();
            headers.Add(new(name, StripLineBreaks(value)));
        }

        return headers;
    }

    private static string StripLineBreaks(string value) => value.Replace("\r", " ").Replace("\n", " ");

    // RFC 9110 token characters.
    private static bool IsHeaderName(string name) =>
        name.Length > 0 && name.All(character => char.IsAsciiLetterOrDigit(character) || "!#$%&'*+-.^_`|~".Contains(character));
}

/// <summary>A ready-made webhook for a common service: what the sender editor fills in when it is picked.</summary>
public sealed record WebhookPreset(
    string Key,
    string Name,
    string Url,
    string Method,
    string ContentType,
    string Headers,
    string Body,
    string Hint);

public static class WebhookPresets
{
    public const string GenericKey = "generic";

    public static readonly IReadOnlyList<WebhookPreset> All =
    [
        new(GenericKey, "Generic JSON", string.Empty, "POST", WebhookContentTypes.Json, string.Empty, WebhookRequestBuilder.DefaultJsonBody,
            "Any service that accepts a JSON request. A token goes into Secret and is written as {{secret}} in a header or the body; Sign requests adds an X-Matmon-Signature header (HMAC-SHA256 of the body)."),

        new("ntfy", "ntfy (push)", "https://ntfy.sh", "POST", WebhookContentTypes.Json, string.Empty, """
{
  "topic": "{{receiver.target}}",
  "title": "{{subject}}",
  "message": "{{summary}}",
  "priority": {{state.priority}},
  "tags": ["{{state.key}}"]
}
""",
            "Push to the ntfy app. The receiver is the topic you subscribed to (pick one nobody can guess - on ntfy.sh anyone who knows it can read it). Your own ntfy server: change the address; with access control add the header Authorization: Bearer {{secret}} and put the token into Secret."),

        new("pushover", "Pushover (push)", "https://api.pushover.net/1/messages.json", "POST", WebhookContentTypes.Form, string.Empty, """
token={{secret}}
user={{receiver.target}}
title={{subject}}
message={{summary}}
priority={{state.urgent}}
""",
            "Push to the Pushover app. Secret = your application's API token, receiver = the user or group key. Critical alerts are sent with high priority."),

        new("gotify", "Gotify (push)", "https://gotify.example.com/message", "POST", WebhookContentTypes.Json, "X-Gotify-Key: {{secret}}", """
{
  "title": "{{subject}}",
  "message": "{{summary}}",
  "priority": {{state.priority}}
}
""",
            "Push to your own Gotify server. Change the address to your server; Secret = an application token."),

        new("telegram", "Telegram (bot)", "https://api.telegram.org/bot{{{secret}}}/sendMessage", "POST", WebhookContentTypes.Json, string.Empty, """
{
  "chat_id": "{{receiver.target}}",
  "text": "{{subject}}\n{{summary}}"
}
""",
            "A message from your Telegram bot. Secret = the bot token from @BotFather, receiver = the chat id (a person has to have started a chat with the bot first)."),

        new("teams", "Microsoft Teams (Workflows)", string.Empty, "POST", WebhookContentTypes.Json, string.Empty, """
{
  "type": "message",
  "attachments": [
    {
      "contentType": "application/vnd.microsoft.card.adaptive",
      "content": {
        "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
        "type": "AdaptiveCard",
        "version": "1.4",
        "body": [
          { "type": "TextBlock", "size": "Medium", "weight": "Bolder", "wrap": true, "text": "{{subject}}" },
          { "type": "TextBlock", "wrap": true, "text": "{{message}}" },
          { "type": "FactSet", "facts": [
            { "title": "State", "value": "{{state.label}}" },
            { "title": "Element", "value": "{{element.path}}" },
            { "title": "Value", "value": "{{sensor.value_with_unit}}" },
            { "title": "Since", "value": "{{problem.since}}" }
          ] }
        ]
      }
    }
  ]
}
""",
            "A card in a Teams channel. In Teams: Workflows > \"Post to a channel when a webhook request is received\", then paste the address the workflow gives you."),

        new("slack", "Slack", string.Empty, "POST", WebhookContentTypes.Json, string.Empty, """
{
  "text": "*{{subject}}*\n{{summary}}"
}
""",
            "A message in a Slack channel. Create an incoming webhook for the channel in Slack and paste its address."),

        new("discord", "Discord", string.Empty, "POST", WebhookContentTypes.Json, string.Empty, """
{
  "content": "**{{subject}}**\n{{summary}}"
}
""",
            "A message in a Discord channel. Channel settings > Integrations > Webhooks, then paste the webhook's address.")
    ];

    public static WebhookPreset? Find(string? key) =>
        string.IsNullOrWhiteSpace(key) ? null : All.FirstOrDefault(preset => string.Equals(preset.Key, key.Trim(), StringComparison.OrdinalIgnoreCase));
}
