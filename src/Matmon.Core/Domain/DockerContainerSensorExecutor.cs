using System.Diagnostics;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Matmon.Core.Domain;

public sealed class DockerContainerSensorExecutor : ISensorExecutor
{
    public static SensorDefinition Definition { get; } = new()
    {
        Key = "docker-container",
        DisplayName = "Docker Container",
        Description = "Checks a Docker container via the Docker Engine API - the local socket on the probe, or a remote host over TCP (optionally TLS).",
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
                Key = "docker.endpoint",
                Label = "Endpoint",
                Kind = SensorParameterKind.Text,
                Description = "Remote Docker Engine endpoint, e.g. tcp://host:2375 (plain) or tcp://host:2376 (TLS). Leave empty to use the local socket below.",
                Placeholder = "tcp://docker-host:2376"
            },
            new SensorParameterDefinition
            {
                Key = "docker.socket",
                Label = "Local socket",
                Kind = SensorParameterKind.Text,
                Description = "Docker Engine Unix socket path (used only when Endpoint is empty).",
                DefaultValue = "/var/run/docker.sock"
            },
            new SensorParameterDefinition
            {
                Key = "docker.tlsVerify",
                Label = "Verify TLS certificate",
                Kind = SensorParameterKind.Boolean,
                Description = "For a TLS endpoint (tcp://host:2376): validate the server certificate. Turn off for a self-signed daemon without a CA cert.",
                DefaultValue = "true"
            },
            new SensorParameterDefinition
            {
                Key = "docker.certDir",
                Label = "TLS cert directory",
                Kind = SensorParameterKind.Text,
                Description = "Optional: directory (on the probe) with ca.pem / cert.pem / key.pem for a TLS/mTLS daemon (the Docker DOCKER_CERT_PATH convention).",
                Placeholder = "/certs/docker"
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

        var timeout = context.Settings.Timeout ?? TimeSpan.FromSeconds(5);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        var watch = Stopwatch.StartNew();

        HttpClient client;
        try
        {
            client = CreateDockerClient(context, timeout);
        }
        catch (Exception ex)
        {
            watch.Stop();
            return SensorExecutionResult.Critical(watch.Elapsed, ex.Message);
        }

        try
        {
            using (client)
            {
                using var listResponse = await client.GetAsync("containers/json?all=true", timeoutCts.Token);
                listResponse.EnsureSuccessStatusCode();
                await using var listStream = await listResponse.Content.ReadAsStreamAsync(timeoutCts.Token);
                using var listJson = await JsonDocument.ParseAsync(listStream, cancellationToken: timeoutCts.Token);
                var id = FindContainerId(listJson.RootElement, containerName);
                if (string.IsNullOrWhiteSpace(id))
                {
                    watch.Stop();
                    return SensorExecutionResult.Critical(watch.Elapsed, $"container '{containerName}' not found");
                }

                using var inspectResponse = await client.GetAsync($"containers/{Uri.EscapeDataString(id)}/json", timeoutCts.Token);
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
                        Value = healthOk,
                        LogByDefault = false
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

    // Builds an HttpClient whose BaseAddress is the Docker Engine API, over either the local Unix socket
    // (default) or a remote TCP endpoint (optionally TLS/mTLS). Requests use paths relative to the base.
    private static HttpClient CreateDockerClient(SensorExecutionContext context, TimeSpan timeout)
    {
        var endpoint = MonitoringSettings.TryReadParameter(context.Settings, "docker.endpoint", out var configuredEndpoint)
            ? configuredEndpoint.Trim()
            : string.Empty;

        // No endpoint (or an explicit unix:// one) -> local Unix domain socket, as before.
        if (string.IsNullOrWhiteSpace(endpoint) || endpoint.StartsWith("unix://", StringComparison.OrdinalIgnoreCase))
        {
            var socketPath = endpoint.StartsWith("unix://", StringComparison.OrdinalIgnoreCase)
                ? endpoint["unix://".Length..]
                : MonitoringSettings.TryReadParameter(context.Settings, "docker.socket", out var configuredSocket) && !string.IsNullOrWhiteSpace(configuredSocket)
                    ? configuredSocket.Trim()
                    : "/var/run/docker.sock";
            if (!File.Exists(socketPath))
            {
                throw new InvalidOperationException($"docker socket not found: {socketPath}");
            }

            var socketHandler = new SocketsHttpHandler
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

            return new HttpClient(socketHandler, disposeHandler: true)
            {
                BaseAddress = new Uri("http://docker/"),
                Timeout = timeout
            };
        }

        // Remote TCP endpoint. Accept tcp://, http://, https:// or a bare host[:port].
        var normalized = endpoint
            .Replace("tcp://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("http://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .TrimEnd('/');
        var host = normalized;
        var port = 2375;
        var colonIndex = normalized.LastIndexOf(':');
        if (colonIndex > 0 && int.TryParse(normalized[(colonIndex + 1)..], out var parsedPort))
        {
            host = normalized[..colonIndex];
            port = parsedPort;
        }

        // TLS when the endpoint says so (https / port 2376) or a cert directory is configured.
        var certDir = MonitoringSettings.TryReadParameter(context.Settings, "docker.certDir", out var configuredCertDir) && !string.IsNullOrWhiteSpace(configuredCertDir)
            ? configuredCertDir.Trim()
            : null;
        var useTls = endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || port == 2376 || certDir is not null;

        if (!useTls)
        {
            return new HttpClient
            {
                BaseAddress = new Uri($"http://{host}:{port}/"),
                Timeout = timeout
            };
        }

        var verify = !MonitoringSettings.TryReadParameterBool(context.Settings, "docker.tlsVerify", out var configuredVerify) || configuredVerify;
        var tlsHandler = new SocketsHttpHandler { SslOptions = BuildTlsOptions(host, verify, certDir) };
        return new HttpClient(tlsHandler, disposeHandler: true)
        {
            BaseAddress = new Uri($"https://{host}:{port}/"),
            Timeout = timeout
        };
    }

    private static SslClientAuthenticationOptions BuildTlsOptions(string host, bool verify, string? certDir)
    {
        var options = new SslClientAuthenticationOptions { TargetHost = host };

        if (certDir is not null)
        {
            var certPem = Path.Combine(certDir, "cert.pem");
            var keyPem = Path.Combine(certDir, "key.pem");
            if (File.Exists(certPem) && File.Exists(keyPem))
            {
                // Round-trip through PKCS#12 so the private key is usable for TLS client auth on every platform.
                using var pemCert = X509Certificate2.CreateFromPemFile(certPem, keyPem);
                var clientCert = X509CertificateLoader.LoadPkcs12(pemCert.Export(X509ContentType.Pkcs12), password: null);
                options.ClientCertificates = [clientCert];
            }
        }

        X509Certificate2? caCert = null;
        if (certDir is not null)
        {
            var caPem = Path.Combine(certDir, "ca.pem");
            if (File.Exists(caPem))
            {
                caCert = X509Certificate2.CreateFromPem(File.ReadAllText(caPem));
            }
        }

        options.RemoteCertificateValidationCallback = (_, serverCert, chain, errors) =>
        {
            if (!verify)
            {
                return true; // explicitly trusting a self-signed daemon
            }

            if (caCert is null || serverCert is null)
            {
                return errors == SslPolicyErrors.None; // fall back to the system trust store
            }

            // Validate the server certificate against the provided CA (self-signed Docker TLS setups).
            using var customChain = new X509Chain
            {
                ChainPolicy =
                {
                    RevocationMode = X509RevocationMode.NoCheck,
                    TrustMode = X509ChainTrustMode.CustomRootTrust
                }
            };
            customChain.ChainPolicy.CustomTrustStore.Add(caCert);
            var serverCert2 = serverCert as X509Certificate2 ?? new X509Certificate2(serverCert);
            return customChain.Build(serverCert2);
        };

        return options;
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
