using System.Net;
using Matmon.Core.Domain;
using Matmon.Host.Services;

namespace Matmon.Tests;

/// <summary>The cloud Executor runs a customer's target. Before any sensor runs, its host must resolve to public addresses
/// only - a ping, a port check, a certificate read or a DNS query must never reach the network the cloud runs in.</summary>
public sealed class ExecutorTargetGuardTests
{
    private static readonly Dictionary<string, IPAddress[]> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["example.com"] = [IPAddress.Parse("93.184.215.14")],
        ["postgres"] = [IPAddress.Parse("172.18.0.3")],
        ["mixed.example"] = [IPAddress.Parse("93.184.215.14"), IPAddress.Parse("10.0.0.7")],
        ["resolver.example"] = [IPAddress.Parse("1.1.1.1")],
    };

    private static Task<IPAddress[]> Resolve(string host, CancellationToken _) =>
        Task.FromResult(Names.TryGetValue(host, out var found) ? found : []);

    private static Task<string?> Check(string type, string target, Dictionary<string, string>? parameters = null) =>
        ExecutorTargetGuard.CheckAsync(type, target, parameters ?? [], Resolve, CancellationToken.None);

    [Theory]
    [InlineData("ping", "example.com")]
    [InlineData("tcp-port", "example.com:443")]
    [InlineData("ssl-certificate", "https://example.com/")]
    [InlineData("ping", "93.184.215.14")]
    [InlineData("ping", "2606:4700:4700::1111")]
    [InlineData("tcp-port", "[2606:4700:4700::1111]:53")]
    public async Task A_public_target_may_run(string type, string target) =>
        Assert.Null(await Check(type, target));

    [Theory]
    [InlineData("ping", "127.0.0.1")]
    [InlineData("ping", "localhost")]
    [InlineData("tcp-port", "postgres:5432")]
    [InlineData("tcp-port", "10.0.0.5:22")]
    [InlineData("ssl-certificate", "https://169.254.169.254/")]
    [InlineData("ping", "[::1]")]
    [InlineData("ping", "mixed.example")]
    [InlineData("ping", "unknown.invalid")]
    [InlineData("ping", "")]
    [InlineData("local-script", "")]
    public async Task A_private_unresolvable_or_missing_target_is_refused(string type, string target) =>
        Assert.NotNull(await Check(type, target));

    [Fact]
    public async Task Dns_checks_the_named_server_else_the_answer()
    {
        // a public resolver may be asked about any name
        Assert.Null(await Check("dns", "postgres", new() { ["dns.server"] = "resolver.example" }));
        // a private resolver may not be asked at all
        Assert.NotNull(await Check("dns", "example.com", new() { ["dns.server"] = "10.0.0.53" }));
        // without a server the executor's own resolver answers - an answer pointing inside is refused
        Assert.Null(await Check("dns", "example.com"));
        Assert.NotNull(await Check("dns", "postgres"));
        Assert.NotNull(await Check("dns", "", new() { ["dns.recordName"] = "postgres" }));
    }

    [Fact]
    public async Task The_runner_refuses_before_the_sensor_runs()
    {
        var runner = new StatelessSensorRunner([new TcpPortSensorExecutor()], Resolve);
        var result = await runner.ExecuteAsync(new ExecuteSensorRequest("tcp-port", "postgres:5432", null, null, null), CancellationToken.None);

        Assert.Equal(SensorState.Critical, result.State);
        Assert.Contains("public", result.Message);
    }

    private sealed class LocalScriptStandIn : ISensorExecutor
    {
        public bool Ran { get; private set; }
        public string SensorTypeKey => "local-script";
        public ValueTask<SensorDiscoveryCheckResult> DiscoverAsync(SensorDiscoveryContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(SensorDiscoveryCheckResult.NotAvailable);
        public ValueTask<SensorExecutionResult> ExecuteAsync(SensorExecutionContext context, CancellationToken cancellationToken = default)
        {
            Ran = true;
            return ValueTask.FromResult(SensorExecutionResult.Healthy(TimeSpan.Zero, "ran"));
        }
    }

    [Fact]
    public async Task Only_cloud_sensor_types_run_in_the_executor()
    {
        // a holder of the executor token must not be able to run a local script or program inside the sidecar
        var script = new LocalScriptStandIn();
        var runner = new StatelessSensorRunner([script, new TcpPortSensorExecutor()], Resolve);

        var result = await runner.ExecuteAsync(new ExecuteSensorRequest("local-script", "example.com", null, null, null), CancellationToken.None);

        Assert.False(script.Ran);
        Assert.Equal(SensorState.Critical, result.State);
        Assert.DoesNotContain(runner.Catalog, definition => definition.Key == "local-script");
        Assert.DoesNotContain("local-program", StatelessSensorRunner.CloudSensorTypes);
    }

    [Fact]
    public async Task A_host_and_port_target_is_not_mistaken_for_a_url()
    {
        // "localhost:1" used to parse as a URL with the scheme "localhost" and no host - "target host is required"
        var result = await new TcpPortSensorExecutor().ExecuteAsync(new SensorExecutionContext("tcp-port", "localhost:1", new MonitoringSettings()));
        Assert.Contains("port 1", result.Message);
    }

    [Theory]
    [InlineData("https://user@example.com:8443/x", "example.com")]
    [InlineData("example.com/path", "example.com")]
    [InlineData("example.com.", "example.com")]
    [InlineData("[fe80::1]:22", "fe80::1")]
    [InlineData("fe80::1", "fe80::1")]
    public void The_host_is_read_from_every_target_shape(string target, string host) =>
        Assert.Equal(host, ExecutorTargetGuard.HostOf(target));
}
