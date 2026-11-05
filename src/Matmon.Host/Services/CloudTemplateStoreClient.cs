using System.Net;
using System.Net.Http.Json;

namespace Matmon.Host.Services;

/// <summary>
/// Reads the template store of the connected Matmon.Cloud (<c>GET /api/instances/{id}/store/templates[/{slug}[/file]]</c>, instance
/// token). The store lives on the cloud - the instance reaches nothing but its cloud, so the catalogue and the files come from
/// there and nowhere else. Transport only: the page hands a fetched file to <see cref="TemplateTransfer.Read"/> and the store's
/// import, exactly like an uploaded file (it is checked and stripped again here; the cloud's cleaning is not trusted alone).
/// </summary>
public sealed class CloudTemplateStoreClient
{
    // The HTTP client is replaceable for the tests (a handler standing in for the cloud).
    internal static Func<HttpMessageHandler>? HandlerFactory;
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly IMonitoringWorkspaceStore _store;
    private readonly MatmonRuntimeOptions _runtimeOptions;

    public CloudTemplateStoreClient(IMonitoringWorkspaceStore store, MatmonRuntimeOptions runtimeOptions)
    {
        _store = store;
        _runtimeOptions = runtimeOptions;
    }

    public sealed record StoreItem(string Slug, string Title, string Summary, string[] Tags, string Author, string Version, int TemplateCount,
        string[] SensorTypes, int Downloads, DateTimeOffset Updated, bool HasScripts = false);

    /// <summary>A script inside a set's live file, as the cloud lists it before anybody adds the set.</summary>
    public sealed record StoreScript(string Template, string SensorType, string Parameter, string Content);

    /// <summary>A set's file and the version it is (null from an older cloud).</summary>
    public sealed record StoreFile(byte[] Bytes, string? Version);

    /// <summary>The live version of a set this instance added.</summary>
    public sealed record StoreLiveVersion(string Slug, string Version, string Title);

    public sealed record StorePage(IReadOnlyList<StoreItem> Items, int Total, int Page, int PageSize, string[] AllTags);

    public sealed record StoreTemplateItem(string Name, string SensorType, string Scope);

    public sealed record StoreVersion(string Version, string Notes, DateTimeOffset Date);

    public sealed record StoreDetail(string Slug, string Title, string Summary, string Description, string[] Tags, string Author, string Version,
        int TemplateCount, string[] SensorTypes, int Downloads, DateTimeOffset Updated, IReadOnlyList<StoreTemplateItem> Templates,
        IReadOnlyList<StoreVersion> Versions, IReadOnlyList<StoreScript>? Scripts = null);

    /// <summary>Why the store could not be read, in words for the page.</summary>
    public sealed class StoreUnavailableException(string message) : Exception(message);

    public bool IsConnected => Resolve().Url is not null;

    // The UI-managed link wins once configured, else the environment bootstrap - INCLUDING its token: an environment link is an
    // identity (MatmonRuntimeOptions.HasCloudBootstrapLink), and reading the store is harmless, so it needs no "Keep this link" first.
    private (string? Url, string? InstanceId, string? Token) Resolve()
    {
        var settings = _store.GetCloudConnectionSettings();
        var token = settings.Configured
            ? _store.GetCloudConnectionToken()
            : _store.GetCloudConnectionToken() is { Length: > 0 } stored ? stored : _runtimeOptions.CloudInstanceToken;
        var url = (settings.Configured ? settings.Url : _runtimeOptions.CloudUrl)?.Trim().TrimEnd('/');
        var instanceId = settings.Configured ? settings.InstanceId : _runtimeOptions.CloudInstanceId;
        return string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(instanceId) || string.IsNullOrWhiteSpace(token)
            ? (null, null, null)
            : (url, instanceId.Trim(), token);
    }

