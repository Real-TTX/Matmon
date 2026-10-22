using System.Net;
using System.Net.Http.Json;
using System.Text;
using Matmon.Core.Domain;
using Matmon.Core.Sample;

namespace Matmon.Host.Services;

public enum NotificationTransport
{
    Email = 0,
    Webhook = 1,
    Cloud = 2
}

/// <summary>One notification ready to go out: the channel, what to send and where. Built by
/// <see cref="NotificationDeliveryFactory"/>, sent by <see cref="NotificationTransportClient"/>, retried by the dispatcher.</summary>
public sealed class NotificationDelivery
{
    public required NotificationTransport Transport { get; init; }

    public required string RuleName { get; init; }

    /// <summary>Who or where, as it may appear in a log or a status line - addresses, a receiver's name, a webhook's
    /// host. Never a token.</summary>
    public required string RecipientLabel { get; init; }

    public required RenderedNotification Content { get; init; }

    /// <summary>The e-mail addresses (Email and Cloud).</summary>
    public string Recipient { get; init; } = string.Empty;

    public EmailNotificationSettings? Smtp { get; init; }

    public WebhookRequest? Webhook { get; init; }

    public int TimeoutSeconds { get; init; } = 10;

    public string? CloudUrl { get; init; }

    public string? CloudToken { get; init; }

    /// <summary>"escalation 2" and the like - added to the log line.</summary>
    public string? Label { get; init; }

    public int Attempt { get; set; }

    public DateTimeOffset NextAttemptUtc { get; set; }
}

/// <summary>A delivery failed. <see cref="Retryable"/> = false for an answer that will not change by asking again
/// (a 4xx from a webhook: wrong token, wrong address, a body the service rejects) - retrying those only delays the
/// error by 40 minutes and hides it in the log.</summary>
public sealed class NotificationDeliveryException(string message, bool retryable, Exception? inner = null)
    : Exception(message, inner)
{
    public bool Retryable { get; } = retryable;
}

/// <summary>The rule's (or an escalation step's) sender and receiver, with every template - what a delivery is built from.</summary>
public sealed record NotificationRoute(
    string RuleName,
    Guid? SenderId,
    Guid? ReceiverId,
    string? LegacyRecipient,
    string? SubjectTemplate,
    string? TextTemplate,
    string? HtmlTemplate,
    string? ShortTemplate)
{
    public static NotificationRoute For(NotificationRule rule) => new(
        rule.Name, rule.SenderId, rule.ReceiverId, rule.Recipient,
        rule.SubjectTemplate, rule.TextTemplate, rule.HtmlTemplate, rule.ShortTemplate);
}

/// <summary>
/// Decides how a notification goes out - the sender picks the channel (no sender = the workspace's default SMTP, an
/// e-mail sender = its SMTP, a webhook sender = its HTTP request, the Matmon Cloud sender = the cloud gateway) - and
/// builds the delivery, or says in words why it cannot (the dispatcher logs it, the rule editor's test button shows it).
/// The receiver is the ONE source of the recipient when a rule names one: the legacy <c>Recipient</c> copy on the rule
/// is only for rules that predate receivers (it used to win, so a rule switched to "All users" kept mailing the
/// receiver it had before).
/// </summary>
public sealed class NotificationDeliveryFactory
{
    private readonly IMonitoringWorkspaceStore _store;

    public NotificationDeliveryFactory(IMonitoringWorkspaceStore store)
    {
        _store = store;
    }

