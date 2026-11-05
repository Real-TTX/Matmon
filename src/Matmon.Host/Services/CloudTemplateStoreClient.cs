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
        string[] SensorTypes, int Downloads, DateTimeOffset Updated);

    public sealed record StorePage(IReadOnlyList<StoreItem> Items, int Total, int Page, int PageSize, string[] AllTags);

    public sealed record StoreTemplateItem(string Name, string SensorType, string Scope);

    public sealed record StoreVersion(string Version, string Notes, DateTimeOffset Date);

    public sealed record StoreDetail(string Slug, string Title, string Summary, string Description, string[] Tags, string Author, string Version,
        int TemplateCount, string[] SensorTypes, int Downloads, DateTimeOffset Updated, IReadOnlyList<StoreTemplateItem> Templates,
        IReadOnlyList<StoreVersion> Versions);

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

    /// <summary>The file of a published entry (the cloud counts it as an install). Null when the entry is gone.</summary>
    public async Task<byte[]?> FileAsync(string slug, CancellationToken cancellationToken)
    {
        using var response = await SendAsync("/store/templates/" + Uri.EscapeDataString(slug) + "/file", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        Ensure(response);
        if (response.Content.Headers.ContentLength > TemplateTransfer.MaxFileBytes)
        {
            throw new StoreUnavailableException("The template file from the store is larger than 2 MB.");
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return bytes.Length > TemplateTransfer.MaxFileBytes
            ? throw new StoreUnavailableException("The template file from the store is larger than 2 MB.")
            : bytes;
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
