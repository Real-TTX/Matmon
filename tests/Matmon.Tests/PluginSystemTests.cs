using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Matmon.Core.Domain;
using Matmon.Core.SnmpTraps;
using Matmon.Core.Syslog;
using Matmon.Core.Telemetry;
using Matmon.Host.Services;
using Matmon.Host.Services.Plugins;
using Matmon.Probe;
using Matmon.Probe.Plugins;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

/// <summary>
/// The plugin system: the runtime starts what is assigned, stops what is not, restarts on a settings change and says why a
/// plugin does not run; the catalog reports a plugin file it cannot load; the collectors turn what arrives into events
/// (and answer an inform); the dispatcher survives a bad event; assignments are kept per probe and never ride in a backup.
/// </summary>
public sealed class PluginSystemTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "matmon-plugins-" + Guid.NewGuid().ToString("N"));

    public PluginSystemTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    private sealed class FakePlugin(string id, PluginPlacement placement = PluginPlacement.Both, bool fail = false) : IMatmonPlugin
    {
        public int Started { get; private set; }
        public int Stopped { get; private set; }
        public IReadOnlyDictionary<string, string>? LastSettings { get; private set; }

        public PluginDefinition Definition { get; } = new(id, id, "test", placement, []);

        public Task<IPluginInstance> StartAsync(PluginContext context, CancellationToken cancellationToken)
        {
            if (fail)
            {
                throw new InvalidOperationException("port in use");
            }

            Started++;
            LastSettings = context.Settings;
            return Task.FromResult<IPluginInstance>(new Instance(this));
        }

        private sealed class Instance(FakePlugin owner) : IPluginInstance
        {
            public PluginStatus Status => PluginStatus.Running("fine");

            public ValueTask DisposeAsync()
            {
                owner.Stopped++;
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class ListSink : IPluginEventSink
    {
        public List<PluginEvent> Events { get; } = [];

        public void Publish(PluginEvent pluginEvent)
        {
            lock (Events)
            {
                Events.Add(pluginEvent);
            }
        }
    }

    private PluginRuntime Runtime(PluginCatalog catalog, IPluginEventSink? sink = null, string? probeId = "probe-01") =>
        new(catalog, sink ?? new ListSink(), NullLoggerFactory.Instance, _dir, probeId);

    private static PluginCatalog Catalog(params IMatmonPlugin[] plugins) => new(plugins, NullLogger<PluginCatalog>.Instance);

    private static PluginAssignmentSpec Spec(Guid id, string plugin, bool enabled = true, string value = "1") =>
        new(id, plugin, enabled, new Dictionary<string, string> { ["x"] = value });

    [Fact]
    public async Task The_runtime_starts_stops_and_restarts_on_a_settings_change()
    {
        var plugin = new FakePlugin("fake");
        await using var runtime = Runtime(Catalog(plugin));
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await runtime.ReconcileAsync([Spec(id, "fake")], now, default);
        await runtime.ReconcileAsync([Spec(id, "fake")], now, default);
        Assert.Equal(1, plugin.Started);
        Assert.Equal("running", Assert.Single(runtime.Statuses()).Status.State);

        await runtime.ReconcileAsync([Spec(id, "fake", value: "2")], now, default);
        Assert.Equal(2, plugin.Started);
        Assert.Equal(1, plugin.Stopped);
        Assert.Equal("2", plugin.LastSettings!["x"]);

        await runtime.ReconcileAsync([Spec(id, "fake", enabled: false)], now, default);
        Assert.Equal(2, plugin.Stopped);
        Assert.Empty(runtime.Statuses());
    }

    [Fact]
    public async Task A_plugin_that_cannot_run_says_why_and_is_retried_later()
    {
        var broken = new FakePlugin("broken", fail: true);
        var primaryOnly = new FakePlugin("primary-only", PluginPlacement.Primary);
        await using var runtime = Runtime(Catalog(broken, primaryOnly));
        var now = DateTimeOffset.UtcNow;

        await runtime.ReconcileAsync([Spec(Guid.NewGuid(), "broken"), Spec(Guid.NewGuid(), "primary-only"), Spec(Guid.NewGuid(), "missing")], now, default);

        var details = runtime.Statuses().Select(status => status.Status).ToList();
        Assert.All(details, status => Assert.Equal("error", status.State));
        Assert.Contains(details, status => status.Detail == "port in use");
        Assert.Contains(details, status => status.Detail!.Contains("cannot run on a probe"));
        Assert.Contains(details, status => status.Detail!.Contains("has no plugin 'missing'"));
    }

    [Fact]
    public void A_plugin_file_that_cannot_be_loaded_is_reported_not_fatal()
    {
        var folder = Path.Combine(_dir, "plugins");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "broken.dll"), Encoding.ASCII.GetBytes("not an assembly"));

        var catalog = new PluginCatalog([new FakePlugin("built-in")], NullLogger<PluginCatalog>.Instance, folder);

        Assert.Single(catalog.Plugins);
        Assert.Contains(catalog.LoadErrors, error => error.StartsWith("broken.dll"));
    }

    [Fact]
    public async Task The_syslog_collector_turns_a_datagram_into_a_syslog_event_with_its_arrival_time_and_sender()
    {
        var sink = new ListSink();
        var plugin = new SyslogCollectorPlugin();
        await using var instance = await plugin.StartAsync(new PluginContext
        {
            AssignmentId = Guid.NewGuid(),
            Settings = new Dictionary<string, string> { ["udpPort"] = "0", ["tcpEnabled"] = "false" },
            Events = sink,
            Logger = NullLogger.Instance,
            DataDirectory = _dir
        }, default);
        var port = int.Parse(Regex.Match(instance.Status.Detail!, @"UDP (\d+)").Groups[1].Value);

        using var udp = new UdpClient(AddressFamily.InterNetwork);
        for (var attempt = 0; attempt < 50 && sink.Events.Count == 0; attempt++)
        {
            await udp.SendAsync(Encoding.UTF8.GetBytes("<11>1 2026-10-23T01:00:00Z nas01 kernel - - - md2: resync done."), new IPEndPoint(IPAddress.Loopback, port));
            await Task.Delay(100);
        }

        var pluginEvent = sink.Events.First();
        Assert.Equal(SyslogCollectorPlugin.EventKind, pluginEvent.Kind);
        Assert.Equal("127.0.0.1", pluginEvent.Source);
        var message = JsonSerializer.Deserialize<SyslogMessage>(pluginEvent.Payload, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("nas01", message.Hostname);
        Assert.Equal("md2: resync done.", message.Message);
        Assert.Equal(pluginEvent.OccurredUtc, message.ReceivedUtc);
    }

    [Fact]
    public async Task A_collector_with_an_allow_list_that_parses_to_nothing_stays_off()
    {
        var context = new PluginContext
        {
            AssignmentId = Guid.NewGuid(),
            Settings = new Dictionary<string, string> { ["allowedSources"] = "nas, printer" },
            Events = new ListSink(),
            Logger = NullLogger.Instance,
            DataDirectory = _dir
        };

        var syslog = await Assert.ThrowsAsync<InvalidOperationException>(() => new SyslogCollectorPlugin().StartAsync(context, default));
        var traps = await Assert.ThrowsAsync<InvalidOperationException>(() => new SnmpTrapCollectorPlugin().StartAsync(context, default));
        Assert.Contains("No valid address", syslog.Message);
        Assert.Contains("No valid address", traps.Message);
    }

    [Fact]
    public void The_trap_collector_forwards_the_datagram_answers_an_inform_and_keeps_out_strangers()
    {
        var sink = new ListSink();
        var counters = new SnmpTrapCollectorPlugin.Counters();
        var inform = SnmpTrapParser.BuildV2Trap("public", "1.3.6.1.2.1.33.2.1", TimeSpan.FromSeconds(5), [], requestId: 42, inform: true);
        var onlyLan = IpAllowList.Parse("192.168.1.0/24", out _);

        var answer = SnmpTrapCollectorPlugin.Handle(inform, IPAddress.Parse("192.168.1.20"), onlyLan, counters, sink);
        var refused = SnmpTrapCollectorPlugin.Handle(inform, IPAddress.Parse("10.0.0.9"), onlyLan, counters, sink);

        Assert.NotNull(answer);
        Assert.Null(refused);
        Assert.Equal(1, counters.Refused);
        var forwarded = Assert.Single(sink.Events);
        Assert.Equal("192.168.1.20", forwarded.Source);
        var datagram = JsonSerializer.Deserialize<SnmpTrapForward>(forwarded.Payload, SnmpTrapCollectorPlugin.JsonOptions)!.Datagram;
        Assert.Equal(inform, Convert.FromBase64String(datagram));
    }

    [Fact]
    public void A_probe_remembers_its_assignments_across_a_restart()
    {
        var path = Path.Combine(_dir, "plugin-assignments.json");
        var id = Guid.NewGuid();
        ProbePluginWorker.WriteCachedAssignments(path, [Spec(id, "syslog-collector", value: "1514")]);

        var cached = Assert.Single(ProbePluginWorker.ReadCachedAssignments(path));

        Assert.Equal(id, cached.Id);
        Assert.Equal("1514", cached.Settings["X"]);
        Assert.Empty(ProbePluginWorker.ReadCachedAssignments(Path.Combine(_dir, "missing.json")));
        File.WriteAllText(path, "{ torn");
        Assert.Empty(ProbePluginWorker.ReadCachedAssignments(path));
    }

    [Fact]
    public void A_bad_event_never_stops_the_batch()
    {
        var dispatcher = new PluginEventDispatcher([new ThrowingHandler()], NullLogger<PluginEventDispatcher>.Instance);

        dispatcher.Dispatch(new PluginEvent("x", "boom", DateTimeOffset.UtcNow, null, "{}"), "probe-01");
        dispatcher.Dispatch(new PluginEvent("x", "unknown-kind", DateTimeOffset.UtcNow, null, "{}"), "probe-01");

        Assert.Equal(2, dispatcher.Unhandled);
    }

    private sealed class ThrowingHandler : IPluginEventHandler
    {
        public string Kind => "boom";

        public void Handle(PluginEvent pluginEvent, string? probeId) => throw new FormatException("not base64");
    }

    // --- assignments in the store ---------------------------------------------------------------------------------

    private InMemoryMonitoringWorkspaceStore NewStore(SqliteTelemetryRepository telemetry)
    {
        var store = new InMemoryMonitoringWorkspaceStore(
            new PluginTestHostEnvironment(_dir), new MatmonRuntimeOptions { WorkspacePath = Path.Combine(_dir, "ws.json") },
            new MatmonAuthOptions(), new EphemeralDataProtectionProvider(), telemetry, NullLogger<InMemoryMonitoringWorkspaceStore>.Instance);
        store.CompleteInitialSetup("admin@example.com", "password123");
        return store;
    }

    [Fact]
    public void Assignments_are_handed_to_their_probe_only_and_stay_out_of_backups()
    {
        using var telemetry = new SqliteTelemetryRepository(Path.Combine(_dir, "t.db"));
        using var store = NewStore(telemetry);
        var root = store.GetAllElements().OfType<ProbeElement>().Single(probe => probe.ParentId is null);
        var remote = store.CreateProbe(root.Id, "Branch", null);
        store.UpdateElement(remote.Id, element => ((ProbeElement)element).ProbeId = "branch-01");

        store.SavePluginAssignment(new PluginAssignment { PluginId = "syslog-collector", ProbeElementId = remote.Id, Settings = { ["udpPort"] = "1514" } });
        store.SavePluginAssignment(new PluginAssignment { PluginId = "other", ProbeElementId = root.Id });

        var forBranch = Assert.Single(store.GetPluginAssignmentsForProbe("BRANCH-01"));
        Assert.Equal("1514", forBranch.Settings["udpport"]);
        Assert.Empty(store.GetPluginAssignmentsForProbe("unknown"));
        Assert.Throws<InvalidOperationException>(() => store.SavePluginAssignment(new PluginAssignment { PluginId = "x", ProbeElementId = Guid.NewGuid() }));

        var package = Encoding.UTF8.GetString(store.CreateBackupBytes(WorkspaceBackupSection.All));
        Assert.DoesNotContain("syslog-collector", package);
    }
}

file sealed class PluginTestHostEnvironment(string contentRoot) : IHostEnvironment
{
    public string ApplicationName { get; set; } = "Matmon.Tests";
    public string EnvironmentName { get; set; } = "Development";
    public string ContentRootPath { get; set; } = contentRoot;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
