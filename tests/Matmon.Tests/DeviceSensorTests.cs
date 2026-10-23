using System.Globalization;
using Matmon.Core.Domain;

namespace Matmon.Tests;

/// <summary>
/// Device sensors of the second batch. The printer is read from what an SNMP walk of the Printer-MIB returns (the shapes
/// RFC 3805 / RFC 2790 define), and against a real Net-SNMP agent serving a colour laser's tables when
/// MATMON_PRINTER_TEST="host:port" points at one.
/// </summary>
public sealed class DeviceSensorTests
{
    // ---- Printer ---------------------------------------------------------------------------------------------------------

    private static SnmpDiscoveryItem Item(string oid, string value, string syntax = "Integer", string? rawHex = null) =>
        new(oid, syntax, value, double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _), false) { RawHex = rawHex };

    private static List<SnmpDiscoveryItem> ColourLaser(string errorStateHex = "00", string errorStateText = "00", params SnmpDiscoveryItem[] extra)
    {
        var items = new List<SnmpDiscoveryItem>
        {
            Item("1.3.6.1.2.1.25.3.5.1.1.1", "3"),
            Item("1.3.6.1.2.1.25.3.5.1.2.1", errorStateText, "OctetString", errorStateHex),
            Item("1.3.6.1.2.1.25.3.2.1.3.1", "HP Color LaserJet MFP M479fdw", "OctetString", "x"),
            Item("1.3.6.1.2.1.43.10.2.1.4.1.1", "48213", "Counter32"),
            Item("1.3.6.1.2.1.43.8.2.1.9.1.1", "100"), Item("1.3.6.1.2.1.43.8.2.1.10.1.1", "0"), Item("1.3.6.1.2.1.43.8.2.1.13.1.1", "Tray 1", "OctetString", "x"),
            Item("1.3.6.1.2.1.43.8.2.1.9.1.2", "250"), Item("1.3.6.1.2.1.43.8.2.1.10.1.2", "150"), Item("1.3.6.1.2.1.43.8.2.1.13.1.2", "Tray 2", "OctetString", "x")
        };
        (string Name, int Class, int Level)[] supplies =
        [
            ("Black Cartridge HP 415A", 3, 62), ("Cyan Cartridge HP 415A", 3, 18), ("Magenta Cartridge HP 415A", 3, 5),
            ("Yellow Cartridge HP 415A", 3, -3), ("Toner Collection Unit", 4, 80), ("Imaging Drum", 3, 47)
        ];
        for (var index = 0; index < supplies.Length; index++)
        {
            var suffix = "1." + (index + 1);
            items.Add(Item("1.3.6.1.2.1.43.11.1.1.4." + suffix, supplies[index].Class.ToString(CultureInfo.InvariantCulture)));
            items.Add(Item("1.3.6.1.2.1.43.11.1.1.6." + suffix, supplies[index].Name, "OctetString", "x"));
            items.Add(Item("1.3.6.1.2.1.43.11.1.1.8." + suffix, "100"));
            items.Add(Item("1.3.6.1.2.1.43.11.1.1.9." + suffix, supplies[index].Level.ToString(CultureInfo.InvariantCulture)));
        }

        items.AddRange(extra);
        return items;
    }

    private static MonitoringSettings PrinterDefaults()
    {
        var settings = new MonitoringSettings();
        SensorThresholdDefaults.Apply("snmp-printer", settings);
        return settings;
    }

    [Fact]
    public void Printer_reads_supplies_trays_pages_and_a_healthy_state()
    {
        var reading = PrinterSensorExecutor.Parse(ColourLaser());
        var result = PrinterSensorExecutor.BuildResult(reading, TimeSpan.Zero);

        Assert.Equal(SensorState.Healthy, result.State);
        Assert.Equal("supplyMinPercent", result.DefaultChannelKey);
        Assert.Equal(5, Channel(result, "supplyMinPercent")); // magenta; yellow says "some left" (-3) and has no percentage
        Assert.Equal(62, Channel(result, "supply.black-cartridge-hp-415a"));
        Assert.Equal(80, Channel(result, "supply.toner-collection-unit"));
        Assert.Equal("Toner Collection Unit (space left)", result.Channels.Single(channel => channel.Key == "supply.toner-collection-unit").Label);
        Assert.DoesNotContain(result.Channels, channel => channel.Key == "supply.yellow-cartridge-hp-415a");
        Assert.Equal(48213, Channel(result, "pageCount"));
        Assert.Equal(1, Channel(result, "traysEmpty"));
        Assert.Equal(60, Channel(result, "tray.tray-2"));
        Assert.Equal("HP Color LaserJet MFP M479fdw: idle (Magenta Cartridge HP 415A 5 %, 48,213 pages)", result.Message);
    }

    [Fact]
    public void Printer_error_bits_are_read_from_the_bytes_not_from_the_text()
    {
        // 0x20 = low toner, which the walk shows as a single space; 0x04 0x00 = paper jam.
        var lowToner = PrinterSensorExecutor.BuildResult(PrinterSensorExecutor.Parse(ColourLaser("20", " ")), TimeSpan.Zero);
        var jam = PrinterSensorExecutor.BuildResult(PrinterSensorExecutor.Parse(ColourLaser("0400", "0400")), TimeSpan.Zero);
        var trayMissing = PrinterSensorExecutor.BuildResult(PrinterSensorExecutor.Parse(ColourLaser("0080", "0080")), TimeSpan.Zero);

        Assert.Equal(SensorState.Warning, lowToner.State);
        Assert.Contains("idle - low toner", lowToner.Message);
        Assert.Equal(SensorState.Critical, jam.State);
        Assert.Contains("paper jam", jam.Message);
        Assert.Equal(SensorState.Warning, trayMissing.State);
        Assert.Contains("input tray missing", trayMissing.Message);
    }

    [Fact]
    public void Printer_alerts_count_and_are_named()
    {
        var warning = PrinterSensorExecutor.BuildResult(PrinterSensorExecutor.Parse(ColourLaser(extra:
        [
            Item("1.3.6.1.2.1.43.18.1.1.2.1.1", "4"), Item("1.3.6.1.2.1.43.18.1.1.8.1.1", "Magenta cartridge very low", "OctetString", "x")
        ])), TimeSpan.Zero);
        var critical = PrinterSensorExecutor.BuildResult(PrinterSensorExecutor.Parse(ColourLaser(extra:
        [
            Item("1.3.6.1.2.1.43.18.1.1.2.1.7", "3"), Item("1.3.6.1.2.1.43.18.1.1.8.1.7", "Fuser error 50.2", "OctetString", "x")
        ])), TimeSpan.Zero);

        Assert.Equal(SensorState.Warning, warning.State);
        Assert.Equal(1, Channel(warning, "warningAlerts"));
        Assert.Contains("Magenta cartridge very low", warning.Message);
        Assert.Equal(SensorState.Critical, critical.State);
        Assert.Equal(1, Channel(critical, "criticalAlerts"));
        Assert.Contains("Fuser error 50.2", critical.Message);
    }

    [Fact]
    public void Printer_the_default_threshold_warns_on_a_low_supply_and_no_printer_mib_says_so()
    {
        var result = SensorThresholdEvaluator.ApplyChannelThresholds(PrinterDefaults(),
            PrinterSensorExecutor.BuildResult(PrinterSensorExecutor.Parse(ColourLaser()), TimeSpan.Zero));
        var notAPrinter = PrinterSensorExecutor.BuildResult(PrinterSensorExecutor.Parse([Item("1.3.6.1.2.1.25.3.2.1.3.768", "GenuineIntel", "OctetString", "x")]), TimeSpan.Zero);

        Assert.Equal(SensorState.Warning, result.State); // 5 % magenta is under 10
        Assert.Equal(SensorState.Critical, notAPrinter.State);
        Assert.Contains("no Printer-MIB", notAPrinter.Message);
    }

    /// <summary>Against a real Net-SNMP agent whose "pass" script serves the colour laser above with the low-toner bit and
    /// one warning alert - skipped unless MATMON_PRINTER_TEST="host:port".</summary>
    [Fact]
    public async Task Printer_a_real_snmp_agent_is_read_like_the_walk_above()
    {
        var endpoint = Environment.GetEnvironmentVariable("MATMON_PRINTER_TEST");
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return;
        }

        var settings = PrinterDefaults();
        settings.Parameters["snmp.community"] = "public";
        settings.Parameters["snmp.version"] = "v2c";
        settings.Parameters["snmp.port"] = endpoint.Split(':')[1];

        var result = await new PrinterSensorExecutor().ExecuteAsync(new SensorExecutionContext("snmp-printer", endpoint.Split(':')[0], settings));

        Assert.Equal(SensorState.Warning, result.State);
        Assert.Equal(5, Channel(result, "supplyMinPercent"));
        Assert.Equal(48213, Channel(result, "pageCount"));
        Assert.Equal(1, Channel(result, "traysEmpty"));
        Assert.Equal(1, Channel(result, "warningAlerts"));
        Assert.Equal("HP Color LaserJet MFP M479fdw: idle - low toner, Magenta cartridge very low (Magenta Cartridge HP 415A 5 %, 48,213 pages)", result.Message);
    }

    // ---- Domain expiry ---------------------------------------------------------------------------------------------------

    private const string Bootstrap = """
        { "services": [ [ ["com", "net"], ["https://rdap.verisign.com/com/v1/"] ], [ ["uk"], ["http://x.example/", "https://rdap.nominet.uk/uk/"] ] ] }
        """;

    private static string RdapAnswer(string name, DateTimeOffset expires, params string[] statuses) => $$"""
        { "ldhName": "{{name.ToUpperInvariant()}}",
          "status": [{{string.Join(", ", statuses.Select(status => $"\"{status}\""))}}],
          "events": [ { "eventAction": "registration", "eventDate": "1997-09-15T04:00:00Z" },
                      { "eventAction": "expiration", "eventDate": "{{expires.UtcDateTime:yyyy-MM-dd'T'HH:mm:ss'Z'}}" } ],
          "entities": [ { "roles": ["registrar"], "vcardArray": ["vcard", [["version", {}, "text", "4.0"], ["fn", {}, "text", "MarkMonitor Inc."]]] } ] }
        """;

    private static DomainExpirySensorExecutor Domains(Dictionary<string, string> rdap, Dictionary<string, string>? whois = null, List<string>? asked = null) =>
        new(() => new FakeHttp(request =>
        {
            asked?.Add(request.RequestUri!.ToString());
            var url = request.RequestUri!.ToString();
            if (url == "https://data.iana.org/rdap/dns.json")
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(Bootstrap) };
            }

            return rdap.TryGetValue(url, out var json)
                ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json) }
                : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        }), (server, query, _) =>
        {
            asked?.Add($"whois {server} {query}");
            return Task.FromResult(whois is not null && whois.TryGetValue(server + " " + query, out var answer) ? answer : "No match for \"" + query + "\".");
        });

    private static MonitoringSettings DomainSettings(string? name = null)
    {
        var settings = new MonitoringSettings();
        if (name is not null)
        {
            settings.Parameters["domain.name"] = name;
        }

        SensorThresholdDefaults.Apply("domain-expiry", settings);
        return settings;
    }

    [Fact]
    public async Task Domain_rdap_gives_the_days_left_and_the_registrar_and_a_subdomain_is_walked_up()
    {
        var asked = new List<string>();
        var expires = DateTimeOffset.UtcNow.AddDays(400.5);
        var result = await Domains(new() { ["https://rdap.verisign.com/com/v1/domain/example.com"] = RdapAnswer("example.com", expires, "client transfer prohibited") }, asked: asked)
            .ExecuteAsync(new SensorExecutionContext("domain-expiry", "https://shop.example.com/cart", DomainSettings()));

        Assert.Equal(SensorState.Healthy, result.State);
        Assert.Equal(400, Channel(result, "daysUntilExpiry"));
        Assert.Equal(1, Channel(result, "registered"));
        Assert.Equal($"example.com expires on {expires.UtcDateTime:yyyy-MM-dd} (in 400 days), registrar MarkMonitor Inc.", result.Message);
        Assert.Contains("https://rdap.verisign.com/com/v1/domain/shop.example.com", asked); // 404 first, then the parent
    }

    [Fact]
    public async Task Domain_close_to_expiry_or_in_redemption_alarms()
    {
        var soon = await Domains(new() { ["https://rdap.verisign.com/com/v1/domain/example.com"] = RdapAnswer("example.com", DateTimeOffset.UtcNow.AddDays(20.5)) })
            .ExecuteAsync(new SensorExecutionContext("domain-expiry", "example.com", DomainSettings()));
        var redemption = await Domains(new() { ["https://rdap.verisign.com/com/v1/domain/example.com"] = RdapAnswer("example.com", DateTimeOffset.UtcNow.AddDays(300), "redemption period") })
            .ExecuteAsync(new SensorExecutionContext("domain-expiry", "example.com", DomainSettings()));
        var expired = await Domains(new() { ["https://rdap.verisign.com/com/v1/domain/example.com"] = RdapAnswer("example.com", DateTimeOffset.UtcNow.AddDays(-3.5)) })
            .ExecuteAsync(new SensorExecutionContext("domain-expiry", "example.com", DomainSettings()));
        var free = await Domains(new()).ExecuteAsync(new SensorExecutionContext("domain-expiry", "", DomainSettings("nobody-has-this.com")));

        Assert.Equal(SensorState.Warning, soon.State);
        Assert.Equal(SensorState.Critical, redemption.State);
        Assert.Contains("status: redemption period", redemption.Message);
        Assert.Equal(SensorState.Critical, expired.State);
        Assert.Contains("EXPIRED", expired.Message);
        Assert.Equal(SensorState.Critical, free.State);
        Assert.Contains("is not registered", free.Message);
    }

    [Fact]
    public async Task Domain_without_rdap_goes_to_the_whois_iana_names()
    {
        var whois = new Dictionary<string, string>
        {
            ["whois.iana.org io"] = "domain: IO\nwhois:        whois.nic.io\nstatus: ACTIVE\n",
            ["whois.nic.io github.io"] = "Domain Name: github.io\nCreation Date: 2013-03-08T19:12:48Z\nRegistry Expiry Date: 2027-03-08T19:12:48Z\nDomain Status: clientTransferProhibited https://icann.org/epp#clientTransferProhibited\n",
            ["whois.iana.org de"] = "domain: DE\nwhois:        whois.denic.de\n",
            ["whois.denic.de denic.de"] = "Domain: denic.de\nStatus: connect\n",
            ["whois.denic.de nobody-has-this.de"] = "Domain: nobody-has-this.de\nStatus: free\n",
            ["whois.iana.org eu"] = "whois:        whois.eu\n",
            ["whois.eu europa.eu"] = "Domain: europa.eu\nScript: LATIN\nRegistrar:\n        Name: ClearMedia NV\n"
        };

        var io = await Domains(new(), whois).ExecuteAsync(new SensorExecutionContext("domain-expiry", "github.io", DomainSettings()));
        var de = await Domains(new(), whois).ExecuteAsync(new SensorExecutionContext("domain-expiry", "www.denic.de", DomainSettings()));
        var freeDe = await Domains(new(), whois).ExecuteAsync(new SensorExecutionContext("domain-expiry", "nobody-has-this.de", DomainSettings()));
        var eu = await Domains(new(), whois).ExecuteAsync(new SensorExecutionContext("domain-expiry", "europa.eu", DomainSettings()));

        Assert.Equal(SensorState.Healthy, io.State);
        Assert.StartsWith("github.io expires on 2027-03-08", io.Message);
        Assert.Equal(SensorState.Healthy, de.State);
        Assert.Equal("denic.de is registered - the .de registry publishes no expiry date, so only the registration is checked", de.Message);
        Assert.Null(Channel(de, "daysUntilExpiry"));
        Assert.Equal("registered", de.DefaultChannelKey);
        Assert.Equal(SensorState.Critical, freeDe.State);
        Assert.Equal(SensorState.Healthy, eu.State);
        Assert.StartsWith("europa.eu is registered", eu.Message);
    }

    [Theory]
    [InlineData("https://www.Example.COM:8443/path?q=1", "example.com")]
    [InlineData("shop.example.co.uk", "shop.example.co.uk")]
    [InlineData("bücher.de", "xn--bcher-kva.de")]
    [InlineData("example.com.", "example.com")]
    [InlineData("192.168.1.10", null)]
    [InlineData("localhost", null)]
    [InlineData("", null)]
    public void Domain_names_are_normalised(string input, string? expected) =>
        Assert.Equal(expected, DomainExpirySensorExecutor.NormalizeDomain(input));

    [Fact]
    public void Domain_bootstrap_prefers_https_and_whois_dates_come_in_many_shapes()
    {
        var servers = DomainExpirySensorExecutor.ParseBootstrap(Bootstrap);
        Assert.Equal("https://rdap.nominet.uk/uk/", servers["uk"]);
        Assert.Equal("https://rdap.verisign.com/com/v1/", servers["net"]);

        Assert.Equal(new DateTime(2027, 3, 8), DomainExpirySensorExecutor.ParseWhois("a.xx", "xx", "Expiry date: 08-Mar-2027").Expires!.Value.Date);
        Assert.Equal(new DateTime(2027, 3, 8), DomainExpirySensorExecutor.ParseWhois("a.xx", "xx", "paid-till: 2027.03.08").Expires!.Value.Date);
        Assert.Equal(new DateTime(2027, 3, 8), DomainExpirySensorExecutor.ParseWhois("a.xx", "xx", "Expiration Date: 08.03.2027").Expires!.Value.Date);
        Assert.False(DomainExpirySensorExecutor.ParseWhois("a.xx", "xx", "NOT FOUND").Registered);
    }

    /// <summary>Against the real registries - skipped unless MATMON_RDAP_TEST=1: Verisign RDAP (.com), WHOIS for .io and
    /// DENIC (.de), and two names nobody has.</summary>
    [Fact]
    public async Task Domain_real_registries_answer()
    {
        if (Environment.GetEnvironmentVariable("MATMON_RDAP_TEST") != "1")
        {
            return;
        }

        var executor = new DomainExpirySensorExecutor();
        var com = await executor.ExecuteAsync(new SensorExecutionContext("domain-expiry", "www.google.com", DomainSettings()));
        var io = await executor.ExecuteAsync(new SensorExecutionContext("domain-expiry", "github.io", DomainSettings()));
        var de = await executor.ExecuteAsync(new SensorExecutionContext("domain-expiry", "denic.de", DomainSettings()));
        var freeCom = await executor.ExecuteAsync(new SensorExecutionContext("domain-expiry", "matmon-not-registered-xyz123.com", DomainSettings()));
        var freeDe = await executor.ExecuteAsync(new SensorExecutionContext("domain-expiry", "matmon-not-registered-xyz123.de", DomainSettings()));

        Assert.Equal(SensorState.Healthy, com.State);
        Assert.StartsWith("google.com expires on 2028-09-14", com.Message);
        Assert.Equal(SensorState.Healthy, io.State);
        Assert.Contains("github.io expires on", io.Message);
        Assert.Equal(SensorState.Healthy, de.State);
        Assert.StartsWith("denic.de is registered", de.Message);
        Assert.Equal(SensorState.Critical, freeCom.State);
        Assert.Equal(SensorState.Critical, freeDe.State);
    }

    // ---- SIP -------------------------------------------------------------------------------------------------------------

    /// <summary>A UDP stand-in: answers each OPTIONS with the given status lines (a 100 first, then the final one), echoing
    /// Via / From / To / Call-ID / CSeq like a real server.</summary>
    private static async Task<(int Port, Task Server)> SipServerAsync(CancellationToken stop, params string[] statusLines)
    {
        var udp = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        var port = ((System.Net.IPEndPoint)udp.Client.LocalEndPoint!).Port;
        var server = Task.Run(async () =>
        {
            using (udp)
            {
                try
                {
                    var request = await udp.ReceiveAsync(stop);
                    var text = System.Text.Encoding.ASCII.GetString(request.Buffer);
                    var echoed = string.Join("\r\n", text.Split("\r\n").Where(line => line.StartsWith("Via:") || line.StartsWith("From:") || line.StartsWith("To:") || line.StartsWith("Call-ID:") || line.StartsWith("CSeq:")));
                    foreach (var status in statusLines)
                    {
                        var answer = $"{status}\r\n{echoed}\r\nServer: FPBX-16.0.40(18.20.0)\r\nContent-Length: 0\r\n\r\n";
                        await udp.SendAsync(System.Text.Encoding.ASCII.GetBytes(answer), request.RemoteEndPoint, stop);
                    }
                }
                catch (OperationCanceledException)
                {
                    // test over
                }
            }
        }, stop);
        await Task.Yield();
        return (port, server);
    }

    private static MonitoringSettings SipSettings(int port, string transport = "udp", double timeoutSeconds = 3)
    {
        var settings = new MonitoringSettings { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
        settings.Parameters["sip.port"] = port.ToString(CultureInfo.InvariantCulture);
        settings.Parameters["sip.transport"] = transport;
        return settings;
    }

    [Fact]
    public async Task Sip_options_is_answered_and_a_provisional_answer_is_skipped()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (port, _) = await SipServerAsync(stop.Token, "SIP/2.0 100 Trying", "SIP/2.0 200 OK");

        var result = await new SipSensorExecutor().ExecuteAsync(new SensorExecutionContext("sip", "127.0.0.1", SipSettings(port)));

        Assert.Equal(SensorState.Healthy, result.State);
        Assert.Equal(200, Channel(result, "statusCode"));
        Assert.Equal("responseTime", result.DefaultChannelKey);
        Assert.Equal($"UDP 127.0.0.1:{port} answers: 200 OK (FPBX-16.0.40(18.20.0))", result.Message);
    }

    [Fact]
    public async Task Sip_a_login_request_is_alive_and_a_5xx_is_a_warning()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (loginPort, _) = await SipServerAsync(stop.Token, "SIP/2.0 401 Unauthorized");
        var (busyPort, _) = await SipServerAsync(stop.Token, "SIP/2.0 503 Service Unavailable");

        var login = await new SipSensorExecutor().ExecuteAsync(new SensorExecutionContext("sip", "127.0.0.1", SipSettings(loginPort)));
        var busy = await new SipSensorExecutor().ExecuteAsync(new SensorExecutionContext("sip", "127.0.0.1", SipSettings(busyPort)));

        Assert.Equal(SensorState.Healthy, login.State);
        Assert.Contains("answers and wants a login: 401 Unauthorized", login.Message);
        Assert.Equal(SensorState.Warning, busy.State);
        Assert.Contains("answers but cannot serve: 503 Service Unavailable", busy.Message);
    }

    [Fact]
    public async Task Sip_silence_is_critical()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (port, _) = await SipServerAsync(stop.Token); // receives, never answers

        var result = await new SipSensorExecutor().ExecuteAsync(new SensorExecutionContext("sip", "127.0.0.1", SipSettings(port, timeoutSeconds: 1)));

        Assert.Equal(SensorState.Critical, result.State);
        Assert.Contains("No SIP answer", result.Message);
    }

    [Fact]
    public void Sip_request_and_response_shapes()
    {
        var request = SipSensorExecutor.BuildOptions("pbx.example", 5080, "100", "UDP", new System.Net.IPEndPoint(System.Net.IPAddress.Parse("10.0.0.5"), 40000), "abc@matmon", "t1", "z9hG4bKb1");
        Assert.StartsWith("OPTIONS sip:100@pbx.example:5080 SIP/2.0\r\nVia: SIP/2.0/UDP 10.0.0.5:40000;branch=z9hG4bKb1;rport\r\n", request);
        Assert.Contains("Call-ID: abc@matmon\r\nCSeq: 1 OPTIONS\r\n", request);
        Assert.EndsWith("Content-Length: 0\r\n\r\n", request);

        var (answer, callId) = SipSensorExecutor.ParseResponse("SIP/2.0 200 OK\r\ni: xyz@host\r\nUser-Agent: FRITZ!OS\r\n\r\n");
        Assert.Equal(200, answer.Status);
        Assert.Equal("FRITZ!OS", answer.Product);
        Assert.Equal("xyz@host", callId);
        Assert.Throws<FormatException>(() => SipSensorExecutor.ParseResponse("HTTP/1.1 400 Bad Request\r\n\r\n"));
    }

    /// <summary>Against a real SIP server (Debian's Kamailio with its stock configuration answers OPTIONS to itself) - skipped
    /// unless MATMON_SIP_TEST="host:port"; MATMON_SIP_TEST_TCP=1 asks over TCP as well.</summary>
    [Fact]
    public async Task Sip_a_real_server_answers()
    {
        var endpoint = Environment.GetEnvironmentVariable("MATMON_SIP_TEST");
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return;
        }

        var host = endpoint.Split(':')[0];
        var port = int.Parse(endpoint.Split(':')[1], CultureInfo.InvariantCulture);
        var udp = await new SipSensorExecutor().ExecuteAsync(new SensorExecutionContext("sip", host, SipSettings(port)));
        Assert.True(udp.State == SensorState.Healthy, udp.Message);
        Assert.Contains("kamailio", udp.Message, StringComparison.OrdinalIgnoreCase);

        if (Environment.GetEnvironmentVariable("MATMON_SIP_TEST_TCP") == "1")
        {
            var tcp = await new SipSensorExecutor().ExecuteAsync(new SensorExecutionContext("sip", host, SipSettings(port, "tcp")));
            Assert.True(tcp.State == SensorState.Healthy, tcp.Message);
            Assert.StartsWith("TCP ", tcp.Message);
        }
    }

    // ---- LDAP ------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("(objectClass=*)", "870B6F626A656374436C617373")]
    [InlineData("objectClass=*", "870B6F626A656374436C617373")]
    [InlineData("(cn=Bob)", "A3090402636E0403426F62")]
    [InlineData("(&(objectClass=person)(cn=a*b))", "A025 A315 040B6F626A656374436C617373 0406706572736F6E A40C 0402636E 3006 800161 820162")]
    [InlineData("(!(uid=x))", "A20A A308 0403756964 040178")]
    [InlineData("(cn=a\\2ab)", "A309 0402636E 0403612A62")]
    [InlineData("(userAccountControl:1.2.840.113556.1.4.803:=2)", "A92F 8116312E322E3834302E3131333535362E312E342E383033 821275736572416363 6F756E74436F6E74726F6C 830132")]
    public void Ldap_filters_encode_to_ber(string filter, string hex) =>
        Assert.Equal(hex.Replace(" ", string.Empty), Convert.ToHexString(LdapSensorExecutor.LdapFilter.Encode(filter)));

    [Theory]
    [InlineData("(cn=Bob")]
    [InlineData("(=Bob)")]
    [InlineData("(&)")]
    [InlineData("(cn=a\\2)")]
    public void Ldap_bad_filters_are_refused(string filter) =>
        Assert.Throws<FormatException>(() => LdapSensorExecutor.LdapFilter.Encode(filter));

    [Fact]
    public void Ldap_ad_reasons_time_and_sync_are_read()
    {
        Assert.Equal("invalid credentials - account locked out",
            LdapSensorExecutor.Describe(new LdapSensorExecutor.LdapResult(49, "80090308: LdapErr: DSID-0C09044E, comment: AcceptSecurityContext error, data 775, v4563")));
        Assert.Equal("the server wants an encrypted connection - use LDAPS or StartTLS (00002028: LdapErr: DSID-0C090259)",
            LdapSensorExecutor.Describe(new LdapSensorExecutor.LdapResult(8, "00002028: LdapErr: DSID-0C090259")));
        Assert.Equal(new DateTimeOffset(2026, 10, 23, 19, 45, 0, TimeSpan.Zero), LdapSensorExecutor.ParseGeneralizedTime("20261023194500.0Z"));

        var now = new DateTimeOffset(2026, 10, 23, 19, 49, 0, TimeSpan.Zero);
        var rootDse = new Dictionary<string, IReadOnlyList<string>>
        {
            ["dnsHostName"] = ["dc01.corp.example"],
            ["currentTime"] = ["20261023194500.0Z"],
            ["isSynchronized"] = ["FALSE"],
            ["domainControllerFunctionality"] = ["7"]
        };
        var reading = new LdapSensorExecutor.LdapReading("10.0.0.2:636", 3, 12, 40, 42, false, rootDse, "monitor@corp.example", "OU=Users,DC=corp,DC=example", "ldaps");
        var settings = new MonitoringSettings();
        SensorThresholdDefaults.Apply("ldap", settings);
        var result = SensorThresholdEvaluator.ApplyChannelThresholds(settings, LdapSensorExecutor.BuildResult(reading, now, TimeSpan.Zero));

        Assert.Equal(SensorState.Warning, result.State); // 240 s skew is over the 120 s default, and NOT synchronized
        Assert.Equal(240, Channel(result, "clockSkewSeconds"));
        Assert.Equal(0, Channel(result, "synchronized"));
        Assert.Equal("dc01.corp.example (LDAPS): bind as monitor@corp.example OK, 42 entries under OU=Users,DC=corp,DC=example (Active Directory, clock 240 s off, NOT synchronized)", result.Message);
    }

    /// <summary>Against a real OpenLDAP (Debian's slapd: dc=matmon,dc=test, three people, a self-signed certificate) -
    /// skipped unless MATMON_LDAP_TEST="host:ldapPort:ldapsPort" and MATMON_LDAP_PASSWORD hold the test server's values.</summary>
    [Fact]
    public async Task Ldap_a_real_openldap_answers_plain_starttls_and_ldaps()
    {
        var endpoint = Environment.GetEnvironmentVariable("MATMON_LDAP_TEST");
        var password = Environment.GetEnvironmentVariable("MATMON_LDAP_PASSWORD");
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrEmpty(password))
        {
            return;
        }

        var parts = endpoint.Split(':');
        MonitoringSettings Settings(string security, string port, string? user = "cn=admin,dc=matmon,dc=test", string? secret = null, string? filter = null)
        {
            var settings = new MonitoringSettings();
            settings.Parameters["ldap.security"] = security;
            settings.Parameters["ldap.port"] = port;
            settings.Parameters["ldap.baseDn"] = "ou=people,dc=matmon,dc=test";
            settings.Parameters["ldap.filter"] = filter ?? "(&(objectClass=inetOrgPerson)(sn=Ex*))";
            if (user is not null)
            {
                settings.Parameters["generic.username"] = user;
                settings.Parameters["generic.password"] = secret ?? password;
            }

            return settings;
        }

        var executor = new LdapSensorExecutor();
        var plain = await executor.ExecuteAsync(new SensorExecutionContext("ldap", parts[0], Settings("none", parts[1])));
        var startTls = await executor.ExecuteAsync(new SensorExecutionContext("ldap", parts[0], Settings("starttls", parts[1], filter: "(objectClass=inetOrgPerson)")));
        var ldaps = await executor.ExecuteAsync(new SensorExecutionContext("ldap", parts[0], Settings("ldaps", parts[2])));
        var wrong = await executor.ExecuteAsync(new SensorExecutionContext("ldap", parts[0], Settings("none", parts[1], secret: "not-the-password")));
        var anonymous = await executor.ExecuteAsync(new SensorExecutionContext("ldap", parts[0], Settings("none", parts[1], user: null)));

        Assert.True(plain.State == SensorState.Healthy, plain.Message);
        Assert.Equal(2, Channel(plain, "entries")); // alice + bob have sn=Example
        Assert.Contains("bind as cn=admin,dc=matmon,dc=test OK, 2 entries under ou=people,dc=matmon,dc=test", plain.Message);
        Assert.True(startTls.State == SensorState.Healthy, startTls.Message);
        Assert.Equal(3, Channel(startTls, "entries"));
        Assert.Contains("(StartTLS)", startTls.Message);
        Assert.True(ldaps.State == SensorState.Healthy, ldaps.Message);
        Assert.Contains("(LDAPS)", ldaps.Message);
        Assert.Equal(SensorState.Critical, wrong.State);
        Assert.Contains("invalid credentials", wrong.Message);
        Assert.True(anonymous.State == SensorState.Healthy, anonymous.Message); // slapd lets anonymous read by default
        Assert.Contains("answers (anonymous)", anonymous.Message);
    }

    // ---- OPNsense --------------------------------------------------------------------------------------------------------

    // Shapes from OPNsense's sources: scripts/routes/gateway_status.php (dpinger formats "12.3 ms" / "0.0 %", "~" when not
    // monitored), Core/FirmwareController::statusAction, Diagnostics/SystemController (systemResources, systemDisk).
    private static Dictionary<string, string> OpnsenseApi(string wanStatus = "none", string wanTranslated = "Online", string firmware = "") => new()
    {
        ["/api/routes/gateway/status"] = $$"""
            { "items": [
                { "name": "WAN_DHCP", "status": "{{wanStatus}}", "status_translated": "{{wanTranslated}}", "loss": "0.0 %", "delay": "12.3 ms", "stddev": "0.8 ms", "monitor": "1.1.1.1" },
                { "name": "WAN_DHCP6", "status": "none", "status_translated": "Online", "loss": "~", "delay": "~", "stddev": "~", "monitor": "~" } ],
              "status": "ok" }
            """,
        ["/api/core/firmware/status"] = firmware.Length > 0 ? firmware : """
            { "status": "update", "status_msg": "There are 2 updates available, total download size is 23.4MB.",
              "product": { "product_version": "24.7.8", "product_name": "OPNsense" },
              "all_packages": { "opnsense": { "name": "opnsense", "reason": "upgrade" }, "unbound": { "name": "unbound", "reason": "upgrade" } } }
            """,
        ["/api/diagnostics/system/systemResources"] = """{ "memory": { "total": "8506318848", "total_frmt": "8112", "used": 2126579712, "used_frmt": "2028" } }""",
        ["/api/diagnostics/system/systemDisk"] = """{ "devices": [ { "device": "zroot/ROOT/default", "type": "zfs", "used_pct": 7, "mountpoint": "/" }, { "device": "zroot/tmp", "type": "zfs", "used_pct": 0, "mountpoint": "/tmp" } ] }"""
    };

    private static MonitoringSettings OpnsenseSettings()
    {
        var settings = new MonitoringSettings();
        settings.Parameters["generic.username"] = "key123";
        settings.Parameters["generic.password"] = "secret456";
        SensorThresholdDefaults.Apply("opnsense", settings);
        return settings;
    }

    [Fact]
    public async Task Opnsense_reads_gateways_firmware_memory_and_disk()
    {
        string? authorization = null;
        var result = await new OpnsenseSensorExecutor(_ => new FakeHttp(request =>
        {
            authorization = request.Headers.Authorization?.ToString();
            return OpnsenseApi().TryGetValue(request.RequestUri!.AbsolutePath, out var json)
                ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json) }
                : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        })).ExecuteAsync(new SensorExecutionContext("opnsense", "fw.example", OpnsenseSettings()));

        Assert.Equal("Basic " + Convert.ToBase64String("key123:secret456"u8.ToArray()), authorization);
        Assert.Equal(SensorState.Warning, result.State); // 2 pending updates, through the default threshold
        Assert.Equal("gw.wan_dhcp.delay".Replace("_", "-"), result.DefaultChannelKey);
        Assert.Equal(12.3, Channel(result, "gw.wan-dhcp.delay"));
        Assert.Equal(0, Channel(result, "gw.wan-dhcp.loss"));
        Assert.Null(Channel(result, "gw.wan-dhcp6.delay")); // "~" = not monitored
        Assert.Equal(1, Channel(result, "gw.wan-dhcp6.up"));
        Assert.Equal(0, Channel(result, "gatewaysDown"));
        Assert.Equal(2, Channel(result, "updatesAvailable"));
        Assert.Equal(25, Channel(result, "memoryUsedPercent"));
        Assert.Equal(7, Channel(result, "diskUsedPercent"));
        Assert.Equal("OPNsense 24.7.8: WAN_DHCP online 12.3 ms, WAN_DHCP6 online; 2 firmware updates pending", result.Message);
    }

    [Fact]
    public async Task Opnsense_a_gateway_down_is_critical_and_latency_a_warning()
    {
        OpnsenseSensorExecutor Executor(Dictionary<string, string> api) => new(_ => new FakeHttp(request =>
            api.TryGetValue(request.RequestUri!.AbsolutePath, out var json)
                ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json) }
                : new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden)));
        var noUpdates = """{ "status": "none", "status_msg": "There are no updates available on the selected mirror.", "product": { "product_version": "24.7.8" } }""";

        var down = await Executor(OpnsenseApi("down", "Offline", noUpdates)).ExecuteAsync(new SensorExecutionContext("opnsense", "fw.example", OpnsenseSettings()));
        var slow = await Executor(OpnsenseApi("delay", "Latency", noUpdates)).ExecuteAsync(new SensorExecutionContext("opnsense", "fw.example", OpnsenseSettings()));
        var onlyGateways = await Executor(new() { ["/api/routes/gateway/status"] = OpnsenseApi()["/api/routes/gateway/status"] })
            .ExecuteAsync(new SensorExecutionContext("opnsense", "fw.example", OpnsenseSettings()));
        var refused = await new OpnsenseSensorExecutor(_ => new FakeHttp(_ => new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)))
            .ExecuteAsync(new SensorExecutionContext("opnsense", "fw.example", OpnsenseSettings()));

        Assert.Equal(SensorState.Critical, down.State);
        Assert.Equal(1, Channel(down, "gatewaysDown"));
        Assert.Contains("WAN_DHCP offline", down.Message);
        Assert.Equal(SensorState.Warning, slow.State);
        Assert.Equal(SensorState.Healthy, onlyGateways.State); // a user without the other privileges still gets the gateways
        Assert.Null(Channel(onlyGateways, "updatesAvailable"));
        Assert.Equal("The firewall refused the API key and secret.", refused.Message);
    }

    // ---- Hyper-V ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void HyperV_names_channels_and_says_which_vms_are_in_trouble()
    {
        // What the PowerShell engine makes of the script's JSON: lower-cased keys, labels from the original path.
        static SensorChannelValue C(string key, string label, double value) => new() { Key = key, Label = label, Value = value };
        var engine = SensorExecutionResult.Warning(TimeSpan.FromSeconds(2), "engine message", 3, "vmrunning",
        [
            C("vmtotal", "Vm Total", 5), C("vmrunning", "Vm Running", 3), C("vmoff", "Vm Off", 1), C("vmsavedorpaused", "Vm Saved Or Paused", 1),
            C("vmcritical", "Vm Critical", 0), C("heartbeatlost", "Heartbeat Lost", 1), C("replicationwarning", "Replication Warning", 0), C("replicationcritical", "Replication Critical", 0),
            C("hostmemoryusedpercent", "Host Memory Used Percent", 71.5),
            C("vm.sql-01.up", "Vm SQL 01 up", 1), C("vm.sql-01.problem", "Vm SQL 01 problem", 1), C("vm.sql-01.cpu", "Vm SQL 01 cpu", 12),
            C("vm.sql-01.memorygb", "Vm SQL 01 memory Gb", 16), C("vm.sql-01.uptimehours", "Vm SQL 01 uptime Hours", 300.5),
            C("vm.webserver.up", "Vm Web Server up", 1), C("vm.webserver.problem", "Vm Web Server problem", 0), C("vm.webserver.cpu", "Vm Web Server cpu", 3)
        ]);

        var result = HyperVSensorExecutor.Describe(engine);

        Assert.Equal(SensorState.Warning, result.State);
        Assert.Equal("5 VMs: 3 running, 1 off, 1 saved or paused - 1 without heartbeat (SQL 01); host memory 72 % used", result.Message);
        Assert.Equal("SQL 01 CPU", result.Channels.Single(channel => channel.Key == "vm.sql-01.cpu").Label);
        Assert.Equal("%", result.Channels.Single(channel => channel.Key == "vm.sql-01.cpu").Unit);
        Assert.Equal("Web Server running", result.Channels.Single(channel => channel.Key == "vm.webserver.up").Label);
        Assert.Equal("Host memory used", result.Channels.Single(channel => channel.Key == "hostmemoryusedpercent").Label);
    }

    [Fact]
    public void HyperV_an_engine_error_keeps_its_own_message()
    {
        var failed = SensorExecutionResult.Critical(TimeSpan.Zero, "You do not have the required permission to complete this task.", -1, "vmtotal",
            [new SensorChannelValue { Key = "vmtotal", Label = "Vm Total", Value = -1 }]);

        var described = HyperVSensorExecutor.Describe(failed);
        Assert.Equal("You do not have the required permission to complete this task - the account needs to be a member of 'Hyper-V Administrators' on the host, and the Hyper-V PowerShell module installed.", described.Message);
        Assert.Empty(described.Channels); // the -1 placeholder is no reading
    }

    /// <summary>On a Windows machine with Hyper-V, run as a member of "Hyper-V Administrators" - skipped unless
    /// MATMON_HYPERV_TEST=1. Runs the real script locally (target localhost).</summary>
    [Fact]
    public async Task HyperV_the_script_runs_on_this_machine()
    {
        if (Environment.GetEnvironmentVariable("MATMON_HYPERV_TEST") != "1" || !OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await new HyperVSensorExecutor().ExecuteAsync(new SensorExecutionContext("hyperv-health", "localhost", new MonitoringSettings()));

        Assert.True(result.State != SensorState.Critical, result.Message);
        Assert.Matches(@"^\d+ VMs?: \d+ running", result.Message);
    }

    private static double? Channel(SensorExecutionResult result, string key) => result.Channels.SingleOrDefault(channel => channel.Key == key)?.Value;

    private sealed class FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(route(request));
    }
}
