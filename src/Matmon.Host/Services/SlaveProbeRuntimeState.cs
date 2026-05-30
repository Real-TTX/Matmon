namespace Matmon.Host.Services;

public sealed class SlaveProbeRuntimeState
{
    private const int MaxEvents = 120;
    private readonly object _gate = new();
    private readonly List<SlaveProbeExchangeEvent> _events = [];

    private DateTimeOffset? _lastHeartbeatUtc;
    private DateTimeOffset? _lastAssignmentSyncUtc;
    private DateTimeOffset? _lastResultPostUtc;
    private bool _isConnected;
    private string _statusMessage = "starting";
    private int _assignedSensorCount;
    private int _lastExecutedSensorCount;

    public SlaveProbeRuntimeSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new SlaveProbeRuntimeSnapshot(
                _isConnected,
                _statusMessage,
                _lastHeartbeatUtc,
                _lastAssignmentSyncUtc,
                _lastResultPostUtc,
                _assignedSensorCount,
                _lastExecutedSensorCount,
                _events.OrderByDescending(entry => entry.TimestampUtc).ToArray());
        }
    }

    public void RecordHeartbeat(bool success, string message)
    {
        lock (_gate)
        {
            _isConnected = success;
            _statusMessage = message;
            if (success)
            {
                _lastHeartbeatUtc = DateTimeOffset.UtcNow;
            }

            AddEventLocked(success ? "out" : "error", "heartbeat", message, !success);
        }
    }

    public void RecordAssignmentSync(int assignedSensorCount, string message, bool success)
    {
        lock (_gate)
        {
            _isConnected = success;
            _statusMessage = message;
            _assignedSensorCount = assignedSensorCount;
            if (success)
            {
                _lastAssignmentSyncUtc = DateTimeOffset.UtcNow;
            }

            AddEventLocked(success ? "in" : "error", "assignments", message, !success);
        }
    }

    public void RecordExecution(string sensorName, string message, bool success)
    {
        lock (_gate)
        {
            AddEventLocked(success ? "local" : "error", sensorName, message, !success);
        }
    }

    public void RecordResultPost(int resultCount, string message, bool success)
    {
        lock (_gate)
        {
            _isConnected = success;
            _statusMessage = message;
            _lastExecutedSensorCount = resultCount;
            if (success)
            {
                _lastResultPostUtc = DateTimeOffset.UtcNow;
            }

            AddEventLocked(success ? "out" : "error", "results", message, !success);
        }
    }

    private void AddEventLocked(string direction, string kind, string message, bool isError)
    {
        _events.Add(new SlaveProbeExchangeEvent(
            DateTimeOffset.UtcNow,
            direction,
            kind,
            message,
            isError));

        if (_events.Count > MaxEvents)
        {
            _events.RemoveRange(0, _events.Count - MaxEvents);
        }
    }
}

public sealed record SlaveProbeRuntimeSnapshot(
    bool IsConnected,
    string StatusMessage,
    DateTimeOffset? LastHeartbeatUtc,
    DateTimeOffset? LastAssignmentSyncUtc,
    DateTimeOffset? LastResultPostUtc,
    int AssignedSensorCount,
    int LastExecutedSensorCount,
    IReadOnlyList<SlaveProbeExchangeEvent> Events);

public sealed record SlaveProbeExchangeEvent(
    DateTimeOffset TimestampUtc,
    string Direction,
    string Kind,
    string Message,
    bool IsError);