    public (NotificationDelivery? Delivery, string? Problem) Create(
        NotificationRoute route,
        NotificationTemplateContext context,
        MonitoringWorkspaceSnapshot workspace,
        string? label = null)
    {
        var sender = route.SenderId is Guid senderId
            ? workspace.NotificationSenders.FirstOrDefault(candidate => candidate.Id == senderId)
            : null;
        if (route.SenderId is not null && sender is null)
        {
            return (null, "its sender no longer exists");
        }

        if (sender is { Enabled: false })
        {
            return (null, $"the sender '{sender.Name}' is switched off");
        }

        var receiver = ResolveReceiver(route, workspace);
        if (receiver.Problem is not null)
        {
            return (null, receiver.Problem);
        }

        var content = NotificationRendering.Render(context, route.SubjectTemplate, route.TextTemplate, route.HtmlTemplate, route.ShortTemplate);

        switch (sender?.Kind)
        {
            case NotificationEndpointKind.Webhook:
            {
                WebhookRequest request;
                try
                {
                    request = WebhookRequestBuilder.Build(sender.Webhook, context.Values, receiver.Target);
                }
                catch (InvalidOperationException exception)
                {
                    return (null, $"the webhook '{sender.Name}' cannot be built: {exception.Message}");
                }

                return (new NotificationDelivery
                {
                    Transport = NotificationTransport.Webhook,
                    RuleName = route.RuleName,
                    RecipientLabel = $"{sender.Name} ({request.SafeTarget})",
                    Content = content,
                    Webhook = request,
                    TimeoutSeconds = sender.Webhook.TimeoutSeconds is > 0 ? Math.Min(sender.Webhook.TimeoutSeconds.Value, 60) : 10,
                    Label = label,
                    NextAttemptUtc = DateTimeOffset.UtcNow
                }, null);
            }

            case NotificationEndpointKind.Cloud:
            {
                if (receiver.Kind != NotificationEndpointKind.Email || string.IsNullOrWhiteSpace(receiver.Target))
                {
                    return (null, "the Matmon Cloud sender delivers e-mail - pick a receiver with an e-mail address");
                }

                var relay = ResolveCloudRelay();
                if (relay is null)
                {
                    return (null, "it uses the Matmon Cloud sender, but the cloud relay is off or the cloud link is down (System > Cloud)");
                }

                return (new NotificationDelivery
                {
                    Transport = NotificationTransport.Cloud,
                    RuleName = route.RuleName,
                    RecipientLabel = receiver.Target,
                    Recipient = receiver.Target,
                    Content = content,
                    CloudUrl = relay.Value.Url,
                    CloudToken = relay.Value.Token,
                    Label = label,
                    NextAttemptUtc = DateTimeOffset.UtcNow
                }, null);
            }

            default:
            {
                if (receiver.Kind != NotificationEndpointKind.Email || string.IsNullOrWhiteSpace(receiver.Target))
                {
                    return (null, string.IsNullOrWhiteSpace(receiver.Target)
                        ? "it has no recipient"
                        : "an e-mail sender needs a receiver with an e-mail address (a topic or user key belongs to a webhook sender)");
                }

                var smtp = sender is { Kind: NotificationEndpointKind.Email }
                    ? sender.Email
                    : workspace.NotificationConfiguration.Email;
                if (smtp is null || string.IsNullOrWhiteSpace(smtp.SmtpHost))
                {
                    return (null, sender is null
                        ? "it has no sender and no default SMTP server is set up"
                        : $"the sender '{sender.Name}' has no SMTP host");
                }

                return (new NotificationDelivery
                {
                    Transport = NotificationTransport.Email,
                    RuleName = route.RuleName,
                    RecipientLabel = receiver.Target,
                    Recipient = receiver.Target,
                    Content = content,
                    Smtp = smtp,
                    Label = label,
                    NextAttemptUtc = DateTimeOffset.UtcNow
                }, null);
            }
        }
    }

    private (string Target, NotificationEndpointKind Kind, string? Problem) ResolveReceiver(NotificationRoute route, MonitoringWorkspaceSnapshot workspace)
    {
        if (route.ReceiverId is Guid receiverId)
        {
            if (NotificationReceiverDefaults.IsBuiltIn(receiverId))
            {
                var addresses = _store.ResolveBuiltInRecipients(receiverId);
                return string.IsNullOrWhiteSpace(addresses)
                    ? (string.Empty, NotificationEndpointKind.Email, "its receiver group has no enabled user with an e-mail address")
                    : (addresses, NotificationEndpointKind.Email, null);
            }

            var receiver = workspace.NotificationReceivers.FirstOrDefault(candidate => candidate.Id == receiverId);
            if (receiver is null)
            {
                return (string.Empty, NotificationEndpointKind.Email, "its receiver no longer exists");
            }

            if (!receiver.Enabled)
            {
                return (string.Empty, receiver.Kind, $"the receiver '{receiver.Name}' is switched off");
            }

            return (receiver.Target?.Trim() ?? string.Empty, receiver.Kind, null);
        }

        return (route.LegacyRecipient?.Trim() ?? string.Empty, NotificationEndpointKind.Email, null);
    }

