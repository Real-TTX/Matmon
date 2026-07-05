using System.Net.Http.Json;
using Matmon.Core.Domain;
using Matmon.Core.Sample;

namespace Matmon.Host.Services;

/// <summary>
/// Primary-only background dispatcher: drains alert transitions from the <see cref="NotificationSpooler"/>,
/// matches them against notification rules, renders the templates and sends via SMTP — with an in-memory
/// retry spooler (exponential-ish backoff) so a transient SMTP failure is retried instead of lost.
/// </summary>
public sealed class NotificationDispatchService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(5);

    // Delay before each retry, indexed by attempt count. Attempt 0 = immediate; length caps total tries.
    private static readonly TimeSpan[] RetryBackoff =
    [
        TimeSpan.Zero,
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(30)
    ];

    private readonly NotificationSpooler _spooler;
    private readonly IMonitoringWorkspaceStore _workspaceStore;
    private readonly INotificationEmailSender _emailSender;
    private readonly ILogger<NotificationDispatchService> _logger;
    private readonly List<PendingDelivery> _pending = [];
    private readonly List<PendingRelay> _pendingRelays = [];
    private readonly HttpClient _relayClient = new() { Timeout = TimeSpan.FromSeconds(15) };

    // Anti-spam / flap suppression: cooldown per (rule, element) + one-recovery-per-episode gating.
    // Owned by the single dispatch loop (not thread-safe by design).
    private readonly NotificationThrottle _throttle = new();

    public NotificationDispatchService(
        NotificationSpooler spooler,
        IMonitoringWorkspaceStore workspaceStore,
        INotificationEmailSender emailSender,
        ILogger<NotificationDispatchService> logger)
    {
        _spooler = spooler;
        _workspaceStore = workspaceStore;
        _emailSender = emailSender;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                DrainEvents();
                await ProcessPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Notification dispatch tick failed");
            }
        }
    }

    private void DrainEvents()
    {
        while (_spooler.TryDequeue(out var notificationEvent))
        {
            try
            {
                _pending.AddRange(BuildDeliveries(notificationEvent));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to build notifications for alert {AlertId}", notificationEvent.AlertId);
            }
        }
    }

    private List<PendingDelivery> BuildDeliveries(AlertNotificationEvent notificationEvent)
    {
        var deliveries = new List<PendingDelivery>();
        var workspace = _workspaceStore.Workspace;
        if (workspace.NotificationRules.Count == 0)
        {
            return deliveries;
        }

        var now = DateTimeOffset.UtcNow;
        var elementsById = _workspaceStore.GetAllElements().ToDictionary(element => element.Id);
        elementsById.TryGetValue(notificationEvent.ElementId, out var element);
        _workspaceStore.GetLatestSensorObservations().TryGetValue(notificationEvent.ElementId, out var latest);
        var alert = workspace.Alerts.FirstOrDefault(candidate => candidate.Id == notificationEvent.AlertId);

        // Resolved once per event: the cloud gateway target for rules that use the Cloud sender (null if relay off).
        var cloudRelay = ResolveCloudRelay();

        foreach (var rule in workspace.NotificationRules)
        {
            if (!rule.Enabled || rule.ChannelKind != NotificationChannelKind.Email)
            {
                continue;
            }

            if (!RuleTargetsElement(rule, notificationEvent.ElementId, elementsById))
            {
                continue;
            }

            if (notificationEvent.Transition == NotificationTransition.Raised)
            {
                if (rule.TriggerStates.Count > 0 && !rule.TriggerStates.Contains(notificationEvent.State))
                {
                    continue;
                }

                // Cooldown: don't re-notify the same rule+element within CooldownMinutes. Combined with
                // the persist-until-ack model, this is what stops a flapping sensor from spamming.
                if (_throttle.IsWithinCooldown(rule.Id, notificationEvent.ElementId, rule.CooldownMinutes, now))
                {
                    _logger.LogDebug("Suppressed notification for rule {Rule} on {Element} — within cooldown", rule.Name, notificationEvent.ElementId);
                    continue;
                }
            }
            else
            {
                // Recovery: only notify a rule that actually sent the raise for this episode. This also
                // prevents flapping from spamming recovery mails (a re-raise that was cooldown-suppressed
                // never marks the episode active, so its recovery is skipped too).
                if (!_throttle.IsEpisodeActive(rule.Id, notificationEvent.ElementId))
                {
                    continue;
                }
            }

            var recipient = ResolveRecipient(rule, workspace);
            if (string.IsNullOrWhiteSpace(recipient))
            {
                _logger.LogWarning("Notification rule {Rule} matched but has no recipient", rule.Name);
                continue;
            }

            // A rule's sender decides the transport: a Cloud sender delivers via the Matmon.Cloud gateway,
            // anything else (or no sender) via local SMTP. Recipient + templates are identical either way.
            var sender = rule.SenderId is Guid senderId
                ? workspace.NotificationSenders.FirstOrDefault(candidate => candidate.Id == senderId)
                : null;
            var isCloud = sender is { Kind: NotificationEndpointKind.Cloud };

            EmailNotificationSettings? smtp = null;
            if (isCloud)
            {
                if (!sender!.Enabled || cloudRelay is null)
                {
                    _logger.LogWarning("Notification rule {Rule} uses the Cloud sender but cloud relay is disabled or the link is down", rule.Name);
                    continue;
                }
            }
            else
            {
                smtp = ResolveSmtp(rule, workspace);
                if (smtp is null || string.IsNullOrWhiteSpace(smtp.SmtpHost))
                {
                    _logger.LogWarning("Notification rule {Rule} matched but has no usable SMTP sender", rule.Name);
                    continue;
                }
            }

            var context = BuildContext(notificationEvent, rule, element, alert, latest, elementsById);
            var subject = NotificationTemplateRenderer.RenderText(rule.SubjectTemplate, context, NotificationTemplateCatalog.DefaultSubjectTemplate);
            var textBody = NotificationTemplateRenderer.RenderText(rule.TextTemplate, context, NotificationTemplateCatalog.DefaultTextTemplate);
            var htmlBody = NotificationTemplateRenderer.RenderHtml(rule.HtmlTemplate, context, NotificationTemplateCatalog.DefaultHtmlTemplate);

            if (isCloud)
            {
                _pendingRelays.Add(new PendingRelay
                {
                    Url = cloudRelay!.Value.Url,
                    Token = cloudRelay.Value.Token,
                    Recipient = recipient,
                    Subject = subject,
                    TextBody = textBody,
                    HtmlBody = htmlBody,
                    NextAttemptUtc = DateTimeOffset.UtcNow
                });
            }
            else
            {
                deliveries.Add(new PendingDelivery
                {
                    Smtp = smtp!,
                    Recipient = recipient,
                    Subject = subject,
                    TextBody = textBody,
                    HtmlBody = htmlBody,
                    RuleName = rule.Name
                });
            }

            // Update flap/cooldown state now that a notification is queued for this (rule, element).
            if (notificationEvent.Transition == NotificationTransition.Raised)
            {
                _throttle.MarkRaised(rule.Id, notificationEvent.ElementId, now);
            }
            else
            {
                _throttle.MarkRecovered(rule.Id, notificationEvent.ElementId);
            }
        }

        return deliveries;
    }

    /// <summary>The Matmon.Cloud gateway target (notify URL + token) when cloud alert-relay is enabled and the
    /// link is connected; null otherwise. Rules whose sender is a Cloud sender deliver through this.</summary>
    private (string Url, string Token)? ResolveCloudRelay()
    {
        var settings = _workspaceStore.GetCloudConnectionSettings();
        if (!settings.RelayAlerts || !settings.Configured || !settings.Enabled ||
            string.IsNullOrWhiteSpace(settings.Url) || string.IsNullOrWhiteSpace(settings.InstanceId))
        {
            return null;
        }

        var token = _workspaceStore.GetCloudConnectionToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var baseUrl = settings.Url!.Trim().TrimEnd('/');
        return ($"{baseUrl}/api/instances/{settings.InstanceId!.Trim()}/notify", token);
    }

    private async Task ProcessPendingAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        for (var i = _pending.Count - 1; i >= 0; i--)
        {
            var delivery = _pending[i];
            if (delivery.NextAttemptUtc > now)
            {
                continue;
            }

            try
            {
                await _emailSender.SendAsync(delivery.Smtp, delivery.Recipient, delivery.Subject, delivery.TextBody, delivery.HtmlBody, cancellationToken);
                _pending.RemoveAt(i);
                _logger.LogInformation("Notification sent to {Recipient} for rule {Rule}", delivery.Recipient, delivery.RuleName);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                delivery.Attempt++;
                if (delivery.Attempt >= RetryBackoff.Length)
                {
                    _pending.RemoveAt(i);
                    _logger.LogError(ex, "Notification to {Recipient} for rule {Rule} failed after {Attempts} attempts — giving up",
                        delivery.Recipient, delivery.RuleName, delivery.Attempt);
                }
                else
                {
                    delivery.NextAttemptUtc = now + RetryBackoff[delivery.Attempt];
                    _logger.LogWarning(ex, "Notification to {Recipient} for rule {Rule} failed (attempt {Attempt}); retrying in {Delay}",
                        delivery.Recipient, delivery.RuleName, delivery.Attempt, RetryBackoff[delivery.Attempt]);
                }
            }
        }

        await ProcessRelaysAsync(now, cancellationToken);
    }

    private async Task ProcessRelaysAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        for (var i = _pendingRelays.Count - 1; i >= 0; i--)
        {
            var relay = _pendingRelays[i];
            if (relay.NextAttemptUtc > now)
            {
                continue;
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, relay.Url)
                {
                    Content = JsonContent.Create(new RelayBody("email", relay.Recipient, relay.Subject, relay.TextBody, relay.HtmlBody))
                };
                request.Headers.TryAddWithoutValidation("X-Matmon-Instance-Token", relay.Token);

                using var response = await _relayClient.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();
                _pendingRelays.RemoveAt(i);
                _logger.LogInformation("Alert relayed to Matmon.Cloud gateway -> {Recipient}", relay.Recipient);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                relay.Attempt++;
                if (relay.Attempt >= RetryBackoff.Length)
                {
                    _pendingRelays.RemoveAt(i);
                    _logger.LogError(ex, "Cloud relay to {Recipient} failed after {Attempts} attempts — giving up", relay.Recipient, relay.Attempt);
                }
                else
                {
                    relay.NextAttemptUtc = now + RetryBackoff[relay.Attempt];
                    _logger.LogWarning(ex, "Cloud relay to {Recipient} failed (attempt {Attempt}); retrying in {Delay}", relay.Recipient, relay.Attempt, RetryBackoff[relay.Attempt]);
                }
            }
        }
    }

    private static bool RuleTargetsElement(NotificationRule rule, Guid elementId, IReadOnlyDictionary<Guid, MonitoringElement> elementsById)
    {
        if (rule.TargetElementId is not Guid targetId)
        {
            return true; // no target = all elements
        }

        if (targetId == elementId)
        {
            return true;
        }

        if (!rule.IncludeDescendants)
        {
            return false;
        }

        var current = elementId;
        var guard = 0;
        while (elementsById.TryGetValue(current, out var element) && element.ParentId is Guid parent && guard++ < 256)
        {
            if (parent == targetId)
            {
                return true;
            }

            current = parent;
        }

        return false;
    }

    private static EmailNotificationSettings? ResolveSmtp(NotificationRule rule, MonitoringWorkspaceSnapshot workspace)
    {
        if (rule.SenderId is Guid senderId)
        {
            var sender = workspace.NotificationSenders.FirstOrDefault(candidate => candidate.Id == senderId && candidate.Enabled);
            if (sender is { Kind: NotificationEndpointKind.Email })
            {
                return sender.Email;
            }
        }

        var fallback = workspace.NotificationConfiguration.Email;
        return string.IsNullOrWhiteSpace(fallback.SmtpHost) ? null : fallback;
    }

    private static string ResolveRecipient(NotificationRule rule, MonitoringWorkspaceSnapshot workspace)
    {
        if (!string.IsNullOrWhiteSpace(rule.Recipient))
        {
            return rule.Recipient.Trim();
        }

        if (rule.ReceiverId is Guid receiverId)
        {
            var receiver = workspace.NotificationReceivers.FirstOrDefault(candidate => candidate.Id == receiverId && candidate.Enabled);
            if (receiver is not null)
            {
                return receiver.Target;
            }
        }

        return string.Empty;
    }

    private static NotificationTemplateContext BuildContext(
        AlertNotificationEvent notificationEvent,
        NotificationRule? rule,
        MonitoringElement? element,
        MonitoringAlert? alert,
        SensorObservation? latest,
        IReadOnlyDictionary<Guid, MonitoringElement> elementsById)
    {
        var now = DateTimeOffset.UtcNow;
        var context = new NotificationTemplateContext();
        var sensor = element as SensorElement;
        var isRecovery = notificationEvent.Transition != NotificationTransition.Raised;
        var stateLabel = isRecovery ? "Recovered" : MonitoringStatePresentation.Label(notificationEvent.State);
        var stateKey = isRecovery ? "recovered" : MonitoringStatePresentation.Key(notificationEvent.State);
        var elementPath = alert?.ElementPath ?? BuildElementPath(notificationEvent.ElementId, elementsById);
        var since = alert?.FirstSeenUtc ?? notificationEvent.TimestampUtc;

        context.SetValue("rule.name", rule?.Name ?? "Matmon.Cloud relay");
        context.SetValue("state.label", stateLabel);
        context.SetValue("state.key", stateKey);
        context.SetValue("message", notificationEvent.Message);
        context.SetValue("rendered_at", now);

        context.SetValue("element.name", element?.Name ?? alert?.ElementName);
        context.SetValue("element.path", elementPath);
        context.SetValue("element.kind", (element?.Kind ?? alert?.ElementKind)?.ToString());
        context.SetValue("element.details", sensor?.Target);

        context.SetValue("sensor.name", element?.Name);
        context.SetValue("sensor.type", sensor?.SensorTypeKey);
        context.SetValue("sensor.target", sensor?.Target);

        var defaultChannel = ResolveDefaultChannel(latest);
        context.SetValue("sensor.value", latest?.Value);
        context.SetValue("sensor.unit", defaultChannel?.Unit);
        context.SetValue("sensor.value_with_unit", latest?.Value, defaultChannel?.Unit);
        context.SetValue("sensor.last_check", latest?.TimestampUtc);
        context.SetValue("channels.summary", BuildChannelSummary(latest));

        context.SetValue("problem.since", since);
        context.SetValue("problem.age", now - since);
        context.SetValue("alert.first_seen", alert?.FirstSeenUtc);
        context.SetValue("alert.last_seen", alert?.LastSeenUtc);
        context.SetValue("alert.acknowledged_at", alert?.AcknowledgedUtc);
        context.SetValue("alert.acknowledged_by", alert?.AcknowledgedBy);
        context.SetValue("alert.resolved_at", alert?.ResolvedUtc);

        var probe = ResolveProbe(notificationEvent.ElementId, elementsById);
        context.SetValue("probe.name", probe?.Name);
        context.SetValue("probe.id", probe?.ProbeId);

        context.SetRawHtml("state.badge_html", BuildStateBadgeHtml(stateLabel, notificationEvent.State));
        context.SetRawHtml("channels.table_html", BuildChannelTableHtml(latest));

        return context;
    }

    private static SensorChannelValue? ResolveDefaultChannel(SensorObservation? latest)
    {
        if (latest?.Channels is not { Count: > 0 } channels)
        {
            return null;
        }

        return channels.FirstOrDefault(channel => !channel.IsVirtual &&
                   !string.IsNullOrWhiteSpace(latest.DefaultChannelKey) &&
                   string.Equals(channel.Key, latest.DefaultChannelKey, StringComparison.OrdinalIgnoreCase))
               ?? channels.FirstOrDefault(channel => channel.IsDefault && !channel.IsVirtual)
               ?? channels.FirstOrDefault(channel => !channel.IsVirtual);
    }

    private static string BuildChannelSummary(SensorObservation? latest)
    {
        if (latest?.Channels is not { Count: > 0 } channels)
        {
            return string.Empty;
        }

        return string.Join(" · ", channels
            .Where(channel => !channel.IsVirtual && channel.Value.HasValue)
            .Take(8)
            .Select(channel => $"{ChannelLabel(channel)}: {FormatChannelValue(channel)}"));
    }

    private static string BuildChannelTableHtml(SensorObservation? latest)
    {
        if (latest?.Channels is not { Count: > 0 } channels)
        {
            return string.Empty;
        }

        var rows = channels
            .Where(channel => !channel.IsVirtual && channel.Value.HasValue)
            .Take(20)
            .Select(channel =>
                "<tr><td style=\"padding:4px 10px 4px 0;color:#6b7280;\">" +
                System.Net.WebUtility.HtmlEncode(ChannelLabel(channel)) +
                "</td><td style=\"padding:4px 0;\">" +
                System.Net.WebUtility.HtmlEncode(FormatChannelValue(channel)) +
                "</td></tr>");

        var body = string.Concat(rows);
        return string.IsNullOrEmpty(body)
            ? string.Empty
            : $"<table style=\"border-collapse:collapse;\">{body}</table>";
    }

    private static string BuildStateBadgeHtml(string stateLabel, SensorState state)
    {
        var color = state switch
        {
            SensorState.Critical => "#dc2626",
            SensorState.Warning => "#d97706",
            SensorState.Healthy => "#16a34a",
            _ => "#6b7280"
        };

        return $"<span style=\"display:inline-block;padding:4px 12px;border-radius:999px;background:{color};color:#fff;font-weight:600;font-size:13px;\">{System.Net.WebUtility.HtmlEncode(stateLabel)}</span>";
    }

    private static string ChannelLabel(SensorChannelValue channel) =>
        string.IsNullOrWhiteSpace(channel.Label) ? channel.Key : channel.Label;

    private static string FormatChannelValue(SensorChannelValue channel)
    {
        var text = channel.Value?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        return string.IsNullOrWhiteSpace(channel.Unit) ? text : $"{text} {channel.Unit}";
    }

    private static string BuildElementPath(Guid elementId, IReadOnlyDictionary<Guid, MonitoringElement> elementsById)
    {
        var names = new List<string>();
        var current = elementId;
        var guard = 0;
        while (elementsById.TryGetValue(current, out var element) && guard++ < 256)
        {
            names.Add(element.Name);
            if (element.ParentId is not Guid parent)
            {
                break;
            }

            current = parent;
        }

        names.Reverse();
        return string.Join(" / ", names);
    }

    private static ProbeElement? ResolveProbe(Guid elementId, IReadOnlyDictionary<Guid, MonitoringElement> elementsById)
    {
        var current = elementId;
        var guard = 0;
        while (elementsById.TryGetValue(current, out var element) && guard++ < 256)
        {
            if (element is ProbeElement probe)
            {
                return probe;
            }

            if (element.ParentId is not Guid parent)
            {
                break;
            }

            current = parent;
        }

        return null;
    }

    private sealed class PendingDelivery
    {
        public required EmailNotificationSettings Smtp { get; init; }
        public required string Recipient { get; init; }
        public required string Subject { get; init; }
        public required string TextBody { get; init; }
        public required string HtmlBody { get; init; }
        public required string RuleName { get; init; }
        public int Attempt { get; set; }
        public DateTimeOffset NextAttemptUtc { get; set; }
    }

    private sealed class PendingRelay
    {
        public required string Url { get; init; }
        public required string Token { get; init; }
        public required string Recipient { get; init; }
        public required string Subject { get; init; }
        public required string TextBody { get; init; }
        public required string HtmlBody { get; init; }
        public int Attempt { get; set; }
        public DateTimeOffset NextAttemptUtc { get; set; }
    }

    // Mirrors Matmon.Cloud's NotificationRelayRequest (Channel, To, Subject, Text, Html).
    private sealed record RelayBody(string Channel, string To, string Subject, string? Text, string? Html);
}
