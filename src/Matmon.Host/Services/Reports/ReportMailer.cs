using System.Net.Http.Json;
using Matmon.Core.Domain;

namespace Matmon.Host.Services.Reports;

/// <summary>How a send ended, in words.</summary>
public readonly record struct ReportSendResult(bool Sent, string Message);

/// <summary>
/// Sends a report e-mail - the scheduled summary report and the saved reports alike: through the chosen sender (an SMTP
/// endpoint, or the Matmon Cloud sender, which relays through the cloud gateway), else the workspace default SMTP; to the
/// chosen receiver (a built-in user group expands to its members' addresses), else the typed addresses.
/// </summary>
public sealed class ReportMailer(
    INotificationEmailSender emailSender,
    IMonitoringWorkspaceStore store,
    ILogger<ReportMailer> logger)
{
    private static readonly HttpClient CloudHttp = new() { Timeout = TimeSpan.FromSeconds(60) };

    public async Task<ReportSendResult> SendAsync(
        Guid? senderId,
        Guid? receiverId,
        string? recipients,
        string subject,
        string text,
        string html,
        IReadOnlyList<EmailAttachment> attachments,
        CancellationToken cancellationToken)
    {
        var to = ResolveRecipients(receiverId, recipients);
        if (string.IsNullOrWhiteSpace(to))
        {
            return new ReportSendResult(false, "Not sent: nobody to send it to (the receiver has no address, or no addresses are set).");
        }

        var chosen = senderId is { } id ? store.Workspace.NotificationSenders.FirstOrDefault(sender => sender.Id == id) : null;
        if (chosen is { Kind: NotificationEndpointKind.Cloud })
        {
            return await SendViaCloudAsync(to, subject, text, html, attachments, cancellationToken);
        }

        var smtp = ResolveSmtp(chosen);
        if (smtp is null)
        {
            return new ReportSendResult(false, "Not sent: no e-mail sender is set up (Notifications > Senders).");
        }

        try
        {
            await emailSender.SendAsync(smtp, to, subject, text, html, cancellationToken, attachments.Count == 0 ? null : attachments);
            return new ReportSendResult(true, $"Sent to {to}.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(exception, "Report e-mail to {Recipients} failed", to);
            return new ReportSendResult(false, $"Not sent: {exception.Message}");
        }
    }

    /// <summary>Recipient(s): the chosen receiver's target (a built-in user group = its members), else the typed list.</summary>
    public string ResolveRecipients(Guid? receiverId, string? recipients)
    {
        if (receiverId is { } id)
        {
            if (NotificationReceiverDefaults.IsBuiltIn(id))
            {
                return store.ResolveBuiltInRecipients(id);
            }

            var receiver = store.Workspace.NotificationReceivers.FirstOrDefault(candidate => candidate.Id == id);
            if (receiver is not null && !string.IsNullOrWhiteSpace(receiver.Target))
            {
                return receiver.Target;
            }
        }

        return recipients ?? string.Empty;
    }

    private EmailNotificationSettings? ResolveSmtp(NotificationSender? chosen)
    {
        if (chosen is { Kind: NotificationEndpointKind.Email } && !string.IsNullOrWhiteSpace(chosen.Email.SmtpHost))
        {
            return chosen.Email;
        }

        var workspace = store.Workspace;
        var configured = workspace.NotificationConfiguration.Email;
        if (!string.IsNullOrWhiteSpace(configured.SmtpHost))
        {
            return configured;
        }

        return workspace.NotificationSenders.FirstOrDefault(candidate =>
            candidate.Enabled && candidate.Kind == NotificationEndpointKind.Email && !string.IsNullOrWhiteSpace(candidate.Email.SmtpHost))?.Email;
    }

    /// <summary>Relays through the connected Matmon.Cloud (POST /api/instances/{id}/notify, instance-token auth), which
    /// spools and retries it like every cloud mail. The gateway takes ONE attachment - the first (the PDF) goes along.</summary>
    private async Task<ReportSendResult> SendViaCloudAsync(string to, string subject, string text, string html, IReadOnlyList<EmailAttachment> attachments, CancellationToken cancellationToken)
    {
        var cloud = store.GetCloudConnectionSettings();
        if (!cloud.Configured || string.IsNullOrWhiteSpace(cloud.Url) || string.IsNullOrWhiteSpace(cloud.InstanceId))
        {
            return new ReportSendResult(false, "Not sent: the Matmon Cloud sender is chosen but the cloud link is not set up.");
        }

        var token = store.GetCloudConnectionToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            return new ReportSendResult(false, "Not sent: the Matmon Cloud sender is chosen but no cloud token is stored.");
        }

        var url = $"{cloud.Url!.Trim().TrimEnd('/')}/api/instances/{cloud.InstanceId!.Trim()}/notify";
        var first = attachments.FirstOrDefault();
        object? attachment = first is null ? null : new { FileName = first.FileName, ContentType = first.ContentType, Content = first.Content };
        var body = new { Channel = "email", To = to, Subject = subject, Text = text, Html = html, Attachment = attachment };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
            request.Headers.TryAddWithoutValidation("X-Matmon-Instance-Token", token);
            using var response = await CloudHttp.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new ReportSendResult(false, $"Not sent: Matmon Cloud answered {(int)response.StatusCode}.");
            }

            var note = attachments.Count > 1 ? $" (through Matmon Cloud, which takes one attachment: {first!.FileName})" : " (through Matmon Cloud)";
            return new ReportSendResult(true, $"Sent to {to}{note}.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(exception, "Report relay to Matmon.Cloud failed");
            return new ReportSendResult(false, $"Not sent: Matmon Cloud could not be reached ({exception.Message}).");
        }
    }
}