    /// <summary>The Matmon.Cloud gateway target (notify URL + token) when cloud alert-relay is enabled and the link is
    /// connected; null otherwise.</summary>
    private (string Url, string Token)? ResolveCloudRelay()
    {
        var settings = _store.GetCloudConnectionSettings();
        if (!settings.RelayAlerts || !settings.Configured || !settings.Enabled ||
            string.IsNullOrWhiteSpace(settings.Url) || string.IsNullOrWhiteSpace(settings.InstanceId))
        {
            return null;
        }

        var token = _store.GetCloudConnectionToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var baseUrl = settings.Url!.Trim().TrimEnd('/');
        return ($"{baseUrl}/api/instances/{settings.InstanceId!.Trim()}/notify", token);
    }
}

/// <summary>Sends a <see cref="NotificationDelivery"/> once - the dispatcher owns the retries.</summary>
public sealed class NotificationTransportClient
{
    public const string HttpClientName = "matmon-notifications";

    private readonly INotificationEmailSender _emailSender;
    private readonly IHttpClientFactory _httpClientFactory;

    public NotificationTransportClient(INotificationEmailSender emailSender, IHttpClientFactory httpClientFactory)
    {
        _emailSender = emailSender;
        _httpClientFactory = httpClientFactory;
    }

    /// <exception cref="NotificationDeliveryException">It did not go out; <see cref="NotificationDeliveryException.Retryable"/>
    /// says whether asking again can help.</exception>
    public async Task SendAsync(NotificationDelivery delivery, CancellationToken cancellationToken)
    {
        switch (delivery.Transport)
        {
            case NotificationTransport.Email:
                try
                {
                    await _emailSender.SendAsync(delivery.Smtp!, delivery.Recipient, delivery.Content.Subject, delivery.Content.Text, delivery.Content.Html, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    throw new NotificationDeliveryException(exception.Message, retryable: true, exception);
                }

                break;

            case NotificationTransport.Webhook:
                await SendWebhookAsync(delivery, cancellationToken);
                break;

            case NotificationTransport.Cloud:
                await SendCloudAsync(delivery, cancellationToken);
                break;
        }
    }

    private async Task SendWebhookAsync(NotificationDelivery delivery, CancellationToken cancellationToken)
    {
        var webhook = delivery.Webhook!;
        using var request = new HttpRequestMessage(new HttpMethod(webhook.Method), webhook.Url);
        string? contentTypeOverride = null;
        foreach (var header in webhook.Headers)
        {
            if (string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                contentTypeOverride = header.Value;
                continue;
            }

            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (webhook.Body is not null)
        {
            request.Content = new StringContent(webhook.Body, Encoding.UTF8);
            request.Content.Headers.Remove("Content-Type");
            request.Content.Headers.TryAddWithoutValidation("Content-Type", contentTypeOverride ?? $"{webhook.ContentType}; charset=utf-8");
        }

        await SendHttpAsync(request, delivery.TimeoutSeconds, "the webhook", cancellationToken);
    }

    private async Task SendCloudAsync(NotificationDelivery delivery, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, delivery.CloudUrl)
        {
            Content = JsonContent.Create(new CloudRelayBody("email", delivery.Recipient, delivery.Content.Subject, delivery.Content.Text, delivery.Content.Html))
        };
        request.Headers.TryAddWithoutValidation("X-Matmon-Instance-Token", delivery.CloudToken);
        await SendHttpAsync(request, 15, "the Matmon Cloud gateway", cancellationToken);
    }

    private async Task SendHttpAsync(HttpRequestMessage request, int timeoutSeconds, string what, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 60)));

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new NotificationDeliveryException($"{what} did not answer within {timeoutSeconds} s", retryable: true);
        }
        catch (HttpRequestException exception)
        {
            throw new NotificationDeliveryException($"{what} could not be reached: {exception.Message}", retryable: true, exception);
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                return;
            }

            var status = (int)response.StatusCode;
            var detail = await ReadSnippetAsync(response, cancellationToken);
            var retryable = status >= 500 || response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || status == 425;
            throw new NotificationDeliveryException(
                $"{what} answered {status} {response.ReasonPhrase}{(string.IsNullOrWhiteSpace(detail) ? string.Empty : $": {detail}")}",
                retryable);
        }
    }

    /// <summary>The start of an error answer - services say what is wrong ("invalid token", "topic too long").</summary>
    private static async Task<string> ReadSnippetAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            text = string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            return text.Length <= 200 ? text : text[..200] + "…";
        }
        catch
        {
            return string.Empty;
        }
    }

    // Mirrors Matmon.Cloud's NotificationRelayRequest (Channel, To, Subject, Text, Html).
    private sealed record CloudRelayBody(string Channel, string To, string Subject, string? Text, string? Html);
}
