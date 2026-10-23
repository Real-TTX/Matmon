using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Matmon.Core.Domain;
using Matmon.Probe;
using Matmon.Core.SnmpTraps;
using Matmon.Core.Syslog;

namespace Matmon.Host.Services;

/// <summary>What the System > SNMP traps page shows about the traps the primary processed (from every collector). The
/// collectors' own sockets report on their plugin rows. Thread-safe; reset by a restart.</summary>
public sealed class SnmpTrapReceiverState
{
    private readonly object _gate = new();
    private string? _warning;
    private long _received;
    private long _rejected;
    private long _unreadable;
    private long _wokenSensors;
    private DateTimeOffset? _lastTrapUtc;
    private string? _lastTrap;

    public void Received(string summary, int woken, DateTimeOffset now)
    {
        lock (_gate)
        {
            _received++;
            _wokenSensors += woken;
            _lastTrapUtc = now;
            _lastTrap = summary;
        }
    }

    public void Rejected() => Interlocked.Increment(ref _rejected);

    public void Unreadable(string reason, DateTimeOffset now)
    {
        Interlocked.Increment(ref _unreadable);
        lock (_gate)
        {
            _warning = $"Last unreadable packet ({now:HH:mm:ss} UTC): {reason}";
        }
    }

    public SnmpTrapReceiverSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new SnmpTrapReceiverSnapshot(_warning, _received, Interlocked.Read(ref _rejected),
                Interlocked.Read(ref _unreadable), _wokenSensors, _lastTrapUtc, _lastTrap);
        }
    }
}

public sealed record SnmpTrapReceiverSnapshot(
    string? Warning,
    long Received,
    long Rejected,
    long Unreadable,
    long WokenSensors,
    DateTimeOffset? LastTrapUtc,
    string? LastTrap);

/// <summary>
/// What happens to a trap a collector took (the SNMP trap collector plugin, on the primary or on a probe - it already kept
/// out the senders its allow-list does not name and answered a v2c inform): the community or the SNMPv3 user decides whether
/// it counts, the trap goes into the central log (the syslog database - so it is searchable on Events > Syslog and a Syslog
/// Match sensor can alarm on it) with the names from the MIB library, and the sending device's sensors poll at once.
/// </summary>
public sealed class SnmpTrapIngest
{
    /// <summary>The app name a trap carries in the log; the message starts with "SNMP trap", which is what the log is
    /// filtered by from the System page.</summary>
    public const string AppName = "snmptrap";

    private static readonly Dictionary<string, int> KnownSeverities = new(StringComparer.Ordinal)
    {
        // SNMPv2-MIB / IF-MIB generic traps
        ["1.3.6.1.6.3.1.1.5.1"] = SyslogSeverity.Notice,  // coldStart
        ["1.3.6.1.6.3.1.1.5.2"] = SyslogSeverity.Notice,  // warmStart
        ["1.3.6.1.6.3.1.1.5.3"] = SyslogSeverity.Warning, // linkDown
        ["1.3.6.1.6.3.1.1.5.4"] = SyslogSeverity.Notice,  // linkUp
        ["1.3.6.1.6.3.1.1.5.5"] = SyslogSeverity.Warning, // authenticationFailure
        // UPS-MIB (RFC 1628)
        ["1.3.6.1.2.1.33.2.1"] = SyslogSeverity.Warning,  // upsTrapOnBattery
        ["1.3.6.1.2.1.33.2.2"] = SyslogSeverity.Notice,   // upsTrapTestCompleted
        ["1.3.6.1.2.1.33.2.3"] = SyslogSeverity.Warning,  // upsTrapAlarmEntryAdded
        ["1.3.6.1.2.1.33.2.4"] = SyslogSeverity.Notice    // upsTrapAlarmEntryRemoved
    };

    private static readonly Dictionary<string, string> KnownNames = new(StringComparer.Ordinal)
    {
        ["1.3.6.1.6.3.1.1.5.1"] = "coldStart",
        ["1.3.6.1.6.3.1.1.5.2"] = "warmStart",
        ["1.3.6.1.6.3.1.1.5.3"] = "linkDown",
        ["1.3.6.1.6.3.1.1.5.4"] = "linkUp",
        ["1.3.6.1.6.3.1.1.5.5"] = "authenticationFailure",
        ["1.3.6.1.2.1.33.2.1"] = "upsTrapOnBattery",
        ["1.3.6.1.2.1.33.2.2"] = "upsTrapTestCompleted",
        ["1.3.6.1.2.1.33.2.3"] = "upsTrapAlarmEntryAdded",
        ["1.3.6.1.2.1.33.2.4"] = "upsTrapAlarmEntryRemoved"
    };

