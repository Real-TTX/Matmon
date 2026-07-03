using System.Collections.Concurrent;
using Matmon.Core.Domain;

namespace Matmon.Host.Services;

public enum NotificationTransition
{
    Raised = 0,
    Recovered = 1,
    Resolved = 2
}

/// <summary>
/// A lightweight record of an alert transition. The store enqueues one (under <c>_gate</c>) when an
/// alert is raised; the background <see cref="NotificationDispatchService"/> matches it against rules
/// and sends off the hot path.
/// </summary>
public sealed record AlertNotificationEvent(
    Guid AlertId,
    Guid ElementId,
    SensorState State,
    string Message,
    DateTimeOffset TimestampUtc,
    NotificationTransition Transition);

/// <summary>Where the store drops alert transitions for the notification dispatcher.</summary>
public interface INotificationSink
{
    void Enqueue(AlertNotificationEvent notificationEvent);
}

/// <summary>
/// Bounded in-memory hand-off queue between the store (producer, under <c>_gate</c>) and the dispatcher
/// (single consumer). Has no dependencies, so it can't create a DI cycle with the store. In-memory only:
/// a restart mid-flight loses queued events (acceptable for a small spooler; retries live in the dispatcher).
/// </summary>
public sealed class NotificationSpooler : INotificationSink
{
    private const int Capacity = 1000;
    private readonly ConcurrentQueue<AlertNotificationEvent> _queue = new();
    private int _count;

    public void Enqueue(AlertNotificationEvent notificationEvent)
    {
        ArgumentNullException.ThrowIfNull(notificationEvent);

        // Drop rather than grow unbounded — only happens if the dispatcher is far behind (e.g. SMTP down
        // for a long time); those retries are already tracked in the dispatcher's own pending list.
        if (Volatile.Read(ref _count) >= Capacity)
        {
            return;
        }

        _queue.Enqueue(notificationEvent);
        Interlocked.Increment(ref _count);
    }

    public bool TryDequeue(out AlertNotificationEvent notificationEvent)
    {
        if (_queue.TryDequeue(out notificationEvent!))
        {
            Interlocked.Decrement(ref _count);
            return true;
        }

        return false;
    }
}
