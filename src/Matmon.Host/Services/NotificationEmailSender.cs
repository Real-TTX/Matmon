using MailKit.Net.Smtp;
using MailKit.Security;
using Matmon.Core.Domain;
using MimeKit;

namespace Matmon.Host.Services;

public interface INotificationEmailSender
{
    Task SendAsync(
        EmailNotificationSettings settings,
        string toAddress,
        string subject,
        string textBody,
        string htmlBody,
        CancellationToken cancellationToken);
}

public sealed class MailKitEmailSender : INotificationEmailSender
{
    public async Task SendAsync(
        EmailNotificationSettings settings,
        string toAddress,
        string subject,
        string textBody,
        string htmlBody,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(settings.SmtpHost))
        {
            throw new InvalidOperationException("SMTP host is not configured.");
        }

        if (string.IsNullOrWhiteSpace(toAddress))
        {
            throw new InvalidOperationException("Notification recipient is empty.");
        }

        var message = new MimeMessage();
        var fromAddress = string.IsNullOrWhiteSpace(settings.SenderEmail)
            ? (settings.Username ?? "matmon@localhost")
            : settings.SenderEmail;
        message.From.Add(new MailboxAddress(
            string.IsNullOrWhiteSpace(settings.SenderName) ? "Matmon" : settings.SenderName,
            fromAddress));

        foreach (var recipient in toAddress.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            message.To.Add(MailboxAddress.Parse(recipient));
        }

        if (message.To.Count == 0)
        {
            throw new InvalidOperationException("Notification recipient is empty.");
        }

        message.Subject = subject ?? string.Empty;

        var builder = new BodyBuilder
        {
            TextBody = string.IsNullOrWhiteSpace(textBody) ? null : textBody,
            HtmlBody = string.IsNullOrWhiteSpace(htmlBody) ? null : htmlBody
        };
        if (builder.TextBody is null && builder.HtmlBody is null)
        {
            builder.TextBody = string.IsNullOrWhiteSpace(subject) ? "(no content)" : subject;
        }

        message.Body = builder.ToMessageBody();

        var port = settings.SmtpPort ?? (settings.UseSsl ? 587 : 25);
        var socketOptions = port switch
        {
            465 => SecureSocketOptions.SslOnConnect,
            _ => settings.UseSsl ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto
        };

        using var client = new SmtpClient();
        await client.ConnectAsync(settings.SmtpHost, port, socketOptions, cancellationToken);
        if (!string.IsNullOrWhiteSpace(settings.Username))
        {
            await client.AuthenticateAsync(settings.Username, settings.Password ?? string.Empty, cancellationToken);
        }

        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(quit: true, cancellationToken);
    }
}
