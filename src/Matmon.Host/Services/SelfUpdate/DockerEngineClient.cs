using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Matmon.Host.Services.SelfUpdate;

/// <summary>
/// The few Docker Engine API calls the self-update needs, over the mounted socket. JSON stays <see cref="JsonNode"/> on
/// purpose: a container is recreated from its own inspect output, and every field this code does not know about (a
/// newer engine's, a compose label, a device mapping) has to come through untouched - a typed model would drop them.
/// </summary>
public sealed class DockerEngineClient : IDisposable
{
    public const string DefaultSocketPath = "/var/run/docker.sock";

    private readonly HttpClient _http;
    private readonly string? _registryAuth;

    public DockerEngineClient(string socketPath, string? registryUsername = null, string? registryPassword = null, TimeSpan? timeout = null)
    {
        SocketPath = socketPath;
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
        _http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri("http://docker/"),
            Timeout = timeout ?? TimeSpan.FromMinutes(15)
        };

        // A private registry (the cloud's image) needs credentials on every pull/lookup: the daemon does not use the
        // logins of the docker CLI on the host for API callers.
        if (!string.IsNullOrWhiteSpace(registryUsername) && !string.IsNullOrWhiteSpace(registryPassword))
        {
            var json = JsonSerializer.Serialize(new { username = registryUsername.Trim(), password = registryPassword.Trim() });
            _registryAuth = Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).Replace('+', '-').Replace('/', '_');
        }
    }

    public string SocketPath { get; }

    // File.Exists is true for a socket too (it only rules out directories) - the Docker sensor relies on the same.
    public static bool SocketExists(string socketPath) => !OperatingSystem.IsWindows() && File.Exists(socketPath);

    public Task<JsonObject?> InspectContainerAsync(string idOrName, CancellationToken cancellationToken) =>
        GetObjectAsync($"containers/{Uri.EscapeDataString(idOrName)}/json", cancellationToken);

    public Task<JsonObject?> InspectImageAsync(string reference, CancellationToken cancellationToken) =>
        GetObjectAsync($"images/{EscapeReference(reference)}/json", cancellationToken);

    /// <summary>The digest the registry serves for this tag right now - without pulling anything.</summary>
    public async Task<string?> GetRegistryDigestAsync(string reference, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"distribution/{EscapeReference(reference)}/json");
        AddAuth(request);
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, $"looking up {reference} in its registry", cancellationToken);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return body?["Descriptor"]?["digest"]?.GetValue<string>();
    }

    /// <summary>Pulls the image (a stream of progress lines - an error is one of them, not a status code).</summary>
    public async Task PullAsync(ImageReference reference, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"images/create?fromImage={Uri.EscapeDataString(reference.Repository)}&tag={Uri.EscapeDataString(reference.Tag)}");
        AddAuth(request);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, $"pulling {reference}", cancellationToken);

        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellationToken));
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Contains("\"error\"", StringComparison.Ordinal) && JsonNode.Parse(line)?["error"]?.GetValue<string>() is { } error)
            {
                throw new InvalidOperationException($"pulling {reference} failed: {error}");
            }
        }
    }

    /// <summary>Gives an image (by id) another name.</summary>
    public Task TagAsync(string imageId, string repository, string tag, CancellationToken cancellationToken) =>
        PostAsync($"images/{Uri.EscapeDataString(imageId)}/tag?repo={Uri.EscapeDataString(repository)}&tag={Uri.EscapeDataString(tag)}",
            $"tagging {imageId} as {repository}:{tag}", cancellationToken);

    public async Task<string> CreateContainerAsync(string name, JsonObject body, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsync($"containers/create?name={Uri.EscapeDataString(name)}",
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), cancellationToken);
        await EnsureSuccessAsync(response, $"creating container {name}", cancellationToken);
        var created = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return created?["Id"]?.GetValue<string>() ?? throw new InvalidOperationException("the engine returned no container id");
    }

    public Task StartAsync(string id, CancellationToken cancellationToken) =>
        PostAsync($"containers/{Uri.EscapeDataString(id)}/start", $"starting {id}", cancellationToken, okStatuses: HttpStatusCode.NotModified);

    public Task StopAsync(string id, int timeoutSeconds, CancellationToken cancellationToken) =>
        PostAsync($"containers/{Uri.EscapeDataString(id)}/stop?t={timeoutSeconds}", $"stopping {id}", cancellationToken, okStatuses: HttpStatusCode.NotModified);

    public Task RenameAsync(string id, string newName, CancellationToken cancellationToken) =>
        PostAsync($"containers/{Uri.EscapeDataString(id)}/rename?name={Uri.EscapeDataString(newName)}", $"renaming {id}", cancellationToken);

    public async Task RemoveAsync(string id, bool force, CancellationToken cancellationToken)
    {
        using var response = await _http.DeleteAsync($"containers/{Uri.EscapeDataString(id)}?force={(force ? "true" : "false")}", cancellationToken);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            await EnsureSuccessAsync(response, $"removing {id}", cancellationToken);
        }
    }

    public async Task ConnectNetworkAsync(string network, string containerId, JsonNode? endpointConfig, CancellationToken cancellationToken)
    {
        var body = new JsonObject { ["Container"] = containerId };
        if (endpointConfig is not null)
        {
            body["EndpointConfig"] = endpointConfig.DeepClone();
        }

        using var response = await _http.PostAsync($"networks/{Uri.EscapeDataString(network)}/connect",
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), cancellationToken);
        await EnsureSuccessAsync(response, $"connecting {containerId} to {network}", cancellationToken);
    }

    /// <summary>All containers (running or not) carrying the label, with their inspect-lite summary.</summary>
    public async Task<JsonArray> ListContainersByLabelAsync(string label, CancellationToken cancellationToken)
    {
        var filters = Uri.EscapeDataString(JsonSerializer.Serialize(new Dictionary<string, string[]> { ["label"] = [label] }));
        using var response = await _http.GetAsync($"containers/json?all=true&filters={filters}", cancellationToken);
        await EnsureSuccessAsync(response, "listing containers", cancellationToken);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken)) as JsonArray ?? [];
    }

    /// <summary>The container's output, de-multiplexed (a container without a TTY frames stdout/stderr in 8-byte headers).</summary>
    public async Task<string> ReadLogsAsync(string id, int tail, CancellationToken cancellationToken)
    {
        var bytes = await _http.GetByteArrayAsync($"containers/{Uri.EscapeDataString(id)}/logs?stdout=true&stderr=true&tail={tail}", cancellationToken);
        return DemultiplexLogs(bytes);
    }

    internal static string DemultiplexLogs(byte[] bytes)
    {
        // Frame = [stream, 0, 0, 0, size (big-endian uint32)] + payload. Anything that does not look like a frame is
        // raw TTY output.
        var output = new StringBuilder();
        var offset = 0;
        while (offset + 8 <= bytes.Length && bytes[offset] <= 2 && bytes[offset + 1] == 0 && bytes[offset + 2] == 0 && bytes[offset + 3] == 0)
        {
            var size = (bytes[offset + 4] << 24) | (bytes[offset + 5] << 16) | (bytes[offset + 6] << 8) | bytes[offset + 7];
            if (size < 0 || offset + 8 + size > bytes.Length)
            {
                break;
            }

            output.Append(Encoding.UTF8.GetString(bytes, offset + 8, size));
            offset += 8 + size;
        }

        if (offset < bytes.Length)
        {
            output.Append(Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset));
        }

        return output.ToString();
    }

    private async Task<JsonObject?> GetObjectAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(path, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, $"GET {path}", cancellationToken);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken)) as JsonObject;
    }

    private async Task PostAsync(string path, string what, CancellationToken cancellationToken, params HttpStatusCode[] okStatuses)
    {
        using var response = await _http.PostAsync(path, content: null, cancellationToken);
        if (okStatuses.Contains(response.StatusCode))
        {
            return;
        }

        await EnsureSuccessAsync(response, what, cancellationToken);
    }

    private void AddAuth(HttpRequestMessage request)
    {
        if (_registryAuth is not null)
        {
            request.Headers.Add("X-Registry-Auth", _registryAuth);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string what, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        // The engine answers {"message": "..."} - that sentence is what an admin needs to read.
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        string? message = null;
        try
        {
            message = JsonNode.Parse(text)?["message"]?.GetValue<string>();
        }
        catch (JsonException)
        {
        }

        throw new InvalidOperationException($"{what}: {(int)response.StatusCode} {message ?? text.Trim()}");
    }

    // An image reference goes into the PATH of images/{name}/json and distribution/{name}/json; its slashes stay.
    private static string EscapeReference(string reference) =>
        string.Join('/', reference.Split('/').Select(Uri.EscapeDataString));

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// An image reference split the way the engine wants it: <c>ghcr.io/real-ttx/matmon:nightly</c> =
/// repository <c>ghcr.io/real-ttx/matmon</c> + tag <c>nightly</c>. A colon only starts the tag after the last slash
/// (<c>localhost:5000/matmon</c> has none), no tag means <c>latest</c>, and a reference pinned by digest
/// (<c>…@sha256:…</c>) has nothing to follow.
/// </summary>
public sealed record ImageReference(string Repository, string Tag, bool PinnedByDigest)
{
    public static ImageReference Parse(string reference)
    {
        var text = reference.Trim();
        var at = text.IndexOf('@');
        if (at >= 0)
        {
            return new ImageReference(text[..at], text[(at + 1)..], PinnedByDigest: true);
        }

        var colon = text.LastIndexOf(':');
        return colon > text.LastIndexOf('/')
            ? new ImageReference(text[..colon], text[(colon + 1)..], false)
            : new ImageReference(text, "latest", false);
    }

    public override string ToString() => PinnedByDigest ? $"{Repository}@{Tag}" : $"{Repository}:{Tag}";
}