    private readonly ISyslogStore _store;
    private readonly SnmpTrapReceiverState _state;
    private readonly MibLibrary? _mibs;
    private readonly SensorWakeService? _wake;
    private readonly ILogger<SnmpTrapIngest> _logger;
    private volatile IReadOnlyList<string> _communities = [];
    private volatile bool _pollOnTrap = true;
    private volatile IReadOnlyList<SnmpTrapV3User> _v3Users = [];

    public SnmpTrapIngest(ISyslogStore store, SnmpTrapReceiverState state, ILogger<SnmpTrapIngest> logger, MibLibrary? mibs = null, SensorWakeService? wake = null)
    {
        _store = store;
        _state = state;
        _logger = logger;
        _mibs = mibs;
        _wake = wake;
    }

    public void Configure(IReadOnlyList<string> communities, bool pollOnTrap, IReadOnlyList<SnmpTrapV3User>? v3Users = null)
    {
        _communities = communities;
        _pollOnTrap = pollOnTrap;
        _v3Users = v3Users ?? [];
    }

    /// <summary>One trap a collector took, logged at the time it ARRIVED there (a trap from an outage of the primary lands
    /// where it belongs in the log). <paramref name="viaProbe"/> is the probe whose collector took it, null for the primary's
    /// own. False when it was refused (community, v3 check) or is no trap.</summary>
    public bool Accept(ReadOnlySpan<byte> datagram, IPAddress sender, DateTimeOffset receivedUtc, string? viaProbe = null)
    {
        var source = sender.IsIPv4MappedToIPv6 ? sender.MapToIPv4() : sender;
        var now = receivedUtc;
        if (!SnmpTrapParser.TryParse(datagram, _v3Users, out var trap, out var error) || trap is null)
        {
            _state.Unreadable($"{source}: {error}", now);
            return false;
        }

        // A v3 trap has no community: the user it came from passed authentication, which is the stronger check.
        if (trap.Version != 3 && _communities.Count > 0 && !_communities.Contains(trap.Community, StringComparer.Ordinal))
        {
            _state.Rejected();
            return false;
        }

        var name = Name(trap.TrapOid);
        var message = Describe(trap, name, source);
        _store.TryEnqueue(new SyslogMessage
        {
            ReceivedUtc = now,
            SourceAddress = source.ToString(),
            // A relay forwards a device's trap with the device's address in snmpTrapAddress (v2) or agent-addr (v1): as
            // the host name, the log's sender filter (address OR host name) finds the trap under the device.
            Hostname = trap.AgentAddress is { Length: > 0 } agent && agent != source.ToString() && agent != "0.0.0.0" ? agent : null,
            Facility = 1,
            Severity = KnownSeverities.TryGetValue(trap.TrapOid, out var severity) ? severity : SyslogSeverity.Notice,
            DeviceTime = trap.Uptime is TimeSpan uptime ? $"uptime {MonitoringSchedule.FormatDuration(uptime)}" : null,
            AppName = AppName,
            MsgId = name,
            Message = message,
            Format = SyslogFormat.Raw
        });

        // A device on this network does not send from loopback - that is the page's test trap, which wakes nothing.
        var woken = 0;
        if (_pollOnTrap && _wake is not null && !IPAddress.IsLoopback(source))
        {
            var device = trap.AgentAddress is { Length: > 0 } agentText && IPAddress.TryParse(agentText, out var agentAddress) && !agentAddress.Equals(IPAddress.Any)
                ? agentAddress
                : source;
            woken = _wake.WakeSensorsOf(device);
        }

        _state.Received($"{name} from {source}", woken, now);
        _logger.LogInformation("SNMP trap {Trap} from {Source}{Via} ({Woken} sensor(s) polled)", name, source, viaProbe is null ? string.Empty : $" via probe {viaProbe}", woken);
        return true;
    }

