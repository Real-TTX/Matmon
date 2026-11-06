using System.Collections.Concurrent;

namespace Matmon.Host.Services;

/// <summary>A request of the cloud's Templates > Submit page, sent with a heartbeat answer (<c>exportTemplates</c>):
/// "send me your templates", so someone can pick sets to submit to the store without downloading and uploading a file.</summary>
public sealed record CloudTemplateExportRequest(Guid Id);

/// <summary>
/// Answers <see cref="CloudTemplateExportRequest"/>: exports EVERY template of this instance exactly like Templates > Export all
/// (<see cref="TemplateTransfer.Export"/> - no secrets, no credential values, no store origin) and uploads the file to
/// <c>POST /api/instances/{id}/templates/exports/{requestId}</c>. The cloud cannot reach the instance, so this rides the
/// heartbeat like the partner reports; the cloud cleans the file again before anything is stored. A request already
/// handled in the last minutes is not answered twice; one the cloud repeats after a failed upload is.
/// </summary>
public sealed class CloudTemplateExportRelay(IMonitoringWorkspaceStore store, ILogger<CloudTemplateExportRelay> logger)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(1) };
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _handled = new();

    public void Handle(IReadOnlyList<CloudTemplateExportRequest>? requests, string baseUrl, string instanceId, string token)
    {
        if (requests is not { Count: > 0 })
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var stale in _handled.Where(entry => now - entry.Value > TimeSpan.FromMinutes(5)).Select(entry => entry.Key).ToArray())
        {
            _handled.TryRemove(stale, out _);
        }

        foreach (var request in requests.Take(5))
        {
            if (_handled.TryAdd(request.Id, now))
            {
                _ = Task.Run(() => UploadAsync(request, baseUrl, instanceId, token));
            }
        }
    }

    /// <summary>The file this instance sends: all templates, cleaned like an export.</summary>
    public byte[] BuildExport(DateTimeOffset now)
    {
        var all = store.GetAllTemplates();
        var templates = TemplateTransfer.WithParents(all, all.Select(template => template.Id));
        return TemplateTransfer.Export(templates, store.GetSecretParameterKeys(), $"Matmon {Matmon.Probe.MatmonVersion.Current}", now);
    }

    private async Task UploadAsync(CloudTemplateExportRequest request, string baseUrl, string instanceId, string token)
    {
        try
        {
            using var content = new ByteArrayContent(BuildExport(DateTimeOffset.UtcNow));
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            using var message = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/api/instances/{instanceId}/templates/exports/{request.Id}")
            {
                Content = content
            };
            message.Headers.TryAddWithoutValidation("X-Matmon-Instance-Token", token);
            using var response = await Http.SendAsync(message);
            if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // 404 = the cloud no longer waits for it (the page was left, or it expired).
                logger.LogInformation("Templates sent to Matmon.Cloud for request {Request} ({Status})", request.Id, (int)response.StatusCode);
            }
            else
            {
                logger.LogWarning("Templates for request {Request}: Matmon.Cloud answered {Status}", request.Id, (int)response.StatusCode);
                _handled.TryRemove(request.Id, out _);
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Templates for request {Request} could not be sent to Matmon.Cloud", request.Id);
            _handled.TryRemove(request.Id, out _);
        }
    }
}
