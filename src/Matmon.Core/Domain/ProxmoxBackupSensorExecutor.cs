using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Matmon.Core.Domain;

/// <summary>
/// A Proxmox Backup Server through its REST API (<c>https://&lt;host&gt;:8007/api2/json</c>) with an API token: how full
/// every datastore is (and when PBS expects it to be full), how long ago the newest backup arrived and how long the
/// most neglected backup group has gone without one, the backup / verify / sync / prune / GC tasks that failed in the last
/// 24 hours, and the server's own CPU, memory and root disk.
/// <para>The token needs <c>Datastore.Audit</c> and <c>Sys.Audit</c> on <c>/</c> (PBS: Configuration → Access Control →
/// Permissions, role "Audit") - a token separated from its user has no rights of its own, and without them PBS simply
/// lists no datastore, which this sensor says in words.</para>
/// </summary>
public sealed class ProxmoxBackupSensorExecutor : ISensorExecutor
{
    public const int DefaultPort = 8007;
    public const string DatastoreUsedChannel = "datastoreUsedPercent";
    public const string DaysUntilFullChannel = "daysUntilFull";
    public const string NewestBackupAgeChannel = "newestBackupAgeHours";
    public const string OldestGroupAgeChannel = "oldestGroupAgeHours";
    public const string FailedTasksChannel = "failedTasks24h";
    public const string WarningTasksChannel = "warningTasks24h";

    private readonly Func<bool, HttpMessageHandler> _handlerFactory;

    public ProxmoxBackupSensorExecutor()
        : this(DefaultHandler)
    {
    }

    /// <summary>For tests: the handler every request goes through (the flag is "verify the certificate").</summary>
    public ProxmoxBackupSensorExecutor(Func<bool, HttpMessageHandler> handlerFactory) => _handlerFactory = handlerFactory;

    public static SensorDefinition Definition { get; } = new()
    {
        Key = "proxmox-backup",
        DisplayName = "Proxmox Backup Server",
        Description = "A Proxmox Backup Server via its API token: datastore usage and days until full, age of the newest backup and of the most neglected backup group, failed tasks in the last 24 hours, CPU / memory / root disk.",
        ChannelMode = SensorChannelMode.Dynamic,
        Parameters =
        [
            new SensorParameterDefinition
            {
                Key = "pve.user",
                Label = "API user (optional)",
                Kind = SensorParameterKind.Text,
                Description = "Only needed if the Token ID is just the token name rather than the full user@realm!name.",
                Placeholder = "root@pam",
                CredentialKind = MonitoringCredentialKind.Proxmox
            },
            new SensorParameterDefinition
            {
                Key = "pve.tokenId",
                Label = "Token ID",
                Kind = SensorParameterKind.Text,
                Description = "The API token as PBS shows it, e.g. monitor@pbs!matmon. Give the token the Audit role on / (Configuration → Access Control → Permissions → Add → API Token Permission) - without it PBS lists no datastore.",
                Required = true,
                Placeholder = "monitor@pbs!matmon",
                CredentialKind = MonitoringCredentialKind.Proxmox
            },
            new SensorParameterDefinition
            {
                Key = "pve.tokenSecret",
                Label = "Token secret",
                Kind = SensorParameterKind.Secret,
                Description = "The token's secret (shown once when the token is created).",
                Required = true,
                CredentialKind = MonitoringCredentialKind.Proxmox
            },
            new SensorParameterDefinition
            {
                Key = "pbs.port",
                Label = "Port",
                Group = "Connection",
                Kind = SensorParameterKind.Integer,
                Description = "The PBS API port.",
                DefaultValue = "8007",
                Min = 1,
                Max = 65535,
                Step = "1"
            },
            new SensorParameterDefinition
            {
                Key = "pbs.verifySsl",
                Label = "Verify SSL",
                Group = "Connection",
                Kind = SensorParameterKind.Boolean,
                Description = "Validate the server's certificate (PBS ships a self-signed one).",
                DefaultValue = "false"
            }
        ]
    };

    public string SensorTypeKey => Definition.Key;

    public ValueTask<SensorDiscoveryCheckResult> DiscoverAsync(SensorDiscoveryContext context, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (!context.OpenTcpPorts.Contains(DefaultPort))
        {
            return ValueTask.FromResult(SensorDiscoveryCheckResult.NotAvailable);
        }

        var settings = new MonitoringSettings();
        settings.Parameters["pbs.port"] = DefaultPort.ToString(CultureInfo.InvariantCulture);
        settings.Parameters["pbs.verifySsl"] = "false";
        return ValueTask.FromResult(SensorDiscoveryCheckResult.Available(
            new SensorDiscoverySuggestion(
                Definition.Key,
                "Proxmox Backup Server",
                context.Host,
                settings,
                "Port 8007 is open - a Proxmox Backup Server answers here. It needs an API token with the Audit role.",
                84)));
    }