    private string Name(string oid)
    {
        if (string.IsNullOrEmpty(oid))
        {
            return "(no trap OID)";
        }

        if (_mibs?.Translate(oid) is { } translation && translation.Instance.Length == 0)
        {
            return translation.Node.Name;
        }

        return KnownNames.TryGetValue(oid, out var known) ? known : oid;
    }

    private string Describe(SnmpTrap trap, string name, IPAddress source)
    {
        var text = new StringBuilder();
        text.Append("SNMP trap ").Append(name);
        if (name != trap.TrapOid && trap.TrapOid.Length > 0)
        {
            text.Append(" (").Append(trap.TrapOid).Append(')');
        }

        if (trap.AgentAddress is { Length: > 0 } agent && agent != source.ToString() && agent != "0.0.0.0")
        {
            text.Append(" for ").Append(agent);
        }

        var bindings = trap.VarBinds.Where(binding => binding.Oid != SnmpTrapParser.SnmpTrapAddressOid).Take(20).ToArray();
        if (bindings.Length > 0)
        {
            text.Append(" - ");
            text.Append(string.Join("; ", bindings.Select(binding =>
            {
                var translation = _mibs?.Translate(binding.Oid);
                var label = translation is not null && translation.IsObject ? translation.Name : binding.Oid;
                var value = translation is not null && translation.IsObject ? translation.FormatValue(binding.Value) : binding.Value;
                return $"{label} = {value}";
            })));
        }

        var line = text.ToString();
        return line.Length <= 2000 ? line : line[..2000] + "…";
    }
}

/// <summary>
/// Makes a device's sensors poll NOW - for a trap ("something happened at 10.0.0.5"), which says that, but not what the
/// state is: the sensors find out. A sensor belongs to the device when its own target, or its host's address, is the
/// device's address (a host NAME is resolved, cached for 10 minutes). Local sensors run in the background, a remote
/// probe's get a run job; each sensor at most once per 10 s, so a burst of traps does not hammer a device.
/// </summary>
public sealed class SensorWakeService
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DnsCacheTime = TimeSpan.FromMinutes(10);

    private readonly IMonitoringWorkspaceStore _store;
    private readonly ProbeSensorAssignmentProvider _assignments;
    private readonly IOnDemandRunStore _runs;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<SensorWakeService> _logger;
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastWake = new();
    private readonly ConcurrentDictionary<string, (IPAddress[] Addresses, DateTimeOffset Until)> _dns = new(StringComparer.OrdinalIgnoreCase);

    public SensorWakeService(
        IMonitoringWorkspaceStore store,
        ProbeSensorAssignmentProvider assignments,
        IOnDemandRunStore runs,
        IServiceScopeFactory scopes,
        ILogger<SensorWakeService> logger)
    {
        _store = store;
        _assignments = assignments;
        _runs = runs;
        _scopes = scopes;
        _logger = logger;
    }

    /// <summary>Starts a poll of every sensor of the device at <paramref name="address"/>; returns how many.</summary>
    public int WakeSensorsOf(IPAddress address)
    {
        var elements = _store.GetAllElements();
        var byId = elements.ToDictionary(element => element.Id);
        return Wake(elements.OfType<SensorElement>().Where(sensor => BelongsTo(sensor, byId, address)), "after a trap");
    }

    /// <summary>Starts a poll of these sensors (a dependency to check, dependents to look again); returns how many.</summary>
    public int WakeSensors(IReadOnlyCollection<Guid> sensorIds)
    {
        if (sensorIds.Count == 0)
        {
            return 0;
        }

        return Wake(_store.GetAllElements().OfType<SensorElement>().Where(sensor => sensorIds.Contains(sensor.Id)), "on request");
    }

    private int Wake(IEnumerable<SensorElement> sensors, string why)
    {
        var now = DateTimeOffset.UtcNow;
        var local = new List<Guid>();
        var count = 0;
        foreach (var sensor in sensors)
        {
            if (sensor.IsPaused)
            {
                continue;
            }

            if (_lastWake.TryGetValue(sensor.Id, out var last) && now - last < MinInterval)
            {
                continue;
            }

            _lastWake[sensor.Id] = now;
            count++;
            if (_assignments.TryBuildProbeReadyRun(sensor.Id, out var probeId, out var typeKey, out var target, out var settings) && probeId is not null)
            {
                _runs.Create(probeId, sensor.Id, typeKey, target, settings, recordObservation: true, ProbeRunJobKind.Sensor);
            }
            else
            {
                local.Add(sensor.Id);
            }
        }

        if (local.Count > 0)
        {
            var ids = local.ToArray();
            _ = Task.Run(async () =>
            {
                using var scope = _scopes.CreateScope();
                var execution = scope.ServiceProvider.GetRequiredService<ISensorExecutionService>();
                foreach (var id in ids)
                {
                    try
                    {
                        await execution.ExecuteNowAsync(id);
                    }
                    catch (Exception exception)
                    {
                        _logger.LogDebug(exception, "Polling sensor {Sensor} {Why} failed", id, why);
                    }
                }
            });
        }

        return count;
    }

    private bool BelongsTo(SensorElement sensor, IReadOnlyDictionary<Guid, MonitoringElement> byId, IPAddress address)
    {
        if (!string.IsNullOrWhiteSpace(sensor.Target) && Matches(sensor.Target, address))
        {
            return true;
        }

        var lineage = MonitoringTopology.BuildLineage(sensor, byId);
        var host = lineage.OfType<HostElement>().LastOrDefault();
        return string.IsNullOrWhiteSpace(sensor.Target) && host is not null && !string.IsNullOrWhiteSpace(host.Address) && Matches(host.Address, address);
    }

    private bool Matches(string target, IPAddress address)
    {
        var text = target.Trim();
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
        {
            text = uri.Host; // an HTTP sensor's target is a URL
        }

        if (IPAddress.TryParse(text, out var parsed))
        {
            return parsed.Equals(address);
        }

        if (text.Contains(' ') || text.Length > 253)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        if (!_dns.TryGetValue(text, out var cached) || cached.Until < now)
        {
            IPAddress[] resolved;
            try
            {
                resolved = Dns.GetHostAddressesAsync(text).WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            }
            catch
            {
                resolved = [];
            }

            cached = (resolved, now + DnsCacheTime);
            _dns[text] = cached;
        }

        return cached.Addresses.Any(candidate => (candidate.IsIPv4MappedToIPv6 ? candidate.MapToIPv4() : candidate).Equals(address));
    }
}