    public Task<StorePage> ListAsync(string? query, string? tag, string? sort, int page, CancellationToken cancellationToken)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(query)) parts.Add("q=" + Uri.EscapeDataString(query.Trim()));
        if (!string.IsNullOrWhiteSpace(tag)) parts.Add("tag=" + Uri.EscapeDataString(tag.Trim()));
        if (!string.IsNullOrWhiteSpace(sort)) parts.Add("sort=" + Uri.EscapeDataString(sort.Trim()));
        if (page > 1) parts.Add("page=" + page);
        return GetJsonAsync<StorePage>("/store/templates" + (parts.Count == 0 ? string.Empty : "?" + string.Join("&", parts)), cancellationToken)!;
    }

    public async Task<StoreDetail?> GetAsync(string slug, CancellationToken cancellationToken) =>
        await GetJsonAsync<StoreDetail>("/store/templates/" + Uri.EscapeDataString(slug), cancellationToken, allowNotFound: true);

    // The live versions of the added sets, asked at most every 30 minutes (the Templates page shows them on every visit).
    private readonly object _versionsGate = new();
    private (string Key, DateTimeOffset At, IReadOnlyList<StoreLiveVersion> Versions)? _versionsCache;
    private static readonly TimeSpan VersionsCacheFor = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The live store versions of <paramref name="slugs"/> (sets this instance added). Cached 30 minutes; null when the store cannot
    /// be asked right now (not connected, unreachable, an older cloud) - a hint that simply does not show, never an error page.
    /// </summary>
    public async Task<IReadOnlyList<StoreLiveVersion>?> LiveVersionsAsync(IReadOnlyCollection<string> slugs, CancellationToken cancellationToken)
    {
        var wanted = slugs.Select(slug => slug.Trim().ToLowerInvariant()).Where(slug => slug.Length > 0).Distinct().Order().ToList();
        if (wanted.Count == 0 || !IsConnected)
        {
            return wanted.Count == 0 ? [] : null;
        }

        var key = string.Join(",", wanted);
        lock (_versionsGate)
        {
            if (_versionsCache is { } cached && cached.Key == key && DateTimeOffset.UtcNow - cached.At < VersionsCacheFor)
            {
                return cached.Versions;
            }
        }

        try
        {
            var versions = await GetJsonAsync<List<StoreLiveVersion>>(
                "/store/versions?" + string.Join("&", wanted.Select(slug => "slug=" + Uri.EscapeDataString(slug))), cancellationToken);
            lock (_versionsGate)
            {
                _versionsCache = (key, DateTimeOffset.UtcNow, versions ?? []);
            }

            return versions;
        }
        catch (StoreUnavailableException)
        {
            return null;
        }
    }

    /// <summary>Forgets the cached live versions (after adding or updating a set).</summary>
    public void ForgetVersions()
    {
        lock (_versionsGate)
        {
            _versionsCache = null;
        }
    }

    /// <summary>The file of a published entry (the cloud counts it as an install). Null when the entry is gone.</summary>
    public async Task<StoreFile?> FileAsync(string slug, CancellationToken cancellationToken)
    {
        using var response = await SendAsync("/store/templates/" + Uri.EscapeDataString(slug) + "/file", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        var version = response.Headers.TryGetValues("X-Matmon-Store-Version", out var values) ? values.FirstOrDefault() : null;

        Ensure(response);
        if (response.Content.Headers.ContentLength > TemplateTransfer.MaxFileBytes)
        {
            throw new StoreUnavailableException("The template file from the store is larger than 2 MB.");
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return bytes.Length > TemplateTransfer.MaxFileBytes
            ? throw new StoreUnavailableException("The template file from the store is larger than 2 MB.")
            : new StoreFile(bytes, string.IsNullOrWhiteSpace(version) ? null : version.Trim());
    }

    private async Task<T?> GetJsonAsync<T>(string path, CancellationToken cancellationToken, bool allowNotFound = false) where T : class
    {
        using var response = await SendAsync(path, cancellationToken);
        if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        Ensure(response);
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(cancellationToken)
                   ?? throw new StoreUnavailableException("The cloud sent an empty answer.");
        }
        catch (System.Text.Json.JsonException)
        {
            throw new StoreUnavailableException("The cloud's answer could not be read - it may not have a template store yet.");
        }
    }

    private async Task<HttpResponseMessage> SendAsync(string path, CancellationToken cancellationToken)
    {
        var (url, instanceId, token) = Resolve();
        if (url is null)
        {
            throw new StoreUnavailableException("This instance is not connected to Matmon Cloud. Connect it under System > Cloud to use the template store.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{url}/api/instances/{instanceId}{path}");
        request.Headers.TryAddWithoutValidation("X-Matmon-Instance-Token", token);
        try
        {
            var http = HandlerFactory is { } factory ? new HttpClient(factory(), disposeHandler: true) { Timeout = TimeSpan.FromSeconds(20) } : SharedHttp;
            return await http.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new StoreUnavailableException("Matmon Cloud could not be reached. Try again in a moment.");
        }
    }

    private static void Ensure(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        throw response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new StoreUnavailableException("Matmon Cloud did not accept this instance's link. Check System > Cloud."),
            HttpStatusCode.NotFound => new StoreUnavailableException("This Matmon Cloud has no template store yet."),
            _ => new StoreUnavailableException($"Matmon Cloud answered {(int)response.StatusCode}. Try again in a moment.")
        };
    }
}
