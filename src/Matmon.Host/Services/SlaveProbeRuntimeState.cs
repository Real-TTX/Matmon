using Matmon.Core.Domain;

namespace Matmon.Host.Services;

public sealed class SlaveProbeRuntimeState
{
    private const int MaxEvents = 120;
    private const int MaxResultTransfers = 60;
    private const int MaxUpcomingExecutions = 20;
    private readonly object _gate = new();
    private readonly List<SlaveProbeExchangeEvent> _events = [];
    private readonly List<SlaveProbeResultTransfer> _resultTransfers = [];
    private IReadOnlyList<SlaveProbeUpcomingExecution> _upcomingExecutions = [];

    private DateTimeOffset? _lastHeartbeatUtc;
    private DateTimeOffset? _lastAssignmentSyncUtc;
    private DateTimeOffset? _lastResultPostUtc;
    private DateTimeOffset? _lastResultTransferAttemptUtc;
    private bool _isConnected;
    private string _statusMessage = "starting";
    private string _lastResultTransferStatus = "No results transferred yet.";
    private bool? _lastResultTransferSucceeded;
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
                _lastResultTransferAttemptUtc,
                _assignedSensorCount,
                _lastExecutedSensorCount,
                _lastResultTransferSucceeded,
                _lastResultTransferStatus,
                _upcomingExecutions,
                _resultTransfers.OrderByDescending(entry => entry.ExecutedUtc).ToArray(),
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

    public void RecordResultPost(
        int resultCount,
        string message,
        bool success,
        IReadOnlyList<SlaveProbePendingResult>? results = null)
    {
        lock (_gate)
        {
            _isConnected = success;
            _statusMessage = message;
            _lastResultTransferAttemptUtc = DateTimeOffset.UtcNow;
            _lastResultTransferSucceeded = success;
            _lastResultTransferStatus = message;
            _lastExecutedSensorCount = resultCount;
            if (success)
            {
                _lastResultPostUtc = _lastResultTransferAttemptUtc;
            }

            if (results is not null)
            {
                foreach (var result in results)
                {
                    _resultTransfers.Add(new SlaveProbeResultTransfer(
                        result.SensorId,
                        result.Name,
                        result.Path,
                        result.State,
                        result.StateKey,
                        result.StateLabel,
                        result.Message,
                        result.ExecutedUtc,
                        _lastResultTransferAttemptUtc,
                        success,
                        message));
                }

                if (_resultTransfers.Count > MaxResultTransfers)
                {
                    _resultTransfers.RemoveRange(0, _resultTransfers.Count - MaxResultTransfers);
                }
            }

            AddEventLocked(success ? "out" : "error", "results", message, !success);
        }
    }

    public void UpdateUpcomingExecutions(IReadOnlyList<SlaveProbeUpcomingExecution> executions)
    {
        lock (_gate)
        {
            _upcomingExecutions = executions
                .OrderBy(entry => entry.NextDueUtc ?? DateTimeOffset.MaxValue)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .Take(MaxUpcomingExecutions)
                .ToArray();
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
    DateTimeOffset? LastResultTransferAttemptUtc,
    int AssignedSensorCount,
    int LastExecutedSensorCount,
    bool? LastResultTransferSucceeded,
    string LastResultTransferStatus,
    IReadOnlyList<SlaveProbeUpcomingExecution> UpcomingExecutions,
    IReadOnlyList<SlaveProbeResultTransfer> ResultTransfers,
    IReadOnlyList<SlaveProbeExchangeEvent> Events);

public sealed record SlaveProbeExchangeEvent(
    DateTimeOffset TimestampUtc,
    string Direction,
    string Kind,
    string Message,
    bool IsError);

public sealed record SlaveProbeUpcomingExecution(
    Guid SensorId,
    string Name,
    string Path,
    string SensorTypeKey,
    DateTimeOffset? NextDueUtc,
    DateTimeOffset? LastExecutedUtc,
    string ScheduleSummary);

public sealed record SlaveProbePendingResult(
    Guid SensorId,
    string Name,
    string Path,
    SensorState State,
    string StateKey,
    string StateLabel,
    string? Message,
    DateTimeOffset ExecutedUtc);

public sealed record SlaveProbeResultTransfer(
    Guid SensorId,
    string Name,
    string Path,
    SensorState State,
    string StateKey,
    string StateLabel,
    string? Message,
    DateTimeOffset ExecutedUtc,
    DateTimeOffset? TransferAttemptUtc,
    bool? TransferSucceeded,
    string TransferStatus);
