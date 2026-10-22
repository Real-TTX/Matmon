using Matmon.Core.Domain;
using Matmon.Core.Sample;

namespace Matmon.Host.Services;

/// <summary>
/// Primary-only background dispatcher: drains alert transitions from the <see cref="NotificationSpooler"/>,
/// matches them against notification rules and hands each match to <see cref="NotificationDeliveryFactory"/> (the
/// rule's sender picks the channel: SMTP, a webhook or the Matmon Cloud gateway) - with an in-memory retry spooler
/// (exponential-ish backoff) so a transient failure is retried instead of lost, and a refusal (a 4xx) is reported at once.
/// </summary>
public sealed class NotificationDispatchService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(5);

    // Escalation is about minutes, not seconds - looking every 15 s is plenty and keeps the element clone off most ticks.
    private static readonly TimeSpan EscalationInterval = TimeSpan.FromSeconds(15);

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
    private readonly NotificationDeliveryFactory _deliveryFactory;
    private readonly NotificationTransportClient _transport;
    private readonly ILogger<NotificationDispatchService> _logger;
    private readonly List<NotificationDelivery> _pending = [];

    // Anti-spam / flap suppression: cooldown per (rule, element) + one-recovery-per-episode gating.
    // Owned by the single dispatch loop (not thread-safe by design).
    private readonly NotificationThrottle _throttle = new();

    private DateTimeOffset _nextEscalationCheck = DateTimeOffset.MinValue;

    public NotificationDispatchService(
        NotificationSpooler spooler,
        IMonitoringWorkspaceStore workspaceStore,
        NotificationDeliveryFactory deliveryFactory,
        NotificationTransportClient transport,
        ILogger<NotificationDispatchService> logger)
    {
        _spooler = spooler;
        _workspaceStore = workspaceStore;
        _deliveryFactory = deliveryFactory;
        _transport = transport;
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
                ProcessEscalations();
                await ProcessPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Includes a network-send timeout (TaskCanceledException) - log + keep the dispatch loop alive.
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

    private List<NotificationDelivery> BuildDeliveries(AlertNotificationEvent notificationEvent)
    {
        var deliveries = new List<NotificationDelivery>();
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

        foreach (var rule in workspace.NotificationRules)
        {
            // The sender decides the channel (e-mail, webhook, cloud) - the legacy ChannelKind used to gate this and
            // silently dropped every rule with a webhook sender.
            if (!rule.Enabled || !NotificationRuleTargeting.Matches(rule, notificationEvent.ElementId, elementsById))
            {
                continue;
            }

            if (notificationEvent.Transition == NotificationTransition.Raised)
            {
                if (rule.TriggerStates.Count > 0 && !rule.TriggerStates.Contains(notificationEvent.State))
                {
                    continue;
                }

                // Rate limit: at most Threshold raised mails per CooldownMinutes window for this rule+element
                // (Threshold null/≤0 = 1, i.e. one mail per window). Combined with the persist-until-ack model,
                // this is what stops a flapping sensor from spamming.
                if (_throttle.IsWithinCooldown(rule.Id, notificationEvent.ElementId, rule.CooldownMinutes, rule.Threshold, now))
                {
                    _logger.LogDebug("Suppressed notification for rule {Rule} on {Element} - rate limit reached", rule.Name, notificationEvent.ElementId);
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

            var context = BuildContext(notificationEvent, rule, element, alert, latest, elementsById);
            var (delivery, problem) = _deliveryFactory.Create(NotificationRoute.For(rule), context, workspace);
            if (delivery is null)
            {
                _logger.LogWarning("Notification rule {Rule} matched but nothing was sent: {Problem}", rule.Name, problem);
                continue;
            }

            deliveries.Add(delivery);

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

    /// <summary>
    /// Sends the escalation steps that are due: an alert still open, unacknowledged and not recovered after a step's
    /// minutes goes again - through the step's sender to the step's receiver (each falls back to the rule's own), with
    /// "[Escalation n]" in front of the subject. The step is marked on the alert BEFORE it is queued (persisted), so a
    /// restart neither forgets it nor sends it twice; the throttle does not apply - an escalation is rare by design.
    /// </summary>
    private void ProcessEscalations()
    {
        var now = DateTimeOffset.UtcNow;
        if (now < _nextEscalationCheck)
        {
            return;
        }

        _nextEscalationCheck = now + EscalationInterval;
        var workspace = _workspaceStore.Workspace;
        var rules = workspace.NotificationRules.Where(rule => rule.Enabled && rule.Escalations.Count > 0).ToArray();
        var open = rules.Length == 0 ? [] : workspace.Alerts.Where(alert => alert.IsActive && !alert.IsAcknowledged && !alert.IsRecovered).ToArray();
        if (open.Length == 0)
        {
            return;
        }

        Dictionary<Guid, MonitoringElement>? elementsById = null;
        IReadOnlyDictionary<Guid, SensorObservation>? latest = null;
        foreach (var alert in open)
        {
            foreach (var rule in rules)
            {
                var due = NotificationEscalation.DueSteps(rule, alert, now);
                if (due.Count == 0)
                {
                    continue;
                }

                elementsById ??= _workspaceStore.GetAllElements().ToDictionary(element => element.Id);
                if (!NotificationRuleTargeting.Matches(rule, alert.ElementId, elementsById))
                {
                    continue;
                }

                latest ??= _workspaceStore.GetLatestSensorObservations();
                foreach (var index in due)
                {
                    if (!_workspaceStore.TryMarkAlertEscalated(alert.Id, rule.Id, index, now))
                    {
                        continue;
                    }

                    var step = rule.Escalations[index];
                    var level = index + 1;
                    elementsById.TryGetValue(alert.ElementId, out var element);
                    latest.TryGetValue(alert.ElementId, out var observation);
                    var notificationEvent = new AlertNotificationEvent(alert.Id, alert.ElementId, alert.State, alert.Message, now, NotificationTransition.Raised);
                    var context = BuildContext(notificationEvent, rule, element, alert, observation, elementsById);
                    context.SetValue("escalation.level", level.ToString(System.Globalization.CultureInfo.InvariantCulture));

                    var prefix = $"[Escalation {level}] ";
                    var route = new NotificationRoute(
                        rule.Name,
                        step.SenderId ?? rule.SenderId,
                        step.ReceiverId ?? rule.ReceiverId,
                        step.ReceiverId is null ? rule.Recipient : null,
                        prefix + (string.IsNullOrWhiteSpace(rule.SubjectTemplate) ? NotificationTemplateCatalog.DefaultSubjectTemplate : rule.SubjectTemplate),
                        rule.TextTemplate,
                        rule.HtmlTemplate,
                        prefix + (string.IsNullOrWhiteSpace(rule.ShortTemplate) ? NotificationTemplateCatalog.DefaultShortTemplate : rule.ShortTemplate));
                    var (delivery, problem) = _deliveryFactory.Create(route, context, workspace, label: $"escalation {level}");
                    if (delivery is null)
                    {
                        _logger.LogWarning("Escalation {Level} of rule {Rule} for {Element} was not sent: {Problem}", level, rule.Name, alert.ElementPath, problem);
                        continue;
                    }

                    _pending.Add(delivery);
                    _logger.LogInformation("Escalation {Level} of rule {Rule} for {Element} queued - unacknowledged for {Minutes} min",
                        level, rule.Name, alert.ElementPath, (int)(now - alert.FirstSeenUtc).TotalMinutes);
                }
            }
        }
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

            var what = delivery.Label is null ? delivery.Transport.ToString().ToLowerInvariant() : $"{delivery.Transport.ToString().ToLowerInvariant()}, {delivery.Label}";
            try
            {
                await _transport.SendAsync(delivery, cancellationToken);
                _pending.RemoveAt(i);
                _logger.LogInformation("Notification ({What}) sent to {Recipient} for rule {Rule}", what, delivery.RecipientLabel, delivery.RuleName);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (NotificationDeliveryException ex) when (!ex.Retryable)
            {
                // An answer that will not change by asking again (a 4xx): say so now instead of 40 minutes later.
                _pending.RemoveAt(i);
                _logger.LogError("Notification ({What}) to {Recipient} for rule {Rule} was refused - not retried: {Error}",
                    what, delivery.RecipientLabel, delivery.RuleName, ex.Message);
            }
            catch (Exception ex)
            {
                delivery.Attempt++;
                if (delivery.Attempt >= RetryBackoff.Length)
                {
                    _pending.RemoveAt(i);
                    _logger.LogError("Notification ({What}) to {Recipient} for rule {Rule} failed after {Attempts} attempts - giving up: {Error}",
                        what, delivery.RecipientLabel, delivery.RuleName, delivery.Attempt, ex.Message);
                }
                else
                {
                    delivery.NextAttemptUtc = now + RetryBackoff[delivery.Attempt];
                    _logger.LogWarning("Notification ({What}) to {Recipient} for rule {Rule} failed (attempt {Attempt}); retrying in {Delay}: {Error}",
                        what, delivery.RecipientLabel, delivery.RuleName, delivery.Attempt, RetryBackoff[delivery.Attempt], ex.Message);
                }
            }
        }
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
        NotificationRendering.SetStateValues(context, notificationEvent.State, isRecovery);
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
}
