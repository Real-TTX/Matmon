using System.Globalization;
using System.Net;
using System.Text;
using Matmon.Core.Domain;

namespace Matmon.Tests;

/// <summary>
/// The three device sensors that read an appliance's own API: a server's management controller (Redfish), a Proxmox
/// Backup Server and a FRITZ!Box. Each runs against a stand-in that answers like the real thing (the shapes are taken from
/// the vendors' documentation and, for Redfish, from the DMTF's own mockup); Redfish also against the DMTF mockup server
/// when MATMON_REDFISH_TEST points at one.
/// </summary>
public sealed class HardwareSensorTests
{
    // ---- Redfish ---------------------------------------------------------------------------------------------------------

    private static Dictionary<string, string> RedfishMockup(string psuHealth = "OK", string tempHealth = "OK", bool legacy = true) => new()
    {
        ["/redfish/v1/"] = """{ "Systems": { "@odata.id": "/redfish/v1/Systems" }, "Chassis": { "@odata.id": "/redfish/v1/Chassis" } }""",
        ["/redfish/v1/Systems"] = """{ "Members": [ { "@odata.id": "/redfish/v1/Systems/1" } ] }""",
        ["/redfish/v1/Systems/1"] = """{ "Id": "1", "Name": "System", "Manufacturer": "Dell Inc.", "Model": "PowerEdge R640", "PowerState": "On", "Status": { "State": "Enabled", "Health": "OK", "HealthRollup": "OK" } }""",
        ["/redfish/v1/Chassis"] = """{ "Members": [ { "@odata.id": "/redfish/v1/Chassis/1" } ] }""",
        ["/redfish/v1/Chassis/1"] = legacy
            ? """{ "Id": "1", "Status": { "Health": "OK" }, "Thermal": { "@odata.id": "/redfish/v1/Chassis/1/Thermal" }, "Power": { "@odata.id": "/redfish/v1/Chassis/1/Power" } }"""
            : """{ "Id": "1", "Status": { "Health": "OK" }, "ThermalSubsystem": { "@odata.id": "/redfish/v1/Chassis/1/ThermalSubsystem" }, "PowerSubsystem": { "@odata.id": "/redfish/v1/Chassis/1/PowerSubsystem" }, "EnvironmentMetrics": { "@odata.id": "/redfish/v1/Chassis/1/EnvironmentMetrics" } }""",
        ["/redfish/v1/Chassis/1/Thermal"] = $$"""
            { "Temperatures": [
                { "Name": "CPU1 Temp", "ReadingCelsius": 61, "Status": { "State": "Enabled", "Health": "{{tempHealth}}" } },
                { "Name": "Inlet Temp", "ReadingCelsius": 23, "Status": { "State": "Enabled", "Health": "OK" } },
                { "Name": "CPU2 Temp", "ReadingCelsius": null, "Status": { "State": "Absent" } } ],
              "Fans": [
                { "Name": "Fan 1", "Reading": 7200, "ReadingUnits": "RPM", "Status": { "State": "Enabled", "Health": "OK" } },
                { "Name": "Fan 2", "Reading": 7080, "ReadingUnits": "RPM", "Status": { "State": "Enabled", "Health": "OK" } } ] }
            """,
        ["/redfish/v1/Chassis/1/Power"] = $$"""
            { "PowerControl": [ { "PowerConsumedWatts": 212 } ],
              "PowerSupplies": [
                { "Name": "PSU 1", "LastPowerOutputWatts": 110, "Status": { "State": "Enabled", "Health": "OK" } },
                { "Name": "PSU 2", "LastPowerOutputWatts": 102, "Status": { "State": "Enabled", "Health": "{{psuHealth}}" } } ] }
            """,
        ["/redfish/v1/Chassis/1/ThermalSubsystem"] = """{ "ThermalMetrics": { "@odata.id": "/redfish/v1/Chassis/1/ThermalSubsystem/ThermalMetrics" }, "Fans": { "@odata.id": "/redfish/v1/Chassis/1/ThermalSubsystem/Fans" } }""",
        ["/redfish/v1/Chassis/1/ThermalSubsystem/ThermalMetrics"] = """{ "TemperatureReadingsCelsius": [ { "DeviceName": "Intake", "Reading": 24.8 }, { "DataSourceUri": "/redfish/v1/Chassis/1/Sensors/ExhaustTemp", "Reading": 40.5 } ] }""",
        ["/redfish/v1/Chassis/1/ThermalSubsystem/Fans"] = """{ "Members": [ { "@odata.id": "/redfish/v1/Chassis/1/ThermalSubsystem/Fans/Bay1" } ] }""",
        ["/redfish/v1/Chassis/1/ThermalSubsystem/Fans/Bay1"] = """{ "Id": "Bay1", "Name": "FanBay1", "SpeedPercent": { "Reading": 45, "SpeedRPM": 2200 }, "Status": { "State": "Enabled", "Health": "OK" } }""",
        ["/redfish/v1/Chassis/1/PowerSubsystem"] = """{ "PowerSupplies": { "@odata.id": "/redfish/v1/Chassis/1/PowerSubsystem/PowerSupplies" } }""",
        ["/redfish/v1/Chassis/1/PowerSubsystem/PowerSupplies"] = """{ "Members": [ { "@odata.id": "/redfish/v1/Chassis/1/PowerSubsystem/PowerSupplies/Bay1" } ] }""",
        ["/redfish/v1/Chassis/1/PowerSubsystem/PowerSupplies/Bay1"] = $$"""{ "Id": "Bay1", "Name": "PSU Bay1", "Status": { "State": "Enabled", "Health": "{{psuHealth}}" } }""",
        ["/redfish/v1/Chassis/1/EnvironmentMetrics"] = """{ "PowerWatts": { "Reading": 374 } }"""
    };

