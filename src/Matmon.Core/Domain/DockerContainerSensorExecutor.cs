using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;

namespace Matmon.Core.Domain;

public sealed class DockerContainerSensorExecutor : ISensorExecutor
{
    public static SensorDefinition Definition { get; } = new()
    {
        Key = "docker-container",
        DisplayName = "Docker Container",
        Description = "Checks a Docker container through the Docker Engine socket mounted into the probe.",
        ChannelMode = SensorChannelMode.Fixed,
        Parameters =
        [
            new SensorParameterDefinition
            {
                Key = "docker.containerName",
                Label = "Container",
                Kind = SensorParameterKind.Text,
                Description = "Container name or ID. Empty uses the sensor target.",
                Required = true,
                Placeholder = "matmon-primary"
            },
            new SensorParameterDefinition
            {
                Key = "docker.socket",
                Label = "Docker socket",
                Kind = SensorParameterKind.Text,
                Description = "Docker Engine Unix socket path inside the probe container.",
                DefaultValue = "/var/run/docker.sock"
            }
        ]
    };

    public string SensorTypeKey => Definition.Key;

    public ValueTask<SensorDiscoveryCheckResult> DiscoverAsync(
        SensorDiscoveryContext context,
        CancellationToken cancellationToken = default)
    {
        _ = context;
        _ = cancellationToken;
        return ValueTask.FromResult(SensorDiscoveryCheckResult.NotAvailable);
    }

    public async ValueTask<SensorExecutionResult> ExecuteAsync(
        SensorExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var containerName = MonitoringSettings.TryReadParameter(context.Settings, "docker.containerName", out var configuredContainerName) &&
            !string.IsNullOrWhiteSpace(configuredContainerName)
            ? configuredContainerName.Trim()
            : context.Target.Trim();
        if (string.IsNullOrWhiteSpace(containerName))
        {
            return SensorExecutionResult.Critical(TimeSpan.Zero, "container name or target is required");
        }

        var socketPath = MonitoringSettings.TryReadParameter(context.Settings, "docker.socket", out var configuredSocket) &&
            !string.IsNullOrWhiteSpace(configuredSocket)
            ? configuredSocket.Trim()
            : "/var/run/docker.sock";
        if (!File.Exists(socketPath))
        {
            return SensorExecutionResult.Critical(TimeSpan.Zero, $"docker socket not found: {socketPath}");
        }

        var timeout = context.Settings.Timeout ?? TimeSpan.FromSeconds(5);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        var watch = Stopwatch.StartNew();

        try
        {
            using var client = CreateDockerClient(socketPath, timeout);
            using var listResponse = await client.GetAsync("http://docker/containers/json?all=true", timeoutCts.Token);
            listResponse.EnsureSuccessStatusCode();
            await using var listStream = await listResponse.Content.ReadAsStreamAsync(timeoutCts.Token);
            using var listJson = await JsonDocument.ParseAsync(listStream, cancellationToken: timeoutCts.Token);
            var id = FindContainerId(listJson.RootElement, containerName);
            if (string.IsNullOrWhiteSpace(id))
            {
                watch.Stop();
                return SensorExecutionResult.Critical(watch.Elapsed, $"container '{containerName}' not found");
            }

            using var inspectResponse = await client.GetAsync($"http://docker/containers/{Uri.EscapeDataString(id)}/json", timeoutCts.Token);
            inspectResponse.EnsureSuccessStatusCode();
            await using var inspectStream = await inspectResponse.Content.ReadAsStreamAsync(timeoutCts.Token);
            using var inspectJson = await JsonDocument.ParseAsync(inspectStream, cancellationToken: timeoutCts.Token);
            watch.Stop();

            var state = inspectJson.RootElement.TryGetProperty("State", out var stateElement)
                ? stateElement
                : default;
            var running = state.ValueKind == JsonValueKind.Object &&
                state.TryGetProperty("Running", out var runningElement) &&
                runningElement.GetBoolean();
            var restartCount = inspectJson.RootElement.TryGetProperty("RestartCount", out var restartElement) &&
                restartElement.TryGetInt32(out var restarts)
                ? restarts
                : 0;
            var healthStatus = state.ValueKind == JsonValueKind.Object &&
                state.TryGetProperty("Health", out var healthElement) &&
                healthElement.ValueKind == JsonValueKind.Object &&
                healthElement.TryGetProperty("Status", out var healthStatusElement)
                ? healthStatusElement.GetString()
                : "none";
            var healthOk = string.Equals(healthStatus, "none", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(healthStatus, "healthy", StringComparison.OrdinalIgnoreCase)
                    ? 1d
                    : 0d;

            var channels = new[]
            {
                new SensorChannelValue
                {
                    Key = "running",
                    Label = "Running",
                    Value = running ? 1 : 0,
                    IsDefault = true
                },
                new SensorChannelValue
                {
                    Key = "restartCount",
                    Label = "Restarts",
                    Value = restartCount
                },
                new SensorChannelValue
                {
                    Key = "healthOk",
                    Label = "Health OK",
                    Value = healthOk
                }
            };

            var result = running && healthOk > 0.5
                ? SensorExecutionResult.Healthy(
                    watch.Elapsed,
                    healthStatus == "none" ? "container running" : $"container running, health {healthStatus}",
                    1,
                    "running",
                    channels)
                : SensorExecutionResult.Critical(
                    watch.Elapsed,
                    running ? $"container health {healthStatus}" : "container not running",
                    running ? healthOk : 0,
                    "running",
                    channels);

            return SensorThresholdEvaluator.ApplyChannelThresholds(context.Settings, result);
        }
        catch (OperationCanceledException)
        {
            watch.Stop();
            return SensorExecutionResult.Critical(watch.Elapsed, "docker request timeout");
        }
        catch (Exception ex)
        {
            watch.Stop();
            return SensorExecutionResult.Critical(watch.Elapsed, ex.Message);
        }
    }

    private static HttpClient CreateDockerClient(string socketPath, TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };

        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = timeout
        };
    }

    private static string? FindContainerId(JsonElement containers, string nameOrId)
    {
        foreach (var container in containers.EnumerateArray())
        {
            var id = container.TryGetProperty("Id", out var idElement)
                ? idElement.GetString()
                : null;
            if (!string.IsNullOrWhiteSpace(id) &&
                id.StartsWith(nameOrId, StringComparison.OrdinalIgnoreCase))
            {
                return id;
            }

            if (!container.TryGetProperty("Names", out var namesElement) ||
                namesElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var name in namesElement.EnumerateArray())
            {
                var normalized = name.GetString()?.TrimStart('/');
                if (string.Equals(normalized, nameOrId, StringComparison.OrdinalIgnoreCase))
                {
                    return id;
                }
            }
        }

        return null;
    }
}
