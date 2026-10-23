using System.Collections.Concurrent;
using System.Text.Json;
using Matmon.Core;
using Matmon.Core.Domain;
using Matmon.Core.Syslog;
using Matmon.Probe.Plugins;

namespace Matmon.Host.Services.Plugins;

/// <summary>Handles the events of one kind on the primary - wherever the plugin that raised them runs.</summary>
public interface IPluginEventHandler
{
    string Kind { get; }

    void Handle(PluginEvent pluginEvent, string? probeId);
}

/// <summary>A <c>syslog</c> event (the syslog collector on a probe) goes into the central log as if the device had sent it here:
/// its arrival time on the probe and its source address are kept, the store assigns a new id.</summary>
public sealed class SyslogPluginEventHandler(ISyslogStore store) : IPluginEventHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Kind => SyslogCollectorPlugin.EventKind;

    public void Handle(PluginEvent pluginEvent, string? probeId)
    {
        if (JsonSerializer.Deserialize<SyslogMessage>(pluginEvent.Payload, Json) is { } message)
        {
            store.TryEnqueue(message with { Id = 0 });
        }
    }
}

/// <summary>Routes plugin events by kind to their handler. An event no handler knows is counted, not an error - a newer
/// probe may carry a plugin this primary does not know yet.</summary>
public sealed class PluginEventDispatcher(IEnumerable<IPluginEventHandler> handlers, ILogger<PluginEventDispatcher> logger)
{
    private readonly Dictionary<string, IPluginEventHandler> _handlers =
        handlers.ToDictionary(handler => handler.Kind, StringComparer.OrdinalIgnoreCase);
    private long _unhandled;

    public long Unhandled => Interlocked.Read(ref _unhandled);

    public void Dispatch(PluginEvent pluginEvent, string? probeId)
    {
        if (!_handlers.TryGetValue(pluginEvent.Kind, out var handler))
        {
            Interlocked.Increment(ref _unhandled);
            return;
        }

        try
        {
            handler.Handle(pluginEvent, probeId);
        }
        // Every failure, not only the expected ones: an event that throws must never make the probe send its batch again forever.
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Interlocked.Increment(ref _unhandled);
            logger.LogDebug(exception, "A {Kind} event from {Probe} could not be handled", pluginEvent.Kind, probeId ?? "the primary");
        }
    }
}

/// <summary>What the primary knows about where plugins run: the last state each assignment reported and, per probe, which
/// plugins it has and how many events wait in its queue. In memory - a restart forgets it until the next report.</summary>
public sealed class PluginStatusRegistry
{
    private readonly ConcurrentDictionary<Guid, PluginAssignmentState> _assignments = new();
    private readonly ConcurrentDictionary<string, ProbePluginState> _probes = new(StringComparer.OrdinalIgnoreCase);

    public void ReportAssignment(Guid assignmentId, PluginStatus status, DateTimeOffset at) =>
        _assignments[assignmentId] = new PluginAssignmentState(status.State, status.Detail, at);

    public void ReportProbe(string probeId, IReadOnlyList<string> availablePlugins, int queuedEvents, long droppedEvents, int receivedEvents, DateTimeOffset at) =>
        _probes.AddOrUpdate(probeId,
            _ => new ProbePluginState(availablePlugins, queuedEvents, droppedEvents, receivedEvents, at),
            (_, previous) => new ProbePluginState(availablePlugins, queuedEvents, droppedEvents, previous.ReceivedEvents + receivedEvents, at));

    public PluginAssignmentState? Assignment(Guid id) => _assignments.TryGetValue(id, out var state) ? state : null;

    public ProbePluginState? Probe(string probeId) => _probes.TryGetValue(probeId, out var state) ? state : null;
}

public sealed record PluginAssignmentState(string State, string? Detail, DateTimeOffset ReportedUtc);

public sealed record ProbePluginState(IReadOnlyList<string> AvailablePlugins, int QueuedEvents, long DroppedEvents, int ReceivedEvents, DateTimeOffset LastReportUtc);

/// <summary>
/// Runs the plugins assigned to the primary itself (the root probe) - the same <see cref="PluginRuntime"/> a probe uses,
/// with the events going straight to the dispatcher. Reconciled every few seconds, so a change on the Plugins page takes
/// effect without a restart.
/// </summary>
public sealed class PrimaryPluginService(
    IMonitoringWorkspaceStore store,
    PluginCatalog catalog,
    PluginEventDispatcher dispatcher,
    PluginStatusRegistry registry,
    MatmonRuntimeOptions options,
    ILoggerFactory loggerFactory,
    ILogger<PrimaryPluginService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Mode != AppMode.Primary)
        {
            return;
        }

        var dataRoot = Path.GetDirectoryName(Path.GetFullPath(options.PluginsPath ?? "plugins")) ?? AppContext.BaseDirectory;
        await using var runtime = new PluginRuntime(catalog, new DispatchingSink(dispatcher), loggerFactory, dataRoot, probeId: null);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var rootId = store.GetAllElements().OfType<ProbeElement>().FirstOrDefault(probe => probe.ParentId is null)?.Id;
                var wanted = store.GetPluginAssignments()
                    .Where(assignment => assignment.ProbeElementId == rootId)
                    .Select(assignment => new PluginAssignmentSpec(assignment.Id, assignment.PluginId, assignment.Enabled, assignment.Settings))
                    .ToList();
                var now = DateTimeOffset.UtcNow;
                await runtime.ReconcileAsync(wanted, now, stoppingToken);
                foreach (var (assignmentId, status) in runtime.Statuses())
                {
                    registry.ReportAssignment(assignmentId, status, now);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "The primary's plugins could not be reconciled; trying again");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private sealed class DispatchingSink(PluginEventDispatcher dispatcher) : IPluginEventSink
    {
        public void Publish(PluginEvent pluginEvent) => dispatcher.Dispatch(pluginEvent, probeId: null);
    }
}

/// <summary>An <c>snmptrap</c> event (the trap collector on a probe): the datagram goes through the primary's own trap path -
/// communities, SNMPv3 users, MIB names, the central log, polling the device's sensors - at the time it reached the probe.</summary>
public sealed class SnmpTrapPluginEventHandler(SnmpTrapIngest ingest) : IPluginEventHandler
{
    public string Kind => SnmpTrapCollectorPlugin.EventKind;

    public void Handle(PluginEvent pluginEvent, string? probeId)
    {
        var forward = JsonSerializer.Deserialize<SnmpTrapForward>(pluginEvent.Payload, SnmpTrapCollectorPlugin.JsonOptions);
        if (forward is null || !System.Net.IPAddress.TryParse(pluginEvent.Source, out var source))
        {
            return;
        }

        ingest.AcceptForwarded(Convert.FromBase64String(forward.Datagram), source, pluginEvent.OccurredUtc, probeId ?? "primary");
    }
}
