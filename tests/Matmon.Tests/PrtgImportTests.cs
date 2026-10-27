using System.Net;
using Matmon.Core.Domain;
using Matmon.Core.Import;
using Matmon.Core.Telemetry;
using Matmon.Host.Services;
using Matmon.Host.Services.Import;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

/// <summary>
/// Import from PRTG: reading PRTG's sensor tree, mapping its sensor types onto Matmon's, and the import itself - against a
/// stand-in PRTG (the tree and getobjectproperty answers below follow PRTG's API documentation; not checked against a real PRTG).
/// </summary>
public sealed class PrtgImportTests : IDisposable
{
    private const string SampleTree = """
        <?xml version="1.0" encoding="UTF-8"?>
        <prtg>
          <version>24.1.92.1554</version>
          <sensortree>
            <nodes>
              <group id="0">
                <name>Root</name>
                <probenode id="1">
                  <name>Local Probe</name>
                  <sensor id="1001"><name>Core Health</name><sensortype>Core Health</sensortype><sensorkind>corehealth</sensorkind><interval>60</interval></sensor>
                  <group id="2001">
                    <name>Servers</name>
                    <tags>prod</tags>
                    <device id="3001">
                      <name>web-01</name>
                      <host>10.0.0.10</host>
                      <tags>web linux</tags>
                      <sensor id="4001"><name>Ping</name><sensortype>Ping</sensortype><sensorkind>ping</sensorkind><interval>30 seconds</interval><status>Up</status><status_raw>3</status_raw><tags>pingsensor</tags></sensor>
                      <sensor id="4002"><name>HTTP</name><sensortype>HTTP</sensortype><sensorkind>http</sensorkind><interval>300</interval></sensor>
                      <sensor id="4003"><name>Port 8443</name><sensortype>Port</sensortype><sensorkind>port</sensorkind><interval>60</interval><status>Paused (paused by user)</status><status_raw>7</status_raw></sensor>
                      <sensor id="4004"><name>SSH Load</name><sensortype>SSH Load Average</sensortype><sensorkind>sshloadavg</sensorkind><interval>60</interval></sensor>
                      <sensor id="4005"><name>Exchange</name><sensortype>Exchange Backup (PowerShell)</sensortype><sensorkind>exchangepsbackup</sensorkind></sensor>
                    </device>
                  </group>
                </probenode>
              </group>
            </nodes>
          </sensortree>
        </prtg>
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "matmon-prtg-" + Guid.NewGuid().ToString("N"));

    public PrtgImportTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void The_sensor_tree_is_read_with_probes_groups_devices_and_sensors()
    {
        var tree = PrtgSensorTree.Parse(SampleTree);

        var probe = Assert.Single(tree);
        Assert.Equal((PrtgNodeKind.Probe, "Local Probe"), (probe.Kind, probe.Name));
        var device = probe.Descendants().Single(node => node.Kind == PrtgNodeKind.Device);
        Assert.Equal(("web-01", "10.0.0.10"), (device.Name, device.Host));
        Assert.Equal(["web", "linux"], device.Tags);

        var ping = device.Children.Single(node => node.Id == 4001);
        Assert.Equal(("ping", 30, false), (ping.SensorKind, ping.IntervalSeconds, ping.Paused));
        Assert.True(device.Children.Single(node => node.Id == 4003).Paused);
        Assert.Equal(6, probe.Descendants().Count(node => node.Kind == PrtgNodeKind.Sensor));
    }

    [Theory]
    [InlineData("ping", "Ping", PrtgMappingLevel.Supported, "ping")]
    [InlineData("pingjitter", "Ping Jitter", PrtgMappingLevel.Supported, "ping")]
    [InlineData("httpadvanced", "HTTP Advanced", PrtgMappingLevel.Supported, "http")]
    [InlineData("port", "Port", PrtgMappingLevel.Supported, "tcp-port")]
    [InlineData("sslcertificate", "SSL Certificate", PrtgMappingLevel.Supported, "ssl-certificate")]
    [InlineData("dnsv2", "DNS v2", PrtgMappingLevel.Partial, "dns")]
    [InlineData("snmptraffic", "SNMP Traffic", PrtgMappingLevel.Partial, "snmp-interface")]
    [InlineData("wmiservice", "WMI Service", PrtgMappingLevel.Partial, "windows-service")]
    [InlineData("wmicpu", "WMI CPU Load", PrtgMappingLevel.Partial, "windows-health")]
    [InlineData("sshdiskfree", "SSH Disk Free", PrtgMappingLevel.Partial, "linux-disk")]
    [InlineData("probehealth", "Probe Health", PrtgMappingLevel.Covered, null)]
    [InlineData("exexml", "EXE/Script Advanced", PrtgMappingLevel.Unsupported, null)]
    public void A_prtg_sensor_type_maps_onto_a_matmon_sensor_type(string kind, string type, PrtgMappingLevel level, string? matmonType)
    {
        var mapping = PrtgSensorMapping.Map(kind, type);
        Assert.Equal(level, mapping.Level);
        Assert.Equal(matmonType, mapping.SensorTypeKey);
    }

    [Fact]
    public void The_settings_come_from_the_prtg_sensor_s_properties()
    {
        var http = PrtgSensorMapping.Settings("http", new Dictionary<string, string> { ["httpurl"] = "/health" }, "10.0.0.10");
        Assert.Equal(("http://10.0.0.10/health", null), (http.Target, http.Missing));

        var port = PrtgSensorMapping.Settings("tcp-port", new Dictionary<string, string> { ["port"] = "8443" }, "10.0.0.10");
        Assert.Equal("8443", port.Parameters["tcp.port"]);
        Assert.Null(port.Missing);
        Assert.NotNull(PrtgSensorMapping.Settings("tcp-port", new Dictionary<string, string>(), "x").Missing);

        var service = PrtgSensorMapping.Settings("windows-service", new Dictionary<string, string> { ["service"] = "Spooler" }, "x");
        Assert.Equal("Spooler", service.Parameters["windows.serviceName"]);
        Assert.Contains("credential", service.Missing);
    }

    [Fact]
    public async Task The_chosen_part_is_imported_once_with_folders_hosts_and_sensors()
    {
        var tree = PrtgSensorTree.Parse(SampleTree);
        var handler = new StandInPrtg();
        using var client = new PrtgClient(new PrtgConnection("https://prtg.test", "tok", null, null, null, false), handler);
        using var telemetry = new SqliteTelemetryRepository(Path.Combine(_dir, "telemetry.db"));
        using var store = new InMemoryMonitoringWorkspaceStore(new PrtgTestHostEnvironment(_dir),
            new MatmonRuntimeOptions { WorkspacePath = Path.Combine(_dir, "workspace.json") }, new MatmonAuthOptions(),
            new EphemeralDataProtectionProvider(), telemetry, NullLogger<InMemoryMonitoringWorkspaceStore>.Instance);
        var root = store.GetAllElements().OfType<ProbeElement>().First(probe => probe.ParentId is null);
        var importer = new PrtgImporter(store, () => (true, null));
        var chosen = new HashSet<int> { 1001, 4001, 4002, 4003, 4004, 4005 };

        var report = await importer.ImportAsync(client, tree, root.Id, chosen, probesAsFolders: true, default);

        Assert.Equal((2, 1, 4), (report.FoldersCreated, report.HostsCreated, report.SensorsCreated)); // Local Probe + Servers; web-01; ping, http, port, ssh
        var elements = store.GetAllElements();
        var host = elements.OfType<HostElement>().Single(h => h.Name == "web-01");
        Assert.Equal("10.0.0.10", host.Address);
        Assert.Contains("web", host.Tags);
        var sensors = elements.OfType<SensorElement>().Where(s => s.ParentId == host.Id).ToDictionary(s => s.Name);
        Assert.Equal("http://10.0.0.10/health", sensors["HTTP"].Target);
        Assert.Equal("8443", sensors["Port 8443"].Settings.Parameters["tcp.port"]);
        Assert.True(sensors["Port 8443"].IsPaused);
        Assert.Equal(TimeSpan.FromSeconds(30), sensors["Ping"].Settings.PollingInterval);
        Assert.Equal("linux-ssh-health", sensors["SSH Load"].SensorTypeKey);
        Assert.Contains(report.ToComplete, item => item.Path.EndsWith("SSH Load", StringComparison.Ordinal));
        Assert.Equal(2, report.NotImported.Count); // Core Health (built in) and the Exchange backup (no match)

        // twice: nothing new
        var again = await importer.ImportAsync(client, tree, root.Id, chosen, probesAsFolders: true, default);
        Assert.Equal((0, 0, 0, 4), (again.FoldersCreated, again.HostsCreated, again.SensorsCreated, again.SensorsSkippedExisting));
    }

    [Fact]
    public async Task The_sensor_limit_stops_the_import_and_says_why()
    {
        var tree = PrtgSensorTree.Parse(SampleTree);
        using var client = new PrtgClient(new PrtgConnection("https://prtg.test", "tok", null, null, null, false), new StandInPrtg());
        using var telemetry = new SqliteTelemetryRepository(Path.Combine(_dir, "telemetry2.db"));
        using var store = new InMemoryMonitoringWorkspaceStore(new PrtgTestHostEnvironment(_dir),
            new MatmonRuntimeOptions { WorkspacePath = Path.Combine(_dir, "workspace2.json") }, new MatmonAuthOptions(),
            new EphemeralDataProtectionProvider(), telemetry, NullLogger<InMemoryMonitoringWorkspaceStore>.Instance);
        var root = store.GetAllElements().OfType<ProbeElement>().First(probe => probe.ParentId is null);
        var left = 1;
        var importer = new PrtgImporter(store, () => left-- > 0 ? (true, null) : (false, "The plan allows no more sensors."));

        var report = await importer.ImportAsync(client, tree, root.Id, new HashSet<int> { 4001, 4002, 4003 }, probesAsFolders: false, default);

        Assert.Equal(1, report.SensorsCreated);
        Assert.Equal("The plan allows no more sensors.", report.LimitReason);
    }

    [Fact]
    public async Task A_refused_login_is_said_in_words()
    {
        using var client = new PrtgClient(new PrtgConnection("https://prtg.test", "wrong", null, null, null, false), new StandInPrtg { Refuse = true });
        var ex = await Assert.ThrowsAsync<PrtgException>(() => client.GetSensorTreeAsync(default));
        Assert.Contains("refused the login", ex.Message);
    }

    private sealed class StandInPrtg : HttpMessageHandler
    {
        public bool Refuse { get; init; }
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);
            if (Refuse)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            }
            string body;
            if (url.Contains("/api/table.xml", StringComparison.Ordinal))
            {
                body = SampleTree;
            }
            else
            {
                var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
                var value = (query["id"], query["name"]) switch
                {
                    ("4002", "httpurl") => "/health",
                    ("4003", "port") => "8443",
                    _ => "(Property not found)"
                };
                body = $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><prtg><version>24.1</version><result>{value}</result></prtg>";
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}

file sealed class PrtgTestHostEnvironment(string contentRoot) : IHostEnvironment
{
    public string ApplicationName { get; set; } = "Matmon.Tests";
    public string EnvironmentName { get; set; } = "Development";
    public string ContentRootPath { get; set; } = contentRoot;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
