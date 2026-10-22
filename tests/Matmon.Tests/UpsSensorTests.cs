using System.Net;
using System.Net.Sockets;
using System.Text;
using Matmon.Core.Domain;
using Matmon.Core.Telemetry;
using Matmon.Host.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

/// <summary>
/// A power cut has to be critical AT ONCE. It used to stay green until the battery had drained below 50 %, because a
/// UPS sensor read charge, runtime and load only. These pin the shared UPS channels (SNMP UPS-MIB and NUT alike), the
/// NUT protocol against a stand-in server, and migration 7 for UPS sensors that already exist.
/// </summary>
public sealed class UpsSensorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "matmon-ups-" + Guid.NewGuid().ToString("N"));

    public UpsSensorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    private static double? Channel(IEnumerable<SensorChannelValue> channels, string key) =>
        channels.FirstOrDefault(channel => channel.Key == key)?.Value;

    // --- the shared channels -------------------------------------------------------------------------------

    [Fact]
    public void Nut_status_flags_become_the_shared_flags()
    {
        var reading = UpsChannels.FromNut(new Dictionary<string, string>
        {
            ["ups.status"] = "OB DISCHRG LB",
            ["battery.charge"] = "18",
            ["battery.runtime"] = "330",
            ["ups.load"] = "42",
            ["input.voltage"] = "0.0",
            ["ups.mfr"] = "APC",
            ["ups.model"] = "Back-UPS 950"
        });

        var channels = UpsChannels.ToChannels(reading);

        Assert.Equal(1, Channel(channels, UpsChannels.OnBattery));
        Assert.Equal(1, Channel(channels, UpsChannels.LowBattery));
        Assert.Equal(0, Channel(channels, UpsChannels.ReplaceBattery));
        Assert.Equal(18, Channel(channels, UpsChannels.BatteryCharge));
        Assert.Equal(5.5, Channel(channels, UpsChannels.RuntimeMinutes)); // seconds -> minutes
        Assert.Equal("APC Back-UPS 950", reading.Model);
        Assert.Equal("On battery, LOW - 18 % - 5.5 min left", UpsChannels.Describe(reading));
        Assert.Equal(UpsChannels.OnBattery, channels[0].Key); // the flags first
    }

    [Fact]
    public void A_forced_shutdown_counts_as_low_battery_and_online_charging_is_said_so()
    {
        Assert.True(UpsChannels.FromNut(new Dictionary<string, string> { ["ups.status"] = "OB FSD" }).LowBattery);

        var online = UpsChannels.FromNut(new Dictionary<string, string> { ["ups.status"] = "OL CHRG", ["battery.charge"] = "97" });
        Assert.False(online.OnBattery);
        Assert.Equal("Online, charging - 97 %", UpsChannels.Describe(online));
    }

    [Fact]
    public void A_ups_that_reports_no_status_gets_no_flags_rather_than_false_ones()
    {
        var reading = UpsChannels.FromNut(new Dictionary<string, string> { ["battery.charge"] = "100" });

        var channels = UpsChannels.ToChannels(reading);

        Assert.Null(Channel(channels, UpsChannels.OnBattery));
        Assert.Equal(100, Channel(channels, UpsChannels.BatteryCharge));
    }

    [Theory]
    [InlineData(5, 0, true)]   // output source battery
    [InlineData(3, 12, true)]  // mains, but seconds on battery > 0 (some cards switch the source late)
    [InlineData(3, 0, false)]
    [InlineData(4, 0, false)]  // bypass is not battery
    public void The_ups_mib_says_on_battery_through_the_output_source_or_the_seconds(double outputSource, double seconds, bool onBattery)
    {
        var raw = new List<SensorChannelValue>
        {
            new() { Key = "battery_charge", Label = "Battery Charge", Value = 100 },
            new() { Key = "output_source", Label = "Output Source", Value = outputSource },
            new() { Key = "seconds_on_battery", Label = "Seconds On Battery", Value = seconds },
            new() { Key = "battery_status", Label = "Battery Status", Value = 2 }
        };

        var reading = UpsChannels.FromRfc1628(raw);

        Assert.Equal(onBattery, reading.OnBattery);
        Assert.Equal(outputSource == 4, reading.OnBypass);
        Assert.False(reading.LowBattery);
    }

    [Fact]
    public void The_snmp_read_gains_the_flags_and_alarms_on_a_power_cut_with_the_default_thresholds()
    {
        var settings = new MonitoringSettings();
        SensorThresholdDefaults.Apply("ups-snmp", settings);
        var read = SensorExecutionResult.Healthy(
            TimeSpan.FromMilliseconds(30),
            "Battery Charge: 100",
            100,
            "battery_charge",
            [
                new SensorChannelValue { Key = "battery_charge", Label = "Battery Charge", Value = 100, IsDefault = true },
                new SensorChannelValue { Key = "runtime_minutes", Label = "Runtime Minutes", Value = 41 },
                new SensorChannelValue { Key = "output_source", Label = "Output Source", Value = 5 },
                new SensorChannelValue { Key = "seconds_on_battery", Label = "Seconds On Battery", Value = 7 },
                new SensorChannelValue { Key = "battery_status", Label = "Battery Status", Value = 2 }
            ]);

        var result = UpsSnmpSensorExecutor.ToUpsResult(read, settings);

        Assert.Equal(SensorState.Critical, result.State);
        Assert.Equal(1, Channel(result.Channels, UpsChannels.OnBattery));
        Assert.Equal(SensorState.Critical, result.Channels.First(channel => channel.Key == UpsChannels.OnBattery).State);
        Assert.Equal("%", result.Channels.First(channel => channel.Key == UpsChannels.BatteryCharge).Unit);
        Assert.Equal("Battery charge", result.Channels.First(channel => channel.Key == UpsChannels.BatteryCharge).Label);
        Assert.StartsWith("On battery - 100 % - 41 min left", result.Message);
    }

    [Fact]
    public void An_apc_card_read_through_powernet_gets_the_same_flags_and_runtime_in_minutes()
    {
        var settings = new MonitoringSettings();
        SensorThresholdDefaults.Apply("ups-snmp", settings);
        var read = SensorExecutionResult.Healthy(
            TimeSpan.FromMilliseconds(30),
            "Battery Charge: 96",
            96,
            "battery_charge",
            [
                new SensorChannelValue { Key = "battery_charge", Label = "Battery Charge", Value = 96, IsDefault = true },
                new SensorChannelValue { Key = "apc_runtime", Label = "APC Runtime", Value = 1530, Unit = "s" },
                new SensorChannelValue { Key = "apc_output_status", Label = "APC Output Status", Value = 3 },
                new SensorChannelValue { Key = "apc_battery_status", Label = "APC Battery Status", Value = 2 },
                new SensorChannelValue { Key = "apc_replace_battery", Label = "APC Replace Battery", Value = 2 }
            ]);

        var result = UpsSnmpSensorExecutor.ToUpsResult(read, settings);

        Assert.Equal(SensorState.Critical, result.State);
        Assert.Equal(1, Channel(result.Channels, UpsChannels.OnBattery));
        Assert.Equal(0, Channel(result.Channels, UpsChannels.LowBattery));
        Assert.Equal(1, Channel(result.Channels, UpsChannels.ReplaceBattery));
        Assert.Equal(25.5, Channel(result.Channels, UpsChannels.RuntimeMinutes));
        Assert.StartsWith("On battery - 96 % - 25.5 min left", result.Message);
    }

    [Theory]
    [InlineData(2, false, false)]
    [InlineData(3, true, false)]
    [InlineData(9, false, true)]
    [InlineData(17, false, true)]
    [InlineData(12, false, false)] // on smart trim = still on mains
    public void The_apc_output_status_codes_follow_the_nut_driver(int status, bool onBattery, bool onBypass)
    {
        var reading = UpsChannels.FromApc([new SensorChannelValue { Key = "apc_output_status", Value = status }]);

        Assert.Equal(onBattery, reading.OnBattery);
        Assert.Equal(onBypass, reading.OnBypass);
        Assert.Null(reading.LowBattery); // not read, not guessed
    }

    [Fact]
    public void The_mib_choice_picks_the_oid_list_unless_someone_edited_it()
    {
        var apc = new MonitoringSettings();
        apc.Parameters[UpsSnmpSensorExecutor.ProfileParameter] = "apc";
        // The editor fills the field with the standard list on create - switching to APC must still read APC.
        apc.Parameters["snmp.oids"] = UpsSnmpSensorExecutor.DefaultOids.Replace("\n", "\r\n");
        Assert.Equal(UpsSnmpSensorExecutor.ApcOids, UpsSnmpSensorExecutor.ResolveOids(apc));

        var back = new MonitoringSettings();
        back.Parameters["snmp.oids"] = UpsSnmpSensorExecutor.ApcOids;
        Assert.Equal(UpsSnmpSensorExecutor.DefaultOids, UpsSnmpSensorExecutor.ResolveOids(back));

        apc.Parameters["snmp.oids"] = "1.3.6.1.4.1.318.1.1.1.2.2.1.0|Battery Charge";
        Assert.Equal("1.3.6.1.4.1.318.1.1.1.2.2.1.0|Battery Charge", UpsSnmpSensorExecutor.ResolveOids(apc));
        Assert.All(UpsSnmpSensorExecutor.ApcOids.Split('\n', StringSplitOptions.RemoveEmptyEntries),
            line => Assert.StartsWith("1.3.6.1.4.1.318.1.1.1.", line.Trim()));
    }

    [Fact]
    public void A_failed_snmp_read_is_passed_through_untouched()
    {
        var failed = SensorExecutionResult.Critical(TimeSpan.FromSeconds(3), "timeout");

        Assert.Same(failed, UpsSnmpSensorExecutor.ToUpsResult(failed, new MonitoringSettings()));
    }

    [Fact]
    public void Ups_sensors_poll_every_30_seconds_by_default()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), SensorScheduleDefaults.Resolve("ups-snmp"));
        Assert.Equal(TimeSpan.FromSeconds(30), SensorScheduleDefaults.Resolve("nut-ups"));
    }

    // --- NUT against a stand-in server ---------------------------------------------------------------------

    [Fact]
    public async Task The_nut_sensor_reads_the_first_ups_and_a_power_cut_is_critical()
    {
        await using var server = FakeNutServer.Start(command => command switch
        {
            "LIST UPS" => ["BEGIN LIST UPS", "UPS ups \"Eaton 5E\"", "END LIST UPS"],
            "LIST VAR ups" =>
            [
                "BEGIN LIST VAR ups",
                "VAR ups ups.status \"OB DISCHRG\"",
                "VAR ups battery.charge \"87\"",
                "VAR ups battery.runtime \"1380\"",
                "VAR ups ups.load \"23\"",
                "VAR ups ups.model \"5E 850i \\\"USB\\\"\"",
                "END LIST VAR ups"
            ],
            _ => ["ERR UNKNOWN-COMMAND"]
        });
        var settings = new MonitoringSettings();
        settings.Parameters["nut.port"] = server.Port.ToString();
        SensorThresholdDefaults.Apply("nut-ups", settings);

        var result = await new NutUpsSensorExecutor().ExecuteAsync(new SensorExecutionContext("nut-ups", "127.0.0.1", settings));

        Assert.Equal(SensorState.Critical, result.State);
        Assert.Equal(1, Channel(result.Channels, UpsChannels.OnBattery));
        Assert.Equal(87, Channel(result.Channels, UpsChannels.BatteryCharge));
        Assert.Equal(23, Channel(result.Channels, UpsChannels.RuntimeMinutes));
        Assert.Equal(UpsChannels.BatteryCharge, result.DefaultChannelKey);
        Assert.Equal("On battery - 87 % - 23 min left (5E 850i \"USB\")", result.Message);
    }

    [Fact]
    public async Task On_mains_the_nut_sensor_is_healthy()
    {
        await using var server = FakeNutServer.Start(command => command == "LIST VAR ups"
            ? ["BEGIN LIST VAR ups", "VAR ups ups.status \"OL\"", "VAR ups battery.charge \"100\"", "END LIST VAR ups"]
            : ["ERR UNKNOWN-COMMAND"]);
        var settings = new MonitoringSettings();
        settings.Parameters["nut.port"] = server.Port.ToString();
        settings.Parameters["nut.ups"] = "ups";
        SensorThresholdDefaults.Apply("nut-ups", settings);

        var result = await new NutUpsSensorExecutor().ExecuteAsync(new SensorExecutionContext("nut-ups", "127.0.0.1", settings));

        Assert.Equal(SensorState.Healthy, result.State);
        Assert.Equal("Online - 100 %", result.Message);
    }

    [Theory]
    [InlineData("ERR UNKNOWN-UPS", "no UPS called 'office'")]
    [InlineData("ERR ACCESS-DENIED", "refuses this address")]
    [InlineData("ERR DATA-STALE", "no fresh data")]
    public async Task A_nut_error_is_said_in_words(string answer, string expected)
    {
        await using var server = FakeNutServer.Start(_ => [answer]);
        var settings = new MonitoringSettings();
        settings.Parameters["nut.port"] = server.Port.ToString();
        settings.Parameters["nut.ups"] = "office";

        var result = await new NutUpsSensorExecutor().ExecuteAsync(new SensorExecutionContext("nut-ups", "127.0.0.1", settings));

        Assert.Equal(SensorState.Critical, result.State);
        Assert.Contains(expected, result.Message);
    }

    [Fact]
    public async Task Something_else_on_the_port_is_not_mistaken_for_a_nut_server()
    {
        await using var server = FakeNutServer.Start(_ => ["HTTP/1.1 400 Bad Request"]);
        var settings = new MonitoringSettings();
        settings.Parameters["nut.port"] = server.Port.ToString();
        settings.Parameters["nut.ups"] = "ups";

        var result = await new NutUpsSensorExecutor().ExecuteAsync(new SensorExecutionContext("nut-ups", "127.0.0.1", settings));

        Assert.Equal(SensorState.Critical, result.State);
        Assert.Contains("does not speak the NUT protocol", result.Message);
    }

    [Fact]
    public async Task Nothing_listening_is_said_so()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var settings = new MonitoringSettings();
        settings.Parameters["nut.port"] = port.ToString();

        var result = await new NutUpsSensorExecutor().ExecuteAsync(new SensorExecutionContext("nut-ups", "127.0.0.1", settings));

        Assert.Equal(SensorState.Critical, result.State);
        Assert.Contains("Nothing listens", result.Message);
    }

    /// <summary>
    /// Against a REAL upsd - skipped unless MATMON_NUT_TEST="host:port" points at one whose first UPS is the dummy-ups
    /// driver with <c>ups.status: OB DISCHRG</c>, <c>battery.charge: 87</c>, <c>battery.runtime: 1380</c> (Debian's
    /// nut-server in a container does it). The stand-in above only proves the protocol as this code understands it.
    /// </summary>
    [Fact]
    public async Task A_real_nut_server_is_read_like_the_stand_in()
    {
        var endpoint = Environment.GetEnvironmentVariable("MATMON_NUT_TEST");
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return;
        }

        var settings = new MonitoringSettings();
        settings.Parameters["nut.port"] = endpoint.Split(':')[1];
        SensorThresholdDefaults.Apply("nut-ups", settings);

        var result = await new NutUpsSensorExecutor().ExecuteAsync(new SensorExecutionContext("nut-ups", endpoint.Split(':')[0], settings));

        Assert.Equal(SensorState.Critical, result.State);
        Assert.Equal(1, Channel(result.Channels, UpsChannels.OnBattery));
        Assert.Equal(87, Channel(result.Channels, UpsChannels.BatteryCharge));
        Assert.Equal(23, Channel(result.Channels, UpsChannels.RuntimeMinutes));
    }

    [Fact]
    public void Protocol_fields_keep_quoted_spaces_and_escapes()
    {
        Assert.Equal(["VAR", "ups", "ups.model", "Smart-UPS \"1500\" \\ RM"],
            NutClient.SplitFields("VAR ups ups.model \"Smart-UPS \\\"1500\\\" \\\\ RM\""));
        Assert.Equal(["UPS", "ups", ""], NutClient.SplitFields("UPS ups \"\""));
    }

    // --- migration 7 ---------------------------------------------------------------------------------------

    [Fact]
    public void An_existing_ups_sensor_with_the_old_oid_list_reads_the_power_values_and_alarms_on_a_power_cut()
    {
        var sensors = BootWith(
            UpsSensor("aaaaaaaa-0000-0000-0000-000000000001", "old list", UpsSnmpSensorExecutor.LegacyDefaultOids.Replace("\n", "\r\n")),
            UpsSensor("aaaaaaaa-0000-0000-0000-000000000002", "edited list", "1.3.6.1.4.1.318.1.1.1.2.2.1.0|APC Capacity"));

        var old = sensors.Single(sensor => sensor.Name == "old list");
        Assert.True(UpsSnmpSensorExecutor.IsOidList(old.Settings.Parameters["snmp.oids"], UpsSnmpSensorExecutor.DefaultOids));
        Assert.True(MonitoringSettings.TryReadChannelThreshold(old.Settings, UpsChannels.OnBattery, "critical", out var onBattery));
        Assert.Equal(new ThresholdRule(ThresholdDirection.Above, 0), onBattery);
        Assert.True(MonitoringSettings.TryReadChannelThreshold(old.Settings, UpsChannels.LowBattery, "critical", out _));
        Assert.True(MonitoringSettings.TryReadChannelThreshold(old.Settings, UpsChannels.OnBypass, "warning", out _));

        var edited = sensors.Single(sensor => sensor.Name == "edited list");
        Assert.Equal("1.3.6.1.4.1.318.1.1.1.2.2.1.0|APC Capacity", edited.Settings.Parameters["snmp.oids"]);
        Assert.True(MonitoringSettings.TryReadChannelThreshold(edited.Settings, UpsChannels.OnBattery, "critical", out _));
        // The charge threshold is not re-added: the admin may have removed it on purpose.
        Assert.False(MonitoringSettings.TryReadChannelThreshold(edited.Settings, UpsChannels.BatteryCharge, "warning", out _));
    }

    private static string UpsSensor(string id, string name, string oids) =>
        $$"""{ "$kind": "sensor", "id": "{{id}}", "parentId": "11111111-1111-1111-1111-111111111111", "name": "{{name}}", "sensorTypeKey": "ups-snmp", "target": "10.0.0.5", "settings": { "parameters": { "snmp.oids": {{System.Text.Json.JsonSerializer.Serialize(oids)}} } } }""";

    private IReadOnlyList<SensorElement> BootWith(params string[] sensors)
    {
        var path = Path.Combine(_dir, $"ws-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, $$"""
            {
              "schemaVersion": 6,
              "rootProbe": { "$kind": "probe", "probeId": "primary", "id": "11111111-1111-1111-1111-111111111111", "name": "Primary Probe", "children": [ {{string.Join(",", sensors)}} ] }
            }
            """);

        using var telemetry = new SqliteTelemetryRepository(Path.Combine(_dir, $"t-{Guid.NewGuid():N}.db"));
        using var store = new InMemoryMonitoringWorkspaceStore(
            new UpsTestHostEnvironment(_dir),
            new MatmonRuntimeOptions { WorkspacePath = path },
            new MatmonAuthOptions(),
            new EphemeralDataProtectionProvider(),
            telemetry,
            NullLogger<InMemoryMonitoringWorkspaceStore>.Instance);
        return store.GetAllElements().OfType<SensorElement>().ToArray();
    }

    /// <summary>A NUT server stand-in: answers each command line with the lines the handler returns.</summary>
    private sealed class FakeNutServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        private FakeNutServer(Func<string, string[]> handler)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _loop = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                    catch { return; }

                    _ = Task.Run(async () =>
                    {
                        using (client)
                        {
                            var stream = client.GetStream();
                            using var reader = new StreamReader(stream, Encoding.UTF8);
                            await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
                            try
                            {
                                while (await reader.ReadLineAsync() is { } command && command != "LOGOUT")
                                {
                                    foreach (var line in handler(command))
                                    {
                                        await writer.WriteLineAsync(line);
                                    }
                                }
                            }
                            catch (IOException)
                            {
                                // the client went away
                            }
                        }
                    });
                }
            });
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public static FakeNutServer Start(Func<string, string[]> handler) => new(handler);

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            try { await _loop; } catch { /* stopped */ }
        }
    }
}

file sealed class UpsTestHostEnvironment(string contentRoot) : IHostEnvironment
{
    public string ApplicationName { get; set; } = "Matmon.Tests";
    public string EnvironmentName { get; set; } = "Development";
    public string ContentRootPath { get; set; } = contentRoot;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
