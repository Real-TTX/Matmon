using System.Text.Json.Nodes;
using Matmon.Core.Domain;
using Matmon.Core.Telemetry;
using Matmon.Host.Services;
using Matmon.Host.Services.Api;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matmon.Tests;

/// <summary>
/// The MCP server (<c>/api/mcp</c>): the JSON-RPC handshake, tools offered only for the token's scopes, reading the
/// monitoring, the actions (acknowledge, mute, maintenance, run now) and refusals in words.
/// </summary>
public sealed class McpServerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "matmon-mcp-" + Guid.NewGuid().ToString("N"));

    public McpServerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void The_handshake_answers_with_a_known_protocol_version_and_notifications_get_no_answer()
    {
        Run(store =>
        {
            var server = new McpServer(store);
            var token = Token(ApiTokenScopes.Read);

            var init = server.Handle(Request(1, "initialize", new JsonObject { ["protocolVersion"] = "2025-03-26" }), token, DateTimeOffset.UtcNow)!;
            Assert.Equal("2025-03-26", init["result"]!["protocolVersion"]!.GetValue<string>());
            Assert.Equal("matmon", init["result"]!["serverInfo"]!["name"]!.GetValue<string>());

            var unknownVersion = server.Handle(Request(2, "initialize", new JsonObject { ["protocolVersion"] = "1999-01-01" }), token, DateTimeOffset.UtcNow)!;
            Assert.Equal(McpServer.ProtocolVersions[0], unknownVersion["result"]!["protocolVersion"]!.GetValue<string>());

            Assert.Null(server.Handle(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }, token, DateTimeOffset.UtcNow));
            Assert.NotNull(server.Handle(Request(3, "ping"), token, DateTimeOffset.UtcNow)!["result"]);
            Assert.Equal(-32601, server.Handle(Request(4, "nope"), token, DateTimeOffset.UtcNow)!["error"]!["code"]!.GetValue<int>());
            Assert.Equal(-32600, server.Handle(new JsonArray(), token, DateTimeOffset.UtcNow)!["error"]!["code"]!.GetValue<int>());
        });
    }

    [Fact]
    public void The_server_is_off_until_an_admin_switches_it_on_and_a_backup_never_switches_it_on()
    {
        Run(store =>
        {
            Assert.False(store.IsMcpEnabled());
            store.SetMcpEnabled(true);
            Assert.True(store.IsMcpEnabled());

            var package = System.Text.Encoding.UTF8.GetString(store.CreateBackupBytes(WorkspaceBackupSection.All));
            Assert.DoesNotContain("\"mcpEnabled\":true", package, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void Only_the_tools_the_token_may_use_are_offered_and_others_are_refused()
    {
        Run(store =>
        {
            var server = new McpServer(store);
            var readOnly = Token(ApiTokenScopes.Read);
            var names = ToolNames(server, readOnly);
            Assert.Contains("get_overview", names);
            Assert.Contains("get_sensor", names);
            Assert.DoesNotContain("acknowledge_alert", names);
            Assert.DoesNotContain("create_maintenance_window", names);

            var everything = Token(ApiTokenScopes.Read, ApiTokenScopes.Alerts, ApiTokenScopes.Maintenance, ApiTokenScopes.Run);
            Assert.Equal(12, ToolNames(server, everything).Count);

            var refused = Call(server, readOnly, "mute_alerts", new JsonObject { ["element"] = "x" });
            Assert.True(refused["isError"]!.GetValue<bool>());
            Assert.Contains("alerts", Text(refused));
        });
    }

    [Fact]
    public void Sensors_are_found_read_and_addressed_by_path_name_or_id()
    {
        Run(store =>
        {
            var root = store.GetAllElements().OfType<ProbeElement>().First(probe => probe.ParentId is null);
            var host = store.CreateHost(root.Id, "web-01", "10.0.0.10", null);
            var sensor = store.CreateSensor(host.Id, "HTTPS", "http", "https://web-01", null);
            store.CreateSensor(host.Id, "Ping", "ping", "10.0.0.10", null);
            store.RecordSensorObservation(sensor.Id, SensorExecutionResult.Critical(TimeSpan.FromMilliseconds(40), "503 Service Unavailable"), DateTimeOffset.UtcNow);

            var server = new McpServer(store);
            var token = Token(ApiTokenScopes.Read);

            var overview = Structured(Call(server, token, "get_overview", []));
            Assert.Equal(1, overview["sensors"]!["error"]!.GetValue<int>());

            var problems = Structured(Call(server, token, "find_sensors", new JsonObject { ["state"] = "problems" }));
            var found = Assert.Single(problems["sensors"]!.AsArray());
            Assert.Equal("HTTPS", found!["name"]!.GetValue<string>());
            Assert.Equal($"{root.Name} / web-01 / HTTPS", found["path"]!.GetValue<string>());

            var byPath = Structured(Call(server, token, "get_sensor", new JsonObject { ["sensor"] = $"{root.Name}/web-01/https" }));
            Assert.Equal("error", byPath["state"]!.GetValue<string>());
            Assert.Equal("503 Service Unavailable", byPath["latest"]!["message"]!.GetValue<string>());

            var byName = Structured(Call(server, token, "get_sensor", new JsonObject { ["sensor"] = "Ping" }));
            Assert.Equal("unknown", byName["state"]!.GetValue<string>());

            var byId = Structured(Call(server, token, "get_sensor_history", new JsonObject { ["sensor"] = sensor.Id.ToString(), ["hours"] = 1 }));
            Assert.Single(byId["checks"]!.AsArray());

            var missing = Call(server, token, "get_sensor", new JsonObject { ["sensor"] = "nothing-like-this" });
            Assert.True(missing["isError"]!.GetValue<bool>());
        });
    }

    [Fact]
    public void Alerts_are_listed_acknowledged_and_muted_in_the_tokens_name()
    {
        Run(store =>
        {
            var root = store.GetAllElements().OfType<ProbeElement>().First(probe => probe.ParentId is null);
            var host = store.CreateHost(root.Id, "nas", "10.0.0.20", null);
            var sensor = store.CreateSensor(host.Id, "Disk", "ping", "10.0.0.20", null);
            store.RecordSensorObservation(sensor.Id, SensorExecutionResult.Critical(TimeSpan.FromMilliseconds(5), "down"), DateTimeOffset.UtcNow);

            var server = new McpServer(store);
            var token = Token(ApiTokenScopes.Read, ApiTokenScopes.Alerts);
            var listed = Structured(Call(server, token, "list_alerts", []));
            var alert = Assert.Single(listed["alerts"]!.AsArray());
            var alertId = alert!["id"]!.GetValue<Guid>();

            var ack = Call(server, token, "acknowledge_alert", new JsonObject { ["alert_id"] = alertId.ToString() });
            Assert.False(ack["isError"]!.GetValue<bool>());
            var stored = store.Workspace.Alerts.Single(entry => entry.Id == alertId);
            Assert.NotNull(stored.AcknowledgedUtc);
            Assert.Contains("MCP", stored.AcknowledgedBy);

            var mute = Call(server, token, "mute_alerts", new JsonObject { ["element"] = "nas", ["minutes"] = 60 });
            Assert.False(mute["isError"]!.GetValue<bool>());
            Assert.Single(store.GetActiveAlertMutes());
            Assert.False(Call(server, token, "unmute_alerts", new JsonObject { ["element"] = "nas" })["isError"]!.GetValue<bool>());
            Assert.Empty(store.GetActiveAlertMutes());

            var badMinutes = Call(server, token, "mute_alerts", new JsonObject { ["element"] = "nas", ["minutes"] = 0 });
            Assert.True(badMinutes["isError"]!.GetValue<bool>());
        });
    }

    [Fact]
    public void Maintenance_windows_and_run_now_go_through_the_same_rules_as_the_api()
    {
        Run(store =>
        {
            var root = store.GetAllElements().OfType<ProbeElement>().First(probe => probe.ParentId is null);
            var host = store.CreateHost(root.Id, "db-01", "10.0.0.30", null);
            var sensor = store.CreateSensor(host.Id, "SQL", "ping", "10.0.0.30", null);
            var woken = new List<Guid>();
            var server = new McpServer(store, ids => { woken.AddRange(ids); return ids.Count; });
            var token = Token(ApiTokenScopes.Maintenance, ApiTokenScopes.Run);

            var created = Call(server, token, "create_maintenance_window", new JsonObject { ["target"] = "db-01", ["duration_minutes"] = 30 });
            Assert.False(created["isError"]!.GetValue<bool>());
            var window = Assert.Single(store.GetMaintenanceWindows());
            Assert.Contains("MCP", window.CreatedBy);

            Assert.True(Call(server, token, "create_maintenance_window", new JsonObject { ["target"] = "db-01", ["duration_minutes"] = 0 })["isError"]!.GetValue<bool>());
            Assert.False(Call(server, token, "end_maintenance_window", new JsonObject { ["id"] = window.Id.ToString() })["isError"]!.GetValue<bool>());

            Assert.False(Call(server, token, "run_sensor_now", new JsonObject { ["sensor"] = "SQL" })["isError"]!.GetValue<bool>());
            Assert.Equal([sensor.Id], woken);
        });
    }

    private static List<string> ToolNames(McpServer server, ApiToken token) =>
        server.Handle(Request(9, "tools/list"), token, DateTimeOffset.UtcNow)!["result"]!["tools"]!.AsArray()
            .Select(tool => tool!["name"]!.GetValue<string>()).ToList();

    private static JsonObject Call(McpServer server, ApiToken token, string name, JsonObject arguments) =>
        server.Handle(Request(10, "tools/call", new JsonObject { ["name"] = name, ["arguments"] = arguments }), token, DateTimeOffset.UtcNow)!["result"]!.AsObject();

    private static string Text(JsonObject result) => result["content"]![0]!["text"]!.GetValue<string>();

    private static JsonObject Structured(JsonObject result)
    {
        Assert.False(result["isError"]!.GetValue<bool>(), Text(result));
        return result["structuredContent"]!.AsObject();
    }

    private static JsonObject Request(int id, string method, JsonObject? parameters = null) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["method"] = method,
        ["params"] = parameters ?? []
    };

    private static ApiToken Token(params string[] scopes) => new() { Name = "assistant", Scopes = [.. scopes] };

    private void Run(Action<InMemoryMonitoringWorkspaceStore> body)
    {
        using var telemetry = new SqliteTelemetryRepository(Path.Combine(_dir, "telemetry.db"));
        using var store = new InMemoryMonitoringWorkspaceStore(
            new McpTestHostEnvironment(_dir),
            new MatmonRuntimeOptions { WorkspacePath = Path.Combine(_dir, "workspace.json") },
            new MatmonAuthOptions(),
            new EphemeralDataProtectionProvider(),
            telemetry,
            NullLogger<InMemoryMonitoringWorkspaceStore>.Instance);
        body(store);
    }
}

file sealed class McpTestHostEnvironment(string contentRoot) : IHostEnvironment
{
    public string ApplicationName { get; set; } = "Matmon.Tests";
    public string EnvironmentName { get; set; } = "Development";
    public string ContentRootPath { get; set; } = contentRoot;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
