using System.Net.Http.Json;

namespace Matmon.Host.Services;

/// <summary>Talks to Matmon.Cloud's backup endpoints - both the per-instance set (<c>/backups</c>) and the
/// account-scoped set (<c>/account-backups</c>, sibling instances of the same owner, for cross-instance DR).
/// Centralizes the HTTP the Config Backup tab, the <see cref="BackupSchedulerService"/> and the setup wizard all
/// need so the cloud-link resolution + token header live in one place. Transport only: callers build the backup
/// bytes via the store (<c>CreateBackupBytes</c>) and apply a downloaded blob via <c>RestoreBackupBytes</c>.</summary>
public sealed class CloudBackupClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly IMonitoringWorkspaceStore _store;
    private readonly MatmonRuntimeOptions _runtimeOptions;

    public CloudBackupClient(IMonitoringWorkspaceStore store, MatmonRuntimeOptions runtimeOptions)
    {
        _store = store;
        _runtimeOptions = runtimeOptions;
    }

    public sealed record CloudBackupItem(Guid Id, DateTimeOffset CreatedUtc, string Label, string Version, long SizeBytes);

    public sealed record CloudAccountBackupItem(Guid Id, Guid InstanceId, string InstanceName, DateTimeOffset CreatedUtc, string Label, string Version, long SizeBytes);

    /// <summary>True when this instance is linked to a cloud (url + instance id + token all present).</summary>
    public bool IsConnected
    {
        get
        {
            var (url, instanceId, token) = Resolve();
            return url is not null && instanceId is not null && token is not null;
        }
    }

    // Same precedence as ConfigModel.ResolveCloud: the UI-managed link wins once configured, else env bootstrap.
    private (string? Url, string? InstanceId, string? Token) Resolve()
    {
        var settings = _store.GetCloudConnectionSettings();
        var token = _store.GetCloudConnectionToken();
        var url = (settings.Configured ? settings.Url : _runtimeOptions.CloudUrl)?.Trim().TrimEnd('/');
        var instanceId = settings.Configured ? settings.InstanceId : _runtimeOptions.CloudInstanceId;
        return string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(instanceId) || string.IsNullOrWhiteSpace(token)
            ? (null, null, null)
            : (url, instanceId, token);
    }

    private HttpRequestMessage BuildRequest(HttpMethod method, string url, string instanceId, string token, string path, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, $"{url}/api/instances/{instanceId}{path}");
        request.Headers.TryAddWithoutValidation("X-Matmon-Instance-Token", token);
        if (content is not null)
        {
            request.Content = content;
        }

        return request;
    }

    /// <summary>Push a config snapshot (already-built bytes) to the cloud. Throws if not connected or the cloud
    /// rejects it - callers record the failure. Returns the new backup id.</summary>
    public async Task<Guid> PushAsync(byte[] bytes, string label, CancellationToken cancellationToken)
    {
        var (url, instanceId, token) = Resolve();
        if (url is null || instanceId is null || token is null)
        {
            throw new InvalidOperationException("Not connected to Matmon.Cloud.");
        }

        using var request = BuildRequest(HttpMethod.Post, url, instanceId, token, $"/backups?label={Uri.EscapeDataString(label)}", new ByteArrayContent(bytes));
        using var response = await Http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<PushResult>(cancellationToken);
        return result?.Id ?? Guid.Empty;
    }

    private sealed record PushResult(Guid Id);

    /// <summary>This instance's own cloud backups. Null = offline / not connected (distinguishes "empty" from
    /// "couldn't reach the cloud"); a non-null (possibly empty) list = a successful fetch.</summary>
    public Task<IReadOnlyList<CloudBackupItem>?> ListAsync(CancellationToken cancellationToken) =>
        ListCoreAsync<CloudBackupItem>("/backups", cancellationToken);

    /// <summary>Every backup on this instance's cloud ACCOUNT (its own + sibling instances of the same owner) -
    /// the pool a fresh instance restores from during setup. Null = offline / not connected.</summary>
    public Task<IReadOnlyList<CloudAccountBackupItem>?> ListAccountAsync(CancellationToken cancellationToken) =>
        ListCoreAsync<CloudAccountBackupItem>("/account-backups", cancellationToken);

    private async Task<IReadOnlyList<T>?> ListCoreAsync<T>(string path, CancellationToken cancellationToken)
    {
        var (url, instanceId, token) = Resolve();
        if (url is null || instanceId is null || token is null)
        {
            return null;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(6));
            using var request = BuildRequest(HttpMethod.Get, url, instanceId, token, path);
            using var response = await Http.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<List<T>>(cts.Token) ?? [];
        }
        catch
        {
            return null; // best-effort: leave it to the caller to show an offline state
        }
    }

    /// <summary>Download one of this instance's own backups by id. Null on failure / not connected.</summary>
    public Task<byte[]?> DownloadAsync(Guid backupId, CancellationToken cancellationToken) =>
        DownloadCoreAsync($"/backups/{backupId}", cancellationToken);

    /// <summary>Download an account-scoped backup (sibling instance of the same owner) by id. Null on failure.</summary>
    public Task<byte[]?> DownloadAccountAsync(Guid backupId, CancellationToken cancellationToken) =>
        DownloadCoreAsync($"/account-backups/{backupId}", cancellationToken);

    private async Task<byte[]?> DownloadCoreAsync(string path, CancellationToken cancellationToken)
    {
        var (url, instanceId, token) = Resolve();
        if (url is null || instanceId is null || token is null)
        {
            return null;
        }

        using var request = BuildRequest(HttpMethod.Get, url, instanceId, token, path);
        using var response = await Http.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadAsByteArrayAsync(cancellationToken)
            : null;
    }

    /// <summary>Delete one of this instance's own cloud backups. True on success.</summary>
    public async Task<bool> DeleteAsync(Guid backupId, CancellationToken cancellationToken)
    {
        var (url, instanceId, token) = Resolve();
        if (url is null || instanceId is null || token is null)
        {
            return false;
        }

        using var request = BuildRequest(HttpMethod.Delete, url, instanceId, token, $"/backups/{backupId}");
        using var response = await Http.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode;
    }
}
