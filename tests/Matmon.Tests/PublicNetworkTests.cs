using Matmon.Core.Domain;
using Matmon.Host.Services;

namespace Matmon.Tests;

/// <summary>The cloud Executor runs an HTTP sensor against a URL a customer typed, so its HTTP client may only reach the
/// internet - never the network the cloud runs in.</summary>
public sealed class PublicNetworkTests
{
    private static HttpSensorExecutor PublicOnlyHttpSensor() =>
        new(new HttpClient(new SocketsHttpHandler { ConnectCallback = PublicNetwork.ConnectCallback("not public") }));

    [Theory]
    [InlineData("http://127.0.0.1:8055/")]
    [InlineData("http://localhost/")]
    [InlineData("http://10.0.0.5/")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://[::1]:8080/")]
    [InlineData("http://192.168.178.1/")]
    public async Task An_http_sensor_in_the_executor_does_not_reach_a_private_address(string url)
    {
        var result = await PublicOnlyHttpSensor().ExecuteAsync(new SensorExecutionContext("http", url, new MonitoringSettings()));

        Assert.Equal(SensorState.Critical, result.State);
        Assert.Contains("not public", result.Message);
    }
}