/// <summary>"Send a test trap": one real SNMPv2c trap to the port of the collector on this machine (the main probe's),
/// then a look in the log - through the collector's socket and allow-list, the community check and the database, the way
/// a device's trap goes.</summary>
public static class SnmpTrapSelfTest
{
    public const string TrapOid = "1.3.6.1.6.3.1.1.5.1"; // coldStart - every receiver knows it

    public static async Task<(bool Ok, string Message)> RunAsync(int port, SnmpTrapSettings settings, ISyslogStore store, SnmpTrapReceiverState state, CancellationToken cancellationToken = default)
    {
        var normalized = settings.Normalized();
        var before = state.Snapshot();
        var marker = Guid.NewGuid().ToString("N")[..8];
        var community = normalized.CommunityList().FirstOrDefault() ?? "public";
        var datagram = SnmpTrapParser.BuildV2Trap(community, TrapOid, TimeSpan.FromSeconds(1), [("1.3.6.1.2.1.1.1.0", $"Matmon test trap {marker}")]);
        var sentAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            await udp.SendAsync(datagram, new IPEndPoint(IPAddress.Loopback, port), cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException exception)
        {
            return (false, $"The test trap could not be sent to this machine's own UDP port: {exception.Message}");
        }

        for (var attempt = 0; attempt < 20; attempt++)
        {
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            await store.FlushAsync(cancellationToken).ConfigureAwait(false);
            var found = store.Search(new SyslogQuery { FromUtc = sentAt, Contains = marker }, 1);
            if (found.Count > 0)
            {
                return (true, $"Received and stored: \"{found[0].Message}\".");
            }
        }

        return state.Snapshot().Rejected > before.Rejected
            ? (false, "The test trap was refused: its community is not in the list above.")
            : (false, "The test trap was sent but did not show up in the log - is 127.0.0.1 in the collector's Allowed sources?");
    }
}
