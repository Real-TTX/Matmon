using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace Matmon.Core.Domain;

public sealed class DockerContainerSensorExecutor : ISensorExecutor
{
    public static SensorDefinition Definition { get; } = new()
    {
        Key = "docker-container",
        DisplayName = "Docker Container",
        Description = "Checks a Docker container via the Docker Engine API - the local socket on the probe, a remote host over SSH (ssh://user@host, no daemon exposure), or over TCP (optionally TLS).",
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
                Description = "Remote Docker endpoint: ssh://user@host[:port] (recommended - tunnels the API over SSH, no exposed daemon) or tcp://host:2375 (plain) / tcp://host:2376 (TLS). Leave empty to use the local socket below.",
                Placeholder = "ssh://docker@docker-host"
            },
            new SensorParameterDefinition
            {
                Key = "docker.sshKeyFile",
                Label = "SSH private key",
                Kind = SensorParameterKind.Text,
                Description = "For an ssh:// endpoint: path (on the probe) to the private key used for key-based auth. Empty uses the probe's default keys (~/.ssh). Password auth is not supported (the remote host must trust the key).",
                Placeholder = "/keys/id_ed25519"
            },
            new SensorParameterDefinition
            {
                Key = "docker.sshKnownHostsFile",
                Label = "SSH known_hosts file",
                Kind = SensorParameterKind.Text,
                Description = "For an ssh:// endpoint: path to a known_hosts file (on a persisted volume) holding the remote's expected host key - enables strict host-key checking (MITM-safe). Empty trusts the host key on first connect (accept-new); note the probe's default known_hosts is not persisted across container upgrades.",
                Placeholder = "/data/ssh/known_hosts"
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
            // Unwrap to the innermost message: HttpClient wraps a transport IOException (e.g. the SSH tunnel's
            // captured stderr) in a generic HttpRequestException, so ex.Message alone would hide the real cause.
            var cause = ex;
            while (cause.InnerException is not null)
            {
                cause = cause.InnerException;
            }

            return SensorExecutionResult.Critical(watch.Elapsed, cause.Message);
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

        // SSH endpoint -> tunnel the Engine API over SSH (like `docker -H ssh://`), no exposed daemon.
        if (endpoint.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
        {
            return CreateSshDockerClient(context, endpoint, timeout);
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

    // ssh://[user@]host[:port] - runs `docker system dial-stdio` on the remote over SSH and pipes the
    // Engine API HTTP/1.1 conversation through the ssh process's stdio (exactly how `docker -H ssh://` works).
    // Key-based auth only (BatchMode), so it never blocks on a password prompt; the `ssh` binary ships in the image.
    private static HttpClient CreateSshDockerClient(SensorExecutionContext context, string endpoint, TimeSpan timeout)
    {
        Uri uri;
        try
        {
            uri = new Uri(endpoint);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"invalid ssh endpoint '{endpoint}': {ex.Message}");
        }

        var host = uri.Host;
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new InvalidOperationException($"ssh endpoint '{endpoint}' has no host");
        }

        var user = string.IsNullOrWhiteSpace(uri.UserInfo) ? null : Uri.UnescapeDataString(uri.UserInfo);

        // Guard: a host/user starting with '-' could be mis-parsed by ssh as an option (e.g. -oProxyCommand=).
        // ArgumentList already prevents shell/word-splitting injection; this closes the option-injection gap.
        if (host.StartsWith('-') || (user is not null && user.StartsWith('-')))
        {
            throw new InvalidOperationException($"ssh endpoint '{endpoint}' has an invalid host or user");
        }

        var port = uri.Port > 0 ? uri.Port : 22;
        var keyFile = MonitoringSettings.TryReadParameter(context.Settings, "docker.sshKeyFile", out var configuredKey) && !string.IsNullOrWhiteSpace(configuredKey)
            ? configuredKey.Trim()
            : null;
        // A configured known_hosts file (on a persisted volume) enables strict host-key checking; without one we
        // trust-on-first-use (accept-new), since the default known_hosts is wiped on every container upgrade.
        var knownHostsFile = MonitoringSettings.TryReadParameter(context.Settings, "docker.sshKnownHostsFile", out var configuredKnownHosts) && !string.IsNullOrWhiteSpace(configuredKnownHosts)
            ? configuredKnownHosts.Trim()
            : null;
        var connectSeconds = Math.Max(1, (int)Math.Ceiling(timeout.TotalSeconds));

        var handler = new SocketsHttpHandler
        {
            // One ssh tunnel per HTTP connection; the daemon speaks HTTP/1.1 over the piped socket.
            ConnectCallback = (_, _) =>
            {
                var startInfo = new ProcessStartInfo("ssh")
                {
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };
                // ArgumentList (never a joined string) so host/user/key can't inject extra ssh options.
                startInfo.ArgumentList.Add("-o");
                startInfo.ArgumentList.Add("BatchMode=yes");
                startInfo.ArgumentList.Add("-o");
                startInfo.ArgumentList.Add(knownHostsFile is null ? "StrictHostKeyChecking=accept-new" : "StrictHostKeyChecking=yes");
                if (knownHostsFile is not null)
                {
                    startInfo.ArgumentList.Add("-o");
                    startInfo.ArgumentList.Add($"UserKnownHostsFile={knownHostsFile}");
                }

                startInfo.ArgumentList.Add("-o");
                startInfo.ArgumentList.Add($"ConnectTimeout={connectSeconds}");
                startInfo.ArgumentList.Add("-p");
                startInfo.ArgumentList.Add(port.ToString(CultureInfo.InvariantCulture));
                if (keyFile is not null)
                {
                    startInfo.ArgumentList.Add("-i");
                    startInfo.ArgumentList.Add(keyFile);
                }

                startInfo.ArgumentList.Add(user is null ? host : $"{user}@{host}");
                startInfo.ArgumentList.Add("docker");
                startInfo.ArgumentList.Add("system");
                startInfo.ArgumentList.Add("dial-stdio");

                var process = Process.Start(startInfo) ?? throw new InvalidOperationException("failed to start ssh");
                return ValueTask.FromResult<Stream>(new SshProcessStream(process));
            }
        };

        return new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri("http://docker/"),
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

    // A duplex stream over an `ssh ... docker system dial-stdio` process: writes go to ssh stdin, reads come
    // from ssh stdout, and it owns the process (killed on dispose). If ssh dies with a non-zero exit before the
    // Engine responds, the EOF is turned into an IOException carrying the captured stderr so the failure is
    // actionable (auth denied / host unreachable / no docker CLI on the remote) instead of a blank socket error.
    private sealed class SshProcessStream : Stream
    {
        private readonly Process _process;
        private readonly Stream _stdin;
        private readonly Stream _stdout;
        private readonly StringBuilder _stderr = new();

        public SshProcessStream(Process process)
        {
            _process = process;
            _stdin = process.StandardInput.BaseStream;
            _stdout = process.StandardOutput.BaseStream;
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    lock (_stderr)
                    {
                        _stderr.AppendLine(e.Data);
                    }
                }
            };
            process.BeginErrorReadLine();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await _stdout.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                // Pipe EOF and process reaping are separate, non-atomic events: on a fast ssh failure the read
                // can hit EOF before HasExited flips. Wait for the real exit so a non-zero code (and its stderr)
                // is reliably observed instead of looking like a clean connection close.
                try
                {
                    await _process.WaitForExitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // Outer timeout / cancellation - let the caller surface it.
                }

                if (_process.HasExited && _process.ExitCode != 0)
                {
                    throw new IOException($"ssh docker tunnel failed: {StderrText()}");
                }
            }

            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => _stdin.WriteAsync(buffer, cancellationToken);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => _stdin.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Write(byte[] buffer, int offset, int count)
            => _stdin.Write(buffer, offset, count);

        public override void Flush() => _stdin.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => _stdin.FlushAsync(cancellationToken);

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        private string StderrText()
        {
            lock (_stderr)
            {
                return _stderr.Length == 0 ? "connection closed" : _stderr.ToString().Trim();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    _stdin.Dispose();
                    _stdout.Dispose();
                    if (!_process.HasExited)
                    {
                        _process.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception)
                {
                    // Best-effort teardown - the tunnel is being torn down anyway.
                }
                finally
                {
                    _process.Dispose();
                }
            }

            base.Dispose(disposing);
        }
    }
}