    private static MonitoringSettings RedfishSettings()
    {
        var settings = new MonitoringSettings();
        settings.Parameters["generic.username"] = "monitor";
        settings.Parameters["generic.password"] = "pw";
        SensorThresholdDefaults.Apply("redfish", settings);
        return settings;
    }

    [Fact]
    public async Task Redfish_reads_health_power_temperatures_fans_and_power_supplies()
    {
        var handler = new RoutingHandler(request => Json(RedfishMockup(), request));
        var result = await new RedfishSensorExecutor(_ => handler).ExecuteAsync(new SensorExecutionContext("redfish", "bmc.example", RedfishSettings()));

        Assert.Equal(SensorState.Healthy, result.State);
        Assert.Equal(RedfishSensorExecutor.TemperatureMaxChannel, result.DefaultChannelKey);
        Assert.Equal(61, Channel(result, "temperatureMax"));
        Assert.Equal(212, Channel(result, "powerWatts"));
        Assert.Equal(1, Channel(result, "powerOn"));
        Assert.Equal(0, Channel(result, "health"));
        Assert.Equal(0, Channel(result, "fansFailed"));
        Assert.Equal(0, Channel(result, "psuFailed"));
        Assert.Equal(7200, Channel(result, "fan.fan-1"));
        Assert.Equal(23, Channel(result, "temp.inlet-temp"));
        Assert.DoesNotContain(result.Channels, channel => channel.Key == "temp.cpu2-temp"); // absent
        Assert.StartsWith("Dell Inc. PowerEdge R640: OK (61 °C max, 2 fans, 2 power supplies, 212 W)", result.Message);
        Assert.All(handler.Requests, request => Assert.Equal("Basic", request.Headers.Authorization?.Scheme));
        Assert.Equal("https://bmc.example/redfish/v1/", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task Redfish_takes_the_controllers_verdict_and_names_the_part()
    {
        var warning = await new RedfishSensorExecutor(_ => new RoutingHandler(request => Json(RedfishMockup(psuHealth: "Warning"), request)))
            .ExecuteAsync(new SensorExecutionContext("redfish", "bmc.example", RedfishSettings()));
        var critical = await new RedfishSensorExecutor(_ => new RoutingHandler(request => Json(RedfishMockup(tempHealth: "Critical"), request)))
            .ExecuteAsync(new SensorExecutionContext("redfish", "bmc.example", RedfishSettings()));

        Assert.Equal(SensorState.Warning, warning.State);
        Assert.Equal(1, Channel(warning, "psuFailed"));
        Assert.Contains("PSU 2 warning", warning.Message);
        Assert.Equal(SensorState.Critical, critical.State);
        Assert.Equal(SensorState.Critical, critical.Channels.Single(channel => channel.Key == "temp.cpu1-temp").State);
        Assert.Contains("CPU1 Temp critical", critical.Message);
    }

    [Fact]
    public async Task Redfish_reads_the_newer_thermal_and_power_subsystems_when_the_old_resources_are_gone()
    {
        var result = await new RedfishSensorExecutor(_ => new RoutingHandler(request => Json(RedfishMockup(legacy: false), request)))
            .ExecuteAsync(new SensorExecutionContext("redfish", "bmc.example", RedfishSettings()));

        Assert.Equal(SensorState.Healthy, result.State);
        Assert.Equal(40.5, Channel(result, "temperatureMax"));
        Assert.Equal(24.8, Channel(result, "temp.intake"));
        Assert.Equal(40.5, Channel(result, "temp.exhausttemp"));
        Assert.Equal(45, Channel(result, "fan.fanbay1"));
        Assert.Equal(374, Channel(result, "powerWatts"));
        Assert.Equal(0, Channel(result, "psuFailed"));
    }

    [Fact]
    public async Task Redfish_falls_back_to_a_session_and_always_gives_it_back()
    {
        var mockup = RedfishMockup();
        var handler = new RoutingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == "/redfish/v1/SessionService/Sessions")
            {
                var response = new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("{}") };
                response.Headers.Add("X-Auth-Token", "token-123");
                response.Headers.Location = new Uri("/redfish/v1/SessionService/Sessions/77", UriKind.Relative);
                return response;
            }

            if (request.Method == HttpMethod.Delete)
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            // The service root is public, everything else wants the session token.
            return path == "/redfish/v1/" || request.Headers.TryGetValues("X-Auth-Token", out _)
                ? Json(mockup, request)
                : new HttpResponseMessage(HttpStatusCode.Unauthorized);
        });

        var result = await new RedfishSensorExecutor(_ => handler).ExecuteAsync(new SensorExecutionContext("redfish", "bmc.example", RedfishSettings()));

        Assert.Equal(SensorState.Healthy, result.State);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post);
        var logout = Assert.Single(handler.Requests, request => request.Method == HttpMethod.Delete);
        Assert.Equal("/redfish/v1/SessionService/Sessions/77", logout.RequestUri!.AbsolutePath);
        Assert.Equal("token-123", logout.Headers.GetValues("X-Auth-Token").Single());
    }

    [Fact]
    public async Task Redfish_says_what_is_wrong_in_words()
    {
        var refused = await new RedfishSensorExecutor(_ => new RoutingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)))
            .ExecuteAsync(new SensorExecutionContext("redfish", "bmc.example", RedfishSettings()));
        var notRedfish = await new RedfishSensorExecutor(_ => new RoutingHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)))
            .ExecuteAsync(new SensorExecutionContext("redfish", "bmc.example", RedfishSettings()));
        var noTarget = await new RedfishSensorExecutor().ExecuteAsync(new SensorExecutionContext("redfish", "", RedfishSettings()));

        Assert.Equal(SensorState.Critical, refused.State);
        Assert.Equal("The controller refused the user name or password.", refused.Message);
        Assert.Contains("No Redfish service", notRedfish.Message);
        Assert.Contains("address is missing", noTarget.Message);
    }

    [Fact]
    public void Redfish_maps_health_and_powered_off_raises_the_default_warning()
    {
        Assert.Equal(0, RedfishSensorExecutor.HealthLevel("OK"));
        Assert.Equal(1, RedfishSensorExecutor.HealthLevel("warning"));
        Assert.Equal(2, RedfishSensorExecutor.HealthLevel("Critical"));
        Assert.Null(RedfishSensorExecutor.HealthLevel(null));

        var reading = new RedfishSensorExecutor.RedfishReading(
            [new RedfishSensorExecutor.RedfishSystem("System", "HPE ProLiant DL380 Gen10", "OK", PoweredOn: false)], [], [], [], null, []);
        var settings = new MonitoringSettings();
        SensorThresholdDefaults.Apply("redfish", settings);
        var result = SensorThresholdEvaluator.ApplyChannelThresholds(settings, RedfishSensorExecutor.BuildResult(reading, TimeSpan.Zero));

        Assert.Equal(SensorState.Warning, result.State);
        Assert.Contains("powered OFF", result.Message);
    }

    /// <summary>Against the DMTF's own Redfish mockup server (docker run -p 8000:8000 dmtf/redfish-mockup-server) - skipped
    /// unless MATMON_REDFISH_TEST holds its base URL. Its public-rackmount1 mockup has one power supply in Warning.</summary>
    [Fact]
    public async Task The_DMTF_mockup_server_is_read()
    {
        var baseUrl = Environment.GetEnvironmentVariable("MATMON_REDFISH_TEST");
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return;
        }

        var result = await new RedfishSensorExecutor().ExecuteAsync(new SensorExecutionContext("redfish", baseUrl, RedfishSettings()));

        Assert.Equal(SensorState.Warning, result.State);
        Assert.Equal(344, Channel(result, "powerWatts"));
        Assert.Equal(41, Channel(result, "temperatureMax"));
        Assert.Equal(1, Channel(result, "psuFailed"));
        Assert.Equal(1, Channel(result, "powerOn"));
        Assert.StartsWith("Contoso 3500: warning", result.Message);
    }

    // ---- Proxmox Backup Server -------------------------------------------------------------------------------------------

    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static Dictionary<string, string> PbsApi(string tasks = "[]", string usage = "") => new()
    {
        ["/api2/json/nodes/localhost/status"] = """{ "data": { "cpu": 0.125, "memory": { "total": 16000000000, "used": 4000000000 }, "root": { "total": 100000000000, "used": 30000000000 }, "uptime": 86400 } }""",
        ["/api2/json/status/datastore-usage"] = usage.Length > 0 ? usage : $$"""
            { "data": [
                { "store": "backup", "total": 4000000000000, "used": 2600000000000, "avail": 1400000000000, "estimated-full-date": {{Now.AddDays(40).ToUnixTimeSeconds()}} },
                { "store": "offsite", "total": 2000000000000, "used": 400000000000, "avail": 1600000000000, "estimated-full-date": 0 } ] }
            """,
        ["/api2/json/admin/datastore/backup/groups"] = $$"""
            { "data": [
                { "backup-type": "vm", "backup-id": "101", "last-backup": {{Now.AddHours(-3).ToUnixTimeSeconds()}}, "backup-count": 14 },
                { "backup-type": "ct", "backup-id": "200", "last-backup": {{Now.AddHours(-27).ToUnixTimeSeconds()}}, "backup-count": 9 } ] }
            """,
        ["/api2/json/admin/datastore/offsite/groups"] = $$"""{ "data": [ { "backup-type": "vm", "backup-id": "101", "last-backup": {{Now.AddDays(-30).ToUnixTimeSeconds()}} } ] }""",
        ["/api2/json/nodes/localhost/tasks"] = $$"""{ "data": {{tasks}} }"""
    };

    private static MonitoringSettings PbsSettings()
    {
        var settings = new MonitoringSettings();
        settings.Parameters["pve.tokenId"] = "monitor@pbs!matmon";
        settings.Parameters["pve.tokenSecret"] = "c0ffee";
        SensorThresholdDefaults.Apply("proxmox-backup", settings);
        return settings;
    }

    [Fact]
    public async Task Pbs_reads_datastores_backup_ages_tasks_and_the_node()
    {
        var handler = new RoutingHandler(request => Json(PbsApi(), request));
        var result = await new ProxmoxBackupSensorExecutor(_ => handler).ExecuteAsync(new SensorExecutionContext("proxmox-backup", "pbs.example", PbsSettings()));

        Assert.Equal(SensorState.Healthy, result.State);
        Assert.Equal("datastoreUsedPercent", result.DefaultChannelKey);
        Assert.Equal(65, Channel(result, "datastoreUsedPercent"));
        Assert.Equal(65, Channel(result, "store.backup.usedPercent"));
        Assert.Equal(20, Channel(result, "store.offsite.usedPercent"));
        Assert.Equal(1400, Channel(result, "store.backup.freeGb"));
        Assert.InRange(Channel(result, "daysUntilFull")!.Value, 39.9, 40.1);
        Assert.InRange(Channel(result, "newestBackupAgeHours")!.Value, 2.9, 3.1);
        Assert.InRange(Channel(result, "oldestGroupAgeHours")!.Value, 719, 721);
        Assert.Equal(3, Channel(result, "backupGroups"));
        Assert.Equal(0, Channel(result, "failedTasks24h"));
        Assert.Equal(12.5, Channel(result, "cpu"));
        Assert.Equal(25, Channel(result, "memoryUsedPercent"));
        Assert.Equal(30, Channel(result, "diskUsedPercent"));
        Assert.Equal("2 datastores, backup fullest at 65%; newest backup 3 h ago", result.Message);
        Assert.Equal("PBSAPIToken=monitor@pbs!matmon:c0ffee", handler.Requests[0].Headers.GetValues("Authorization").Single());
        Assert.Equal("https://pbs.example:8007/api2/json/nodes/localhost/status", handler.Requests[0].RequestUri!.ToString());
        Assert.Contains(handler.Requests, request => request.RequestUri!.Query.Contains("errors=true", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pbs_a_failed_task_is_a_warning_and_named()
    {
        var tasks = """
            [ { "worker_type": "backup", "worker_id": "backup:vm/101", "status": "connection error: timed out" },
              { "worker_type": "verify", "worker_id": "backup", "status": "WARNINGS: 2" },
              { "worker_type": "garbage_collection", "worker_id": "backup", "status": "OK" } ]
            """;
        var result = await new ProxmoxBackupSensorExecutor(_ => new RoutingHandler(request => Json(PbsApi(tasks), request)))
            .ExecuteAsync(new SensorExecutionContext("proxmox-backup", "pbs.example", PbsSettings()));

        Assert.Equal(SensorState.Warning, result.State);
        Assert.Equal(1, Channel(result, "failedTasks24h"));
        Assert.Equal(1, Channel(result, "warningTasks24h"));
        Assert.Contains("1 failed task in 24 h (backup backup:vm/101)", result.Message);
    }

    [Fact]
    public async Task Pbs_an_unavailable_datastore_is_critical_and_no_datastore_says_why()
    {
        var broken = """{ "data": [ { "store": "usb", "error": "datastore is not mounted" } ] }""";
        var unavailable = await new ProxmoxBackupSensorExecutor(_ => new RoutingHandler(request => Json(PbsApi(usage: broken), request)))
            .ExecuteAsync(new SensorExecutionContext("proxmox-backup", "pbs.example", PbsSettings()));
        var none = await new ProxmoxBackupSensorExecutor(_ => new RoutingHandler(request => Json(PbsApi(usage: """{ "data": [] }"""), request)))
            .ExecuteAsync(new SensorExecutionContext("proxmox-backup", "pbs.example", PbsSettings()));
        var refused = await new ProxmoxBackupSensorExecutor(_ => new RoutingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)))
            .ExecuteAsync(new SensorExecutionContext("proxmox-backup", "pbs.example", PbsSettings()));

        Assert.Equal(SensorState.Critical, unavailable.State);
        Assert.Contains("datastore usb unavailable: datastore is not mounted", unavailable.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SensorState.Critical, none.State);
        Assert.Contains("Audit role", none.Message);
        Assert.Contains("refused the API token", refused.Message);
    }

    [Fact]
    public void Pbs_token_header_uses_a_colon_and_completes_a_bare_token_name()
    {
        Assert.Equal("PBSAPIToken=root@pam!mon:s3", ProxmoxBackupSensorExecutor.BuildAuthorizationHeader("", "root@pam!mon", "s3"));
        Assert.Equal("PBSAPIToken=root@pam!mon:s3", ProxmoxBackupSensorExecutor.BuildAuthorizationHeader("root@pam", "mon", "s3"));
        Assert.Null(ProxmoxBackupSensorExecutor.BuildAuthorizationHeader("", "mon", "s3"));
        Assert.Null(ProxmoxBackupSensorExecutor.BuildAuthorizationHeader("", "root@pam!mon", ""));
    }

    [Fact]
    public async Task Pbs_a_day_without_any_backup_raises_the_default()
    {
        var api = PbsApi();
        api["/api2/json/admin/datastore/backup/groups"] = $$"""{ "data": [ { "last-backup": {{Now.AddHours(-60).ToUnixTimeSeconds()}} } ] }""";
        api["/api2/json/admin/datastore/offsite/groups"] = """{ "data": [] }""";
        var result = await new ProxmoxBackupSensorExecutor(_ => new RoutingHandler(request => Json(api, request)))
            .ExecuteAsync(new SensorExecutionContext("proxmox-backup", "pbs.example", PbsSettings()));

        Assert.Equal(SensorState.Critical, result.State);
        Assert.Contains("newest backup 60 h ago", result.Message);
    }

    [Fact]
    public async Task Pbs_has_a_channel_per_backup_group_named_after_the_guest_and_in_the_state_of_its_backup_task()
    {
        var api = PbsApi("""[ { "worker_type": "backup", "worker_id": "backup:ct/200", "status": "connection error: timed out" } ]""");
        api["/api2/json/admin/datastore/backup/groups"] = $$"""
            { "data": [
                { "backup-type": "vm", "backup-id": "101", "last-backup": {{Now.AddHours(-3).ToUnixTimeSeconds()}}, "backup-count": 14, "comment": "web-01" },
                { "backup-type": "ct", "backup-id": "200", "last-backup": {{Now.AddHours(-27).ToUnixTimeSeconds()}}, "backup-count": 9 } ] }
            """;
        var result = await new ProxmoxBackupSensorExecutor(_ => new RoutingHandler(request => Json(api, request)))
            .ExecuteAsync(new SensorExecutionContext("proxmox-backup", "pbs.example", PbsSettings()));

        var web = result.Channels.Single(channel => channel.Key == "group.backup.vm-101.ageHours");
        Assert.Equal("web-01 (VM 101) on backup - last backup", web.Label);
        Assert.InRange(web.Value!.Value, 2.9, 3.1);
        Assert.Equal(SensorState.Healthy, web.State);
        Assert.StartsWith("last backup 3 h ago, 14 snapshots", web.Message);

        var container = result.Channels.Single(channel => channel.Key == "group.backup.ct-200.ageHours");
        Assert.Equal(SensorState.Warning, container.State);
        Assert.Contains("connection error", container.Message);
        Assert.NotNull(result.Channels.SingleOrDefault(channel => channel.Key == "group.offsite.vm-101.ageHours"));
    }

    // ---- Synology Hyper Backup ---------------------------------------------------------------------------------------------

    private static RoutingHandler Dsm(string tasks, bool loginOk = true) => new(request =>
    {
        var form = request.Content?.ReadAsStringAsync().Result ?? string.Empty;
        string json = request.RequestUri!.AbsolutePath switch
        {
            "/webapi/query.cgi" => """{ "success": true, "data": { "SYNO.API.Auth": { "path": "entry.cgi", "maxVersion": 7 }, "SYNO.Backup.Task": { "path": "entry.cgi", "maxVersion": 1 } } }""",
            _ when form.Contains("method=login", StringComparison.Ordinal) => loginOk
                ? """{ "success": true, "data": { "sid": "s1d" } }"""
                : """{ "success": false, "error": { "code": 400 } }""",
            _ when form.Contains("method=logout", StringComparison.Ordinal) => """{ "success": true }""",
            _ when form.Contains("SYNO.Backup.Task", StringComparison.Ordinal) => $$"""{ "success": true, "data": { "task_list": {{tasks}} } }""",
            _ => """{ "success": false, "error": { "code": 102 } }"""
        };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    });

    private static MonitoringSettings DsmSettings()
    {
        var settings = new MonitoringSettings();
        settings.Parameters["generic.username"] = "monitor";
        settings.Parameters["generic.password"] = "p@ss";
        return settings;
    }

    private static string NasTime(TimeSpan ago) => DateTime.Now.Subtract(ago).ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture);

    [Fact]
    public async Task Synology_backup_has_a_channel_per_task_and_the_worst_task_decides()
    {
        var tasks = $$"""
            [ { "task_id": 1, "name": "Daily to C2", "status": "none", "last_bkp_result": "done", "last_bkp_time": "{{NasTime(TimeSpan.FromHours(5))}}", "last_bkp_success_time": "{{NasTime(TimeSpan.FromHours(5))}}", "next_bkp_time": "{{NasTime(TimeSpan.FromHours(-19))}}" },
              { "task_id": 2, "name": "USB copy", "status": "none", "last_bkp_result": "failed", "last_bkp_time": "{{NasTime(TimeSpan.FromHours(2))}}", "last_bkp_success_time": "{{NasTime(TimeSpan.FromDays(3))}}", "next_bkp_time": "{{NasTime(TimeSpan.FromHours(-22))}}" },
              { "task_id": 3, "name": "Offsite", "status": "none", "last_bkp_result": "done", "last_bkp_time": "{{NasTime(TimeSpan.FromDays(2))}}", "next_bkp_time": "{{NasTime(TimeSpan.FromHours(5))}}" } ]
            """;
        var handler = Dsm(tasks);
        var result = await new SynologyBackupSensorExecutor(_ => handler)
            .ExecuteAsync(new SensorExecutionContext("synology-backup", "nas.example", DsmSettings()));

        Assert.Equal(SensorState.Critical, result.State);
        Assert.Equal(1, Channel(result, "failedTasks"));
        Assert.Equal(1, Channel(result, "missedTasks"));
        Assert.Equal(0, Channel(result, "task.daily-to-c2.result"));
        Assert.InRange(Channel(result, "task.daily-to-c2.successAgeHours")!.Value, 4.9, 5.1);
        Assert.Equal(SensorState.Critical, result.Channels.Single(channel => channel.Key == "task.usb-copy.result").State);
        Assert.InRange(Channel(result, "task.usb-copy.successAgeHours")!.Value, 71.9, 72.1);
        Assert.Equal(1, Channel(result, "task.offsite.result"));
        Assert.Contains("USB copy: last run failed", result.Message);
        Assert.Contains("Offsite: missed its scheduled run", result.Message);
        Assert.Equal("https://nas.example:5001/webapi/query.cgi", handler.Requests[0].RequestUri!.ToString());
        // The password never travels in the address.
        Assert.All(handler.Requests, request => Assert.DoesNotContain("p%40ss", request.RequestUri!.Query, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Synology_backup_says_why_it_cannot_sign_in_and_a_running_task_is_fine()
    {
        var refused = await new SynologyBackupSensorExecutor(_ => Dsm("[]", loginOk: false))
            .ExecuteAsync(new SensorExecutionContext("synology-backup", "nas.example", DsmSettings()));
        Assert.Equal(SensorState.Critical, refused.State);
        Assert.Contains("wrong user name or password", refused.Message);

        var running = await new SynologyBackupSensorExecutor(_ => Dsm($$"""[ { "task_id": 1, "name": "Nightly", "status": "backup", "last_bkp_result": "failed", "next_bkp_time": "{{NasTime(TimeSpan.FromHours(3))}}" } ]"""))
            .ExecuteAsync(new SensorExecutionContext("synology-backup", "nas.example", DsmSettings()));
        Assert.Equal(SensorState.Healthy, running.State);
        Assert.Contains("backing up right now", running.Channels.Single(channel => channel.Key == "task.nightly.result").Message);

        var none = await new SynologyBackupSensorExecutor(_ => Dsm("[]"))
            .ExecuteAsync(new SensorExecutionContext("synology-backup", "nas.example", DsmSettings()));
        Assert.Equal(SensorState.Warning, none.State);
        Assert.Contains("administrators", none.Message);
    }

    // ---- Proxmox VE backup jobs ------------------------------------------------------------------------------------------

    private const string PveUpid = "UPID:pve1:0000ABCD:01234567:65000000:vzdump::root@pam:";

    private static HttpResponseMessage PveRoute(HttpRequestMessage request, string jobs, string log, string taskStatus = "\"status\": \"job errors\", ")
    {
        var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
        var started = Now.AddHours(-5).ToUnixTimeSeconds();
        string? json = path switch
        {
            "/api2/json/cluster/backup" => $$"""{ "data": {{jobs}} }""",
            "/api2/json/cluster/resources" => """
                { "data": [
                    { "vmid": 100, "type": "qemu", "name": "web01", "node": "pve1" },
                    { "vmid": 101, "type": "qemu", "name": "db01", "node": "pve1" },
                    { "vmid": 200, "type": "lxc", "name": "dns", "node": "pve2", "pool": "infra" },
                    { "vmid": 300, "type": "qemu", "name": "lab", "node": "pve2" },
                    { "vmid": 9000, "type": "qemu", "name": "tmpl", "node": "pve2", "template": 1 } ] }
                """,
            "/api2/json/nodes" => """{ "data": [ { "node": "pve1", "status": "online" }, { "node": "pve2", "status": "online" } ] }""",
            "/api2/json/nodes/pve1/tasks" => $$"""{ "data": [ { "upid": "{{PveUpid}}", "node": "pve1", "type": "vzdump", "id": "", "starttime": {{started}}, "endtime": {{started + 600}}, {{taskStatus}}"user": "root@pam" } ] }""",
            "/api2/json/nodes/pve2/tasks" => """{ "data": [] }""",
            "/api2/json/nodes/pve1/tasks/" + PveUpid + "/log" => $$"""{ "data": [ {{string.Join(", ", log.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select((line, index) => $$"""{ "n": {{index + 1}}, "t": "{{line.Trim()}}" }"""))}} ] }""",
            "/api2/json/nodes/pve1/storage" => """{ "data": [ { "storage": "pbs", "shared": 1, "active": 1 } ] }""",
            "/api2/json/nodes/pve2/storage" => """{ "data": [ { "storage": "pbs", "shared": 1, "active": 1 } ] }""",
            "/api2/json/nodes/pve1/storage/pbs/content" => $$"""
                { "data": [
                    { "volid": "pbs:backup/vm/100/a", "vmid": 100, "ctime": {{Now.AddHours(-5).ToUnixTimeSeconds()}} },
                    { "volid": "pbs:backup/vm/101/a", "vmid": 101, "ctime": {{Now.AddDays(-3).ToUnixTimeSeconds()}} },
                    { "volid": "pbs:backup/ct/200/a", "vmid": 200, "ctime": {{Now.AddHours(-20).ToUnixTimeSeconds()}} } ] }
                """,
            _ => null
        };
        return json is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private static MonitoringSettings PveSettings()
    {
        var settings = new MonitoringSettings();
        settings.Parameters["pve.tokenId"] = "monitor@pve!matmon";
        settings.Parameters["pve.tokenSecret"] = "c0ffee";
        return settings;
    }

    private const string PveJobs = """
        [ { "id": "backup-nightly", "enabled": 1, "all": 1, "exclude": "300", "node": "pve1", "storage": "pbs", "schedule": "21:00" },
          { "id": "backup-infra", "enabled": 1, "pool": "infra", "storage": "pbs", "schedule": "sun 02:00" },
          { "id": "backup-old", "enabled": 0, "vmid": "300", "storage": "pbs" } ]
        """;

    private const string PveLog = """
        INFO: starting new backup job: vzdump --all 1 --exclude 300 --node pve1 --storage pbs
        INFO: Starting Backup of VM 100 (qemu)
        INFO: Backup started at 2026-11-10 21:00:01
        INFO: Finished Backup of VM 100 (00:01:02)
        INFO: Starting Backup of VM 101 (qemu)
        ERROR: Backup of VM 101 failed - storage 'pbs' is not online
        INFO: Failed at 2026-11-10 21:01:10
        INFO: Backup job finished with errors
        TASK ERROR: job errors
        """;

    [Fact]
    public async Task Pve_backup_jobs_give_each_covered_guest_its_last_result_and_age()
    {
        var handler = new RoutingHandler(request => PveRoute(request, PveJobs, PveLog));
        var result = await new ProxmoxBackupJobsSensorExecutor(_ => handler)
            .ExecuteAsync(new SensorExecutionContext("proxmox-backup-jobs", "pve1.example", PveSettings()));

        Assert.Equal(SensorState.Critical, result.State);
        Assert.Equal("failedGuests", result.DefaultChannelKey);
        Assert.Equal(1, Channel(result, "failedGuests"));
        Assert.Equal(0, Channel(result, "vm.100.backup"));
        Assert.Equal(2, Channel(result, "vm.101.backup"));
        Assert.Equal(SensorState.Critical, result.Channels.Single(channel => channel.Key == "vm.101.backup").State);
        Assert.Contains("storage 'pbs' is not online", result.Channels.Single(channel => channel.Key == "vm.101.backup").Message);
        Assert.Equal("db01 (VM 101) backup", result.Channels.Single(channel => channel.Key == "vm.101.backup").Label);
        // The pool job covers the container on pve2; it has no run in the window - a warning, its last file 20 h old.
        Assert.Equal(1, Channel(result, "ct.200.backup"));
        Assert.InRange(Channel(result, "ct.200.backupAgeHours")!.Value, 19.9, 20.1);
        Assert.InRange(Channel(result, "vm.101.backupAgeHours")!.Value, 71.9, 72.1);
        Assert.InRange(Channel(result, "vm.100.backupAgeHours")!.Value, 4.9, 5.1);
        // VM 300 is only in a disabled job and the template counts for nothing.
        Assert.Null(Channel(result, "vm.300.backup"));
        Assert.Equal(1, Channel(result, "guestsWithoutJob"));
        Assert.Equal(2, Channel(result, "backupJobs"));
        Assert.Contains("db01 (VM 101): failed", result.Message);
        Assert.Contains("in no job: lab", result.Message);
        Assert.Equal("PVEAPIToken=monitor@pve!matmon=c0ffee", handler.Requests[0].Headers.GetValues("Authorization").Single());
    }

    [Fact]
    public async Task Pve_backup_jobs_all_fine_is_healthy_and_says_how_old_the_oldest_backup_is()
    {
        var jobs = """[ { "id": "backup-nightly", "enabled": 1, "vmid": "100", "storage": "pbs" } ]""";
        var log = """
            INFO: Starting Backup of VM 100 (qemu)
            INFO: Finished Backup of VM 100 (00:01:02)
            """;
        var result = await new ProxmoxBackupJobsSensorExecutor(_ => new RoutingHandler(request => PveRoute(request, jobs, log, "\"status\": \"OK\", ")))
            .ExecuteAsync(new SensorExecutionContext("proxmox-backup-jobs", "pve1.example", PveSettings()));

        Assert.Equal(SensorState.Healthy, result.State);
        Assert.Equal(0, Channel(result, "failedGuests"));
        Assert.StartsWith("1 guest backed up by 1 job, oldest last backup 5 h ago", result.Message);
        Assert.Equal(3, Channel(result, "guestsWithoutJob"));
    }

    [Fact]
    public async Task Pve_backup_jobs_no_job_and_a_refused_token_say_why()
    {
        var none = await new ProxmoxBackupJobsSensorExecutor(_ => new RoutingHandler(request => PveRoute(request, "[]", string.Empty)))
            .ExecuteAsync(new SensorExecutionContext("proxmox-backup-jobs", "pve1.example", PveSettings()));
        var forbidden = await new ProxmoxBackupJobsSensorExecutor(_ => new RoutingHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)))
            .ExecuteAsync(new SensorExecutionContext("proxmox-backup-jobs", "pve1.example", PveSettings()));

        Assert.Equal(SensorState.Warning, none.State);
        Assert.Contains("No backup job", none.Message);
        Assert.Equal(SensorState.Critical, forbidden.State);
        Assert.Contains("PVEAuditor", forbidden.Message);
    }

    [Fact]
    public void Pve_backup_log_parsing_handles_warnings_and_an_aborted_guest()
    {
        var runs = ProxmoxBackupJobsSensorExecutor.ParseLog(
        [
            "INFO: Starting Backup of VM 100 (qemu)",
            "WARN: unable to connect to the guest agent",
            "INFO: Finished Backup of VM 100 (00:00:40)",
            "INFO: Starting Backup of VM 200 (lxc)",
            "INFO: Finished Backup of VM 200 (00:00:05)",
            "INFO: Starting Backup of VM 101 (qemu)",
            "TASK ERROR: interrupted by signal"
        ]);

        Assert.Equal([(100, 1), (200, 0), (101, 2)], runs.Select(run => (run.VmId, run.Result)));
        Assert.Equal("unable to connect to the guest agent", runs[0].Message);
        Assert.Equal("done in 00:00:05", runs[1].Message);
    }

    // ---- FRITZ!Box -------------------------------------------------------------------------------------------------------

    private static Dictionary<string, string> FritzActions(string status = "Connected") => new()
    {
        ["GetStatusInfo"] = $"<NewConnectionStatus>{status}</NewConnectionStatus><NewLastConnectionError>{(status == "Connected" ? "ERROR_NONE" : "ERROR_NO_CARRIER")}</NewLastConnectionError><NewUptime>90000</NewUptime>",
        ["GetExternalIPAddress"] = "<NewExternalIPAddress>203.0.113.7</NewExternalIPAddress>",
        ["GetCommonLinkProperties"] = "<NewWANAccessType>DSL</NewWANAccessType><NewLayer1UpstreamMaxBitRate>40000000</NewLayer1UpstreamMaxBitRate><NewLayer1DownstreamMaxBitRate>250000000</NewLayer1DownstreamMaxBitRate><NewPhysicalLinkStatus>Up</NewPhysicalLinkStatus>",
        ["GetAddonInfos"] = "<NewByteSendRate>125000</NewByteSendRate><NewByteReceiveRate>3125000</NewByteReceiveRate><NewTotalBytesSent>1</NewTotalBytesSent>",
        ["deviceinfo#GetInfo"] = "<NewModelName>FRITZ!Box 7590 AX</NewModelName><NewSoftwareVersion>7.62</NewSoftwareVersion><NewUpTime>1209600</NewUpTime>",
        ["wandslifconfig1#GetInfo"] = "<NewStatus>Up</NewStatus><NewUpstreamCurrRate>40000</NewUpstreamCurrRate><NewDownstreamCurrRate>250000</NewDownstreamCurrRate><NewUpstreamNoiseMargin>80</NewUpstreamNoiseMargin><NewDownstreamNoiseMargin>52</NewDownstreamNoiseMargin><NewUpstreamAttenuation>110</NewUpstreamAttenuation><NewDownstreamAttenuation>180</NewDownstreamAttenuation>"
    };

    private static HttpResponseMessage Soap(Dictionary<string, string> actions, HttpRequestMessage request, bool loginWanted = false)
    {
        var soapAction = request.Headers.GetValues("SOAPAction").Single().Trim('"');
        var action = soapAction.Split('#')[1];
        var path = request.RequestUri!.AbsolutePath;
        var key = path.StartsWith("/upnp/control/", StringComparison.Ordinal) ? path["/upnp/control/".Length..] + "#" + action : action;
        if (path.StartsWith("/upnp/", StringComparison.Ordinal) && loginWanted)
        {
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }

        if (!actions.TryGetValue(key, out var body))
        {
            return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("<s:Envelope/>") };
        }

        var service = soapAction.Split('#')[0];
        var xml = $"<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><u:{action}Response xmlns:u=\"{service}\">{body}</u:{action}Response></s:Body></s:Envelope>";
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, "text/xml") };
    }

    [Fact]
    public async Task FritzBox_reads_the_connection_without_a_login()
    {
        NetworkCredential? used = null;
        var handler = new RoutingHandler(request => Soap(FritzActions(), request));
        var result = await new FritzBoxSensorExecutor(credential => { used = credential; return handler; })
            .ExecuteAsync(new SensorExecutionContext("fritzbox", "fritz.box", new MonitoringSettings()));

        Assert.Null(used);
        Assert.Equal(SensorState.Healthy, result.State);
        Assert.Equal("downloadKbps", result.DefaultChannelKey);
        Assert.Equal(1, Channel(result, "connected"));
        Assert.Equal(25000, Channel(result, "downloadKbps"));
        Assert.Equal(1000, Channel(result, "uploadKbps"));
        Assert.Equal(10, Channel(result, "downloadUtilization"));
        Assert.Equal(2.5, Channel(result, "uploadUtilization"));
        Assert.Equal(250000, Channel(result, "downstreamMaxKbps"));
        Assert.Equal(25, Channel(result, "connectionUptimeHours"));
        Assert.DoesNotContain(result.Channels, channel => channel.Key.StartsWith("dsl", StringComparison.Ordinal));
        Assert.Equal("FRITZ!Box: internet connected (DSL), 250 / 40 Mbit/s, public address 203.0.113.7", result.Message);
        Assert.Equal("http://fritz.box:49000/igdupnp/control/WANIPConn1", handler.Requests[0].RequestUri!.ToString());
        Assert.Equal("\"urn:schemas-upnp-org:service:WANIPConnection:1#GetStatusInfo\"", handler.Requests[0].Headers.GetValues("SOAPAction").Single());
    }

    [Fact]
    public async Task FritzBox_with_a_user_reads_the_box_and_the_dsl_line()
    {
        NetworkCredential? used = null;
        var settings = new MonitoringSettings();
        settings.Parameters["generic.username"] = "monitor";
        settings.Parameters["generic.password"] = "pw";
        SensorThresholdDefaults.Apply("fritzbox", settings);
        var result = await new FritzBoxSensorExecutor(credential => { used = credential; return new RoutingHandler(request => Soap(FritzActions(), request)); })
            .ExecuteAsync(new SensorExecutionContext("fritzbox", "192.168.178.1", settings));

        Assert.Equal("monitor", used!.UserName);
        Assert.Equal(SensorState.Warning, result.State); // 5.2 dB downstream noise margin is below the default 6
        Assert.Equal(5.2, Channel(result, "dslDownstreamSnrDb"));
        Assert.Equal(8, Channel(result, "dslUpstreamSnrDb"));
        Assert.Equal(18, Channel(result, "dslDownstreamAttenuationDb"));
        Assert.Equal(250000, Channel(result, "dslDownstreamSyncKbps"));
        Assert.Equal(336, Channel(result, "uptimeHours"));
        Assert.StartsWith("FRITZ!Box 7590 AX: internet connected (DSL)", result.Message);
        Assert.Contains("FRITZ!OS 7.62", result.Message);
    }

    [Fact]
    public async Task FritzBox_not_connected_is_critical_and_missing_pieces_are_said()
    {
        var down = await new FritzBoxSensorExecutor(_ => new RoutingHandler(request => Soap(FritzActions("Disconnected"), request)))
            .ExecuteAsync(new SensorExecutionContext("fritzbox", "fritz.box", new MonitoringSettings()));
        var noStatus = await new FritzBoxSensorExecutor(_ => new RoutingHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)))
            .ExecuteAsync(new SensorExecutionContext("fritzbox", "fritz.box", new MonitoringSettings()));
        var settings = new MonitoringSettings();
        settings.Parameters["generic.username"] = "monitor";
        settings.Parameters["generic.password"] = "wrong";
        var badLogin = await new FritzBoxSensorExecutor(_ => new RoutingHandler(request => Soap(FritzActions(), request, loginWanted: true)))
            .ExecuteAsync(new SensorExecutionContext("fritzbox", "fritz.box", settings));

        Assert.Equal(SensorState.Critical, down.State);
        Assert.Equal(0, Channel(down, "connected"));
        Assert.Contains("internet NOT connected (Disconnected, ERROR_NO_CARRIER)", down.Message);
        Assert.Equal(SensorState.Critical, noStatus.State);
        Assert.Contains("Transmit status information over UPnP", noStatus.Message);
        Assert.Equal(SensorState.Healthy, badLogin.State); // the connection is fine; only the line values are missing
        Assert.Contains("refused the user name or password", badLogin.Message);
    }

    [Fact]
    public void FritzBox_soap_envelope_and_answer()
    {
        var envelope = FritzBoxSensorExecutor.Envelope("urn:schemas-upnp-org:service:WANIPConnection:1", "GetStatusInfo");
        Assert.Contains("<u:GetStatusInfo xmlns:u=\"urn:schemas-upnp-org:service:WANIPConnection:1\"/>", envelope);

        var values = FritzBoxSensorExecutor.ParseResponse(
            "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><u:GetStatusInfoResponse xmlns:u=\"x\"><NewConnectionStatus> Connected </NewConnectionStatus></u:GetStatusInfoResponse></s:Body></s:Envelope>");
        Assert.Equal("Connected", values["NewConnectionStatus"]);
    }

    [Fact]
    public async Task The_three_suggest_themselves_on_their_ports()
    {
        static SensorDiscoveryContext Ports(params int[] ports) => new("10.0.0.5", true, 1, ports, false, null, "public", "2c", 161, TimeSpan.FromSeconds(1));

        Assert.True((await new FritzBoxSensorExecutor().DiscoverAsync(Ports(49000))).IsAvailable);
        Assert.True((await new ProxmoxBackupSensorExecutor().DiscoverAsync(Ports(8007))).IsAvailable);
        Assert.False((await new FritzBoxSensorExecutor().DiscoverAsync(Ports(80, 443))).IsAvailable);
        Assert.False((await new ProxmoxBackupSensorExecutor().DiscoverAsync(Ports(8006))).IsAvailable);
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------------

    private static double? Channel(SensorExecutionResult result, string key) => result.Channels.SingleOrDefault(channel => channel.Key == key)?.Value;

    private static HttpResponseMessage Json(Dictionary<string, string> resources, HttpRequestMessage request) =>
        resources.TryGetValue(request.RequestUri!.AbsolutePath, out var json)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }
            : new HttpResponseMessage(HttpStatusCode.NotFound);

    /// <summary>Answers every request with the route's response and keeps a copy of the request (headers included).</summary>
    private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var copy = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var header in request.Headers)
            {
                copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            Requests.Add(copy);
            return Task.FromResult(route(request));
        }
    }
}