    public async ValueTask<SensorExecutionResult> ExecuteAsync(SensorExecutionContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.Target))
        {
            return SensorExecutionResult.Critical(TimeSpan.Zero, "The backup server's address is missing (the host's address, or a target).");
        }

        MonitoringSettings.TryReadParameter(context.Settings, "pve.user", out var user);
        MonitoringSettings.TryReadParameter(context.Settings, "pve.tokenId", out var tokenId);
        MonitoringSettings.TryReadParameter(context.Settings, "pve.tokenSecret", out var tokenSecret);
        if (BuildAuthorizationHeader(user ?? string.Empty, tokenId ?? string.Empty, tokenSecret ?? string.Empty) is not { } authorization)
        {
            return SensorExecutionResult.Critical(TimeSpan.Zero, "An API token is needed: the Token ID as user@realm!name and its secret.");
        }

        var baseUri = ResolveBaseUri(context);
        if (baseUri is null)
        {
            return SensorExecutionResult.Critical(TimeSpan.Zero, $"'{context.Target}' is not an address.");
        }

        var verifySsl = MonitoringSettings.TryReadParameterBool(context.Settings, "pbs.verifySsl", out var verify) && verify;
        var timeout = context.Settings.Timeout ?? TimeSpan.FromSeconds(20);
        var watch = Stopwatch.StartNew();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        using var client = new HttpClient(_handlerFactory(verifySsl), disposeHandler: true) { BaseAddress = baseUri, Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authorization);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var status = await GetDataAsync(client, "nodes/localhost/status", required: false, timeoutCts.Token);
            var usage = await GetDataAsync(client, "status/datastore-usage", required: true, timeoutCts.Token);
            var stores = new List<PbsDatastore>();
            foreach (var entry in Items(usage))
            {
                var name = Text(entry, "store") ?? "datastore";
                var groups = Text(entry, "error") is null
                    ? Items(await GetDataAsync(client, $"admin/datastore/{Uri.EscapeDataString(name)}/groups", required: false, timeoutCts.Token))
                        .Select(group => Number(group, "last-backup")).OfType<double>().Select(epoch => DateTimeOffset.FromUnixTimeSeconds((long)epoch)).ToList()
                    : [];
                stores.Add(new PbsDatastore(name, Number(entry, "total"), Number(entry, "used"), Number(entry, "avail"),
                    Number(entry, "estimated-full-date") is { } full && full > 0 ? DateTimeOffset.FromUnixTimeSeconds((long)full) : null,
                    Text(entry, "error"), groups));
            }

            var since = now.AddHours(-24).ToUnixTimeSeconds();
            var tasks = Items(await GetDataAsync(client, $"nodes/localhost/tasks?errors=true&since={since}&limit=500", required: false, timeoutCts.Token))
                .Select(task => new PbsTask(Text(task, "worker_type") ?? "task", Text(task, "worker_id"), Text(task, "status")))
                .ToList();
            var node = new PbsNode(
                Number(status, "cpu") is { } cpu ? cpu * 100 : null,
                UsedPercent(Child(status, "memory"), "total", "used"),
                UsedPercent(Child(status, "root"), "total", "used"));
            watch.Stop();
            return SensorThresholdEvaluator.ApplyChannelThresholds(context.Settings, BuildResult(stores, tasks, node, now, watch.Elapsed));
        }
        catch (PbsException exception)
        {
            return SensorExecutionResult.Critical(watch.Elapsed, exception.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return SensorExecutionResult.Critical(watch.Elapsed, $"The backup server at {baseUri.Host} did not answer within {timeout.TotalSeconds:0} s.");
        }
        catch (HttpRequestException exception)
        {
            return SensorExecutionResult.Critical(watch.Elapsed, $"The backup server at {baseUri.Host} cannot be reached: {exception.Message}");
        }
        catch (JsonException)
        {
            return SensorExecutionResult.Critical(watch.Elapsed, $"{baseUri.Host}:{baseUri.Port} does not answer like a Proxmox Backup Server.");
        }
    }

    // ---- the result (pure) -----------------------------------------------------------------------------------------------

    public sealed record PbsDatastore(string Name, double? Total, double? Used, double? Available, DateTimeOffset? EstimatedFull, string? Error, IReadOnlyList<DateTimeOffset> GroupLastBackups);

    public sealed record PbsTask(string Type, string? Id, string? Status);

    public sealed record PbsNode(double? CpuPercent, double? MemoryUsedPercent, double? RootUsedPercent);

    public static SensorExecutionResult BuildResult(IReadOnlyList<PbsDatastore> stores, IReadOnlyList<PbsTask> tasks, PbsNode node, DateTimeOffset now, TimeSpan duration)
    {
        if (stores.Count == 0)
        {
            return SensorExecutionResult.Critical(duration, "The server lists no datastore - either there is none, or the API token lacks the Audit role on / (a token separated from its user has no rights of its own).");
        }

        var channels = new List<SensorChannelValue>();
        var readable = stores.Where(store => store.Error is null && store.Total > 0).ToList();
        if (readable.Count > 0)
        {
            channels.Add(new SensorChannelValue
            {
                Key = DatastoreUsedChannel, Label = "Fullest datastore", Value = Math.Round(readable.Max(store => store.Used!.Value / store.Total!.Value * 100), 1),
                Unit = "%", MeasurementKind = SensorMeasurementKind.Percent
            });
        }

        var fullDays = readable.Where(store => store.EstimatedFull is not null).Select(store => (store.EstimatedFull!.Value - now).TotalDays).ToList();
        if (fullDays.Count > 0)
        {
            channels.Add(new SensorChannelValue
            {
                Key = DaysUntilFullChannel, Label = "Days until a datastore is full (PBS estimate)", Value = Math.Round(Math.Max(0, fullDays.Min()), 1),
                Unit = "d", MeasurementKind = SensorMeasurementKind.Count
            });
        }

        var lastBackups = stores.SelectMany(store => store.GroupLastBackups).ToList();
        if (lastBackups.Count > 0)
        {
            channels.Add(new SensorChannelValue
            {
                Key = NewestBackupAgeChannel, Label = "Newest backup", Value = Hours(now - lastBackups.Max()), Unit = "h", MeasurementKind = SensorMeasurementKind.Count
            });
            channels.Add(new SensorChannelValue
            {
                Key = OldestGroupAgeChannel, Label = "Most neglected backup group", Value = Hours(now - lastBackups.Min()), Unit = "h", MeasurementKind = SensorMeasurementKind.Count
            });
            channels.Add(new SensorChannelValue
            {
                Key = "backupGroups", Label = "Backup groups", Value = lastBackups.Count, MeasurementKind = SensorMeasurementKind.Count, LogByDefault = false
            });
        }

        var finished = tasks.Where(task => !string.IsNullOrWhiteSpace(task.Status) && !task.Status.Equals("OK", StringComparison.OrdinalIgnoreCase)).ToList();
        var warnings = finished.Where(task => task.Status!.StartsWith("WARNINGS", StringComparison.OrdinalIgnoreCase)).ToList();
        var failures = finished.Except(warnings).ToList();
        channels.Add(new SensorChannelValue { Key = FailedTasksChannel, Label = "Failed tasks (24 h)", Value = failures.Count, MeasurementKind = SensorMeasurementKind.Count });
        channels.Add(new SensorChannelValue { Key = WarningTasksChannel, Label = "Tasks with warnings (24 h)", Value = warnings.Count, MeasurementKind = SensorMeasurementKind.Count, LogByDefault = false });

        if (node.CpuPercent is { } cpu)
        {
            channels.Add(new SensorChannelValue { Key = "cpu", Label = "CPU", Value = Math.Round(cpu, 1), Unit = "%", MeasurementKind = SensorMeasurementKind.Percent });
        }

        if (node.MemoryUsedPercent is { } memory)
        {
            channels.Add(new SensorChannelValue { Key = "memoryUsedPercent", Label = "Memory used", Value = memory, Unit = "%", MeasurementKind = SensorMeasurementKind.Percent });
        }

        if (node.RootUsedPercent is { } root)
        {
            channels.Add(new SensorChannelValue { Key = "diskUsedPercent", Label = "Root disk used", Value = root, Unit = "%", MeasurementKind = SensorMeasurementKind.Percent });
        }

        var used = new HashSet<string>(channels.Select(channel => channel.Key), StringComparer.Ordinal);
        foreach (var store in readable)
        {
            var key = "store." + RedfishSensorExecutor.Slug(store.Name);
            if (!used.Add(key + ".usedPercent"))
            {
                continue;
            }

            channels.Add(new SensorChannelValue
            {
                Key = key + ".usedPercent", Label = $"{store.Name} used", Value = Math.Round(store.Used!.Value / store.Total!.Value * 100, 1),
                Unit = "%", MeasurementKind = SensorMeasurementKind.Percent
            });
            if (store.Available is { } available)
            {
                channels.Add(new SensorChannelValue { Key = key + ".freeGb", Label = $"{store.Name} free", Value = Math.Round(available / 1_000_000_000d, 1), Unit = "GB" });
            }
        }

        var defaultKey = channels.Any(channel => channel.Key == DatastoreUsedChannel) ? DatastoreUsedChannel : channels[0].Key;
        channels = channels.Select(channel => channel with { IsDefault = channel.Key == defaultKey }).ToList();
        var value = channels.First(channel => channel.IsDefault).Value;

        var broken = stores.Where(store => store.Error is not null).ToList();
        var message = Describe(stores, readable, lastBackups, failures, warnings, now, broken);
        return broken.Count > 0
            ? SensorExecutionResult.Critical(duration, message, value, defaultKey, channels)
            : SensorExecutionResult.Healthy(duration, message, value, defaultKey, channels);
    }

    private static string Describe(IReadOnlyList<PbsDatastore> stores, List<PbsDatastore> readable, List<DateTimeOffset> lastBackups, List<PbsTask> failures, List<PbsTask> warnings, DateTimeOffset now, List<PbsDatastore> broken)
    {
        var parts = new List<string>();
        foreach (var store in broken)
        {
            parts.Add($"datastore {store.Name} unavailable: {store.Error}");
        }

        if (readable.Count > 0)
        {
            var fullest = readable.MaxBy(store => store.Used!.Value / store.Total!.Value)!;
            parts.Add(stores.Count == 1
                ? $"{fullest.Name} {fullest.Used!.Value / fullest.Total!.Value * 100:0}% used"
                : $"{stores.Count} datastores, {fullest.Name} fullest at {fullest.Used!.Value / fullest.Total!.Value * 100:0}%");
        }

        parts.Add(lastBackups.Count == 0 ? "no backup yet" : $"newest backup {Ago(now - lastBackups.Max())}");
        if (failures.Count > 0)
        {
            var named = failures.Take(3).Select(task => string.IsNullOrWhiteSpace(task.Id) ? task.Type : $"{task.Type} {task.Id}");
            parts.Add($"{failures.Count} failed task{(failures.Count == 1 ? string.Empty : "s")} in 24 h ({string.Join(", ", named)})");
        }
        else if (warnings.Count > 0)
        {
            parts.Add($"{warnings.Count} task{(warnings.Count == 1 ? string.Empty : "s")} with warnings in 24 h");
        }

        var text = string.Join("; ", parts);
        return text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
    }

    private static double Hours(TimeSpan span) => Math.Round(Math.Max(0, span.TotalHours), 1);

    private static string Ago(TimeSpan span) =>
        span.TotalMinutes < 90 ? $"{Math.Max(0, span.TotalMinutes):0} min ago"
        : span.TotalHours < 72 ? $"{span.TotalHours:0} h ago"
        : $"{span.TotalDays:0} days ago";

    // ---- helpers ---------------------------------------------------------------------------------------------------------

    /// <summary>The PBS token header - <c>PBSAPIToken=user@realm!name:secret</c> (a colon, where Proxmox VE has '=').</summary>
    public static string? BuildAuthorizationHeader(string user, string tokenId, string tokenSecret)
    {
        var id = tokenId.Trim();
        var secret = tokenSecret.Trim();
        if (id.Length == 0 || secret.Length == 0)
        {
            return null;
        }

        if (!id.Contains('!'))
        {
            if (user.Trim().Length == 0)
            {
                return null;
            }

            id = $"{user.Trim()}!{id}";
        }

        return $"PBSAPIToken={id}:{secret}";
    }

    private static Uri? ResolveBaseUri(SensorExecutionContext context)
    {
        var target = context.Target!.Trim();
        if (!target.Contains("://", StringComparison.Ordinal))
        {
            var port = MonitoringSettings.TryReadParameterInt(context.Settings, "pbs.port", out var configured) && configured is >= 1 and <= 65535 ? configured : DefaultPort;
            target = $"https://{target}:{port}";
        }

        return Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? new Uri(uri.GetLeftPart(UriPartial.Authority) + "/api2/json/")
            : null;
    }

    private static async Task<JsonElement> GetDataAsync(HttpClient client, string path, bool required, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(path, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new PbsException("The backup server refused the API token (check the Token ID and secret).");
        }

        if (!response.IsSuccessStatusCode)
        {
            if (!required && response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
            {
                return default;
            }

            throw new PbsException(response.StatusCode == HttpStatusCode.Forbidden
                ? "The API token may not read the datastores - give it the Audit role on /."
                : $"The backup server answered {(int)response.StatusCode} for {path.Split('?')[0]}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return document.RootElement.TryGetProperty("data", out var data) ? data.Clone() : default;
    }

    private static HttpMessageHandler DefaultHandler(bool verifySsl)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        if (!verifySsl)
        {
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        return handler;
    }

    private static IEnumerable<JsonElement> Items(JsonElement element) =>
        element.ValueKind == JsonValueKind.Array ? element.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object).ToList() : [];

    private static JsonElement Child(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) ? value : default;

    private static double? UsedPercent(JsonElement element, string totalProperty, string usedProperty) =>
        Number(element, totalProperty) is { } total && total > 0 && Number(element, usedProperty) is { } used ? Math.Round(used / total * 100, 1) : null;

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    private static double? Number(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? number
            : null;

    private sealed class PbsException(string message) : Exception(message);
}
