namespace Matmon.Core.Domain;

/// <summary>
/// When an escalation step of a rule is due for an alert - pure, so the conditions are tested. A step is due when the
/// alert is still open, nobody acknowledged it, it has not recovered, its state is one the rule fires on, the step's
/// minutes since the alert was raised have passed, and the step has not been sent for this alert yet. The marks of
/// what was sent live on the alert (<see cref="MonitoringAlert.Escalations"/>), so this survives a restart.
/// </summary>
public static class NotificationEscalation
{
    public const int MaxSteps = 3;

    /// <summary>A week: anything later is not an escalation any more.</summary>
    public const int MaxAfterMinutes = 7 * 24 * 60;

    public static IReadOnlyList<int> DueSteps(NotificationRule rule, MonitoringAlert alert, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(alert);

        if (!rule.Enabled || rule.Escalations.Count == 0 || !alert.IsActive || alert.IsAcknowledged || alert.IsRecovered)
        {
            return [];
        }

        if (rule.TriggerStates.Count > 0 && !rule.TriggerStates.Contains(alert.State))
        {
            return [];
        }

        var due = new List<int>();
        for (var index = 0; index < rule.Escalations.Count && index < MaxSteps; index++)
        {
            var step = rule.Escalations[index];
            if (step.AfterMinutes <= 0 || now < alert.FirstSeenUtc + TimeSpan.FromMinutes(step.AfterMinutes))
            {
                continue;
            }

            if (alert.Escalations.Any(mark => mark.RuleId == rule.Id && mark.Step == index))
            {
                continue;
            }

            due.Add(index);
        }

        return due;
    }

    /// <summary>What a rule stores: steps with a positive delay, capped, in the order they fire, at most <see cref="MaxSteps"/>.</summary>
    public static List<NotificationEscalationStep> Normalize(IEnumerable<NotificationEscalationStep>? steps) =>
        (steps ?? [])
            .Where(step => step.AfterMinutes > 0)
            .Select(step => new NotificationEscalationStep
            {
                AfterMinutes = Math.Min(step.AfterMinutes, MaxAfterMinutes),
                SenderId = step.SenderId,
                ReceiverId = step.ReceiverId
            })
            .OrderBy(step => step.AfterMinutes)
            .Take(MaxSteps)
            .ToList();
}
