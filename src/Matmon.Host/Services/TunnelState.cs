namespace Matmon.Host.Services;

/// <summary>
/// Live Full Access tunnel status, surfaced on the System → Cloud page so an admin can tell a healthy idle tunnel
/// from a silently-failing one (e.g. a reverse proxy that doesn't pass WebSocket upgrades, or a cloud that refuses
/// the tunnel because Full Access isn't licensed). In-memory only (a restart just re-establishes it). Written by the
/// Primary-only <see cref="TunnelClient"/>; read by the Config page. Thread-safe.
/// </summary>
public sealed class TunnelState
{
    private readonly object _gate = new();
    private bool _enabled;
    private bool _connected;
    private DateTimeOffset? _connectedSinceUtc;
    private DateTimeOffset? _lastAttemptUtc;
    private string? _lastError;
    private int _consecutiveFailures;
    private long _requestsServed;
    private DateTimeOffset? _lastRequestUtc;

    /// <summary>Whether Full Access is switched on for this instance (independent of whether it is connected).</summary>
    public void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            _enabled = enabled;
            if (!enabled)
            {
                _connected = false;
                _connectedSinceUtc = null;
            }
        }
    }

    public void MarkAttempt()
    {
        lock (_gate) { _lastAttemptUtc = DateTimeOffset.UtcNow; }
    }

    public void MarkConnected()
    {
        lock (_gate)
        {
            _connected = true;
            _connectedSinceUtc = DateTimeOffset.UtcNow;
            _lastError = null;
            _consecutiveFailures = 0;
        }
    }

    /// <summary>Record a disconnect / failed attempt. <paramref name="failure"/> distinguishes an error drop
    /// (counts toward the backoff + escalates logging) from an orderly close (settings changed).</summary>
    public void MarkDisconnected(string? error, bool failure)
    {
        lock (_gate)
        {
            _connected = false;
            _connectedSinceUtc = null;
            if (!string.IsNullOrWhiteSpace(error)) { _lastError = error; }
            if (failure) { _consecutiveFailures++; }
        }
    }

    public void MarkRequestServed()
    {
        lock (_gate)
        {
            _requestsServed++;
            _lastRequestUtc = DateTimeOffset.UtcNow;
        }
    }

    public int ConsecutiveFailures
    {
        get { lock (_gate) { return _consecutiveFailures; } }
    }

    public TunnelStatusSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new TunnelStatusSnapshot(_enabled, _connected, _connectedSinceUtc, _lastAttemptUtc, _lastError, _consecutiveFailures, _requestsServed, _lastRequestUtc);
        }
    }
}

/// <summary>An immutable snapshot of <see cref="TunnelState"/> for rendering.</summary>
public sealed record TunnelStatusSnapshot(
    bool Enabled,
    bool Connected,
    DateTimeOffset? ConnectedSinceUtc,
    DateTimeOffset? LastAttemptUtc,
    string? LastError,
    int ConsecutiveFailures,
    long RequestsServed,
    DateTimeOffset? LastRequestUtc);
