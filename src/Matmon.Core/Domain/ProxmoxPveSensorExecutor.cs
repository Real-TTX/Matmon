using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Matmon.Core.Domain;

public sealed class ProxmoxPveSensorExecutor : ISensorExecutor
{
    public static SensorDefinition Definition { get; } = new SensorDefinition
    {
        Key = "proxmox",
        DisplayName = "Proxmox PVE",
        Description = "Monitor Proxmox cluster or node health via the REST API using API tokens.",
        ChannelMode = SensorChannelMode.Fixed,
        Parameters =
        [
            new SensorParameterDefinition
            {
                Key = "pve.scope",
                Label = "Scope",
                Kind = SensorParameterKind.ValueList,
                Description = "Cluster or node health",
                DefaultValue = "cluster",
                Options =
                [
                    new SensorParameterOption { Value = "cluster", Label = "Cluster" },
                    new SensorParameterOption { Value = "node", Label = "Node" }
                ]
            },
            new SensorParameterDefinition
            {
                Key = "pve.port",
                Label = "API port",
                Kind = SensorParameterKind.Integer,
                Description = "Proxmox API port on the target node",
                DefaultValue = "8006",
                Min = 1,
                Max = 65535,
                Step = "1"
            },
            new SensorParameterDefinition
            {
                Key = "pve.user",
                Label = "API user (optional)",
                Kind = SensorParameterKind.Text,
                Description = "Only needed if the Token ID is just the token name rather than the full user@realm!name.",
                Required = false,
                Placeholder = "root@pam",
                CredentialKind = MonitoringCredentialKind.Proxmox
            },
            new SensorParameterDefinition
            {
                Key = "pve.tokenId",
                Label = "Token ID",
                Kind = SensorParameterKind.Text,
                Description = "Full Proxmox API token ID, exactly as shown in the Proxmox UI, e.g. root@pam!monitoring.",
                Required = true,
                Placeholder = "root@pam!monitoring",
                CredentialKind = MonitoringCredentialKind.Proxmox
            },
            new SensorParameterDefinition
            {
                Key = "pve.tokenSecret",
                Label = "Token secret",
                Kind = SensorParameterKind.Secret,
                Description = "API token secret",
                Required = true,
                CredentialKind = MonitoringCredentialKind.Proxmox
            },
            new SensorParameterDefinition
            {
                Key = "pve.verifySsl",
                Label = "Verify SSL",
                Kind = SensorParameterKind.Boolean,
                Description = "Validate the Proxmox certificate",
                DefaultValue = "false"
            }
        ]
    };

    public string SensorTypeKey => Definition.Key;

    public ValueTask<SensorDiscoveryCheckResult> DiscoverAsync(
        SensorDiscoveryContext context,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        if (!context.OpenTcpPorts.Contains(8006))
        {
            return ValueTask.FromResult(SensorDiscoveryCheckResult.NotAvailable);
        }

        var settings = new MonitoringSettings();
        settings.Parameters["pve.scope"] = "cluster";
        settings.Parameters["pve.port"] = "8006";
        settings.Parameters["pve.verifySsl"] = "false";

        return ValueTask.FromResult(
            SensorDiscoveryCheckResult.Available(
                new SensorDiscoverySuggestion(
                    Definition.Key,
                    "Proxmox PVE Cluster",
                    string.Empty,
                    settings,
                    "Proxmox API port 8006 is open. API credentials can be inherited from the parent.",
                    86)));
    }

    public async ValueTask<SensorExecutionResult> ExecuteAsync(
        SensorExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.Target))
        {
            return SensorExecutionResult.Critical(TimeSpan.Zero, "PVE host / API URL is required");
        }

        MonitoringSettings.TryReadParameter(context.Settings, "pve.user", out var user);
        user = (user ?? string.Empty).Trim();

        if (!MonitoringSettings.TryReadParameter(context.Settings, "pve.tokenId", out var tokenId) ||
            string.IsNullOrWhiteSpace(tokenId))
        {
            return SensorExecutionResult.Critical(TimeSpan.Zero, "Proxmox token ID is required");
        }

        // A full token ID already carries the user@realm (root@pam!name), exactly as the Proxmox UI
        // shows it; the separate API user is only needed when the Token ID is just the bare name.
        if (!tokenId.Contains('!') && string.IsNullOrWhiteSpace(user))
        {
            return SensorExecutionResult.Critical(TimeSpan.Zero, "Enter the full Token ID (user@realm!name), or set the API user separately");
        }

        if (!MonitoringSettings.TryReadParameter(context.Settings, "pve.tokenSecret", out var tokenSecret) ||
            string.IsNullOrWhiteSpace(tokenSecret))
        {
            return SensorExecutionResult.Critical(TimeSpan.Zero, "Proxmox token secret is required");
        }

        var scope = ResolveScope(context.Settings);
        var verifySsl = MonitoringSettings.TryReadParameterBool(context.Settings, "pve.verifySsl", out var configuredVerifySsl) &&
            configuredVerifySsl;
        var port = MonitoringSettings.TryReadParameterInt(context.Settings, "pve.port", out var configuredPort)
            ? configuredPort
            : 8006;

        var timeout = context.Settings.Timeout ?? TimeSpan.FromSeconds(15);

        var watch = Stopwatch.StartNew();
        try
        {
            using var client = CreateHttpClient(verifySsl);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            var apiBaseUri = BuildApiBaseUri(context.Target, port);
            var authHeader = BuildAuthorizationHeader(user, tokenId, tokenSecret);

            return scope switch
            {
                "node" => await ExecuteNodeAsync(client, apiBaseUri, context.Settings, authHeader, user, tokenId, context.Target, watch, timeoutCts.Token),
                _ => await ExecuteClusterAsync(client, apiBaseUri, context.Settings, authHeader, user, tokenId, watch, timeoutCts.Token)
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            watch.Stop();
            return SensorExecutionResult.Critical(watch.Elapsed, "request timed out");
        }
        catch (Exception ex)
        {
            watch.Stop();
            return SensorExecutionResult.Critical(watch.Elapsed, ex.Message);
        }
    }

    private static async ValueTask<SensorExecutionResult> ExecuteClusterAsync(
        HttpClient client,
        Uri apiBaseUri,
        MonitoringSettings settings,
        string authHeader,
        string apiUser,
        string tokenId,
        Stopwatch watch,
        CancellationToken cancellationToken)
    {
        var resources = await ReadApiDataAsync(client, new Uri(apiBaseUri, "cluster/resources"), authHeader, apiUser, tokenId, cancellationToken);
        var resourceSnapshot = BuildResourceSnapshot(resources);

        try
        {
            var clusterStatus = await ReadApiDataAsync(client, new Uri(apiBaseUri, "cluster/status"), authHeader, apiUser, tokenId, cancellationToken);
            var quorate = TryReadQuorate(clusterStatus, out var quorateValue) && quorateValue;
            var channels = new List<SensorChannelValue>
            {
                new()
                {
                    Key = "quorum",
                    Label = "Quorum",
                    Value = quorate ? 1 : 0,
                    IsDefault = true
                }
            };

            channels.AddRange(BuildResourceChannels(resourceSnapshot));

            var state = !quorate
                ? SensorState.Critical
                : resourceSnapshot.NodeOfflineCount > 0
                    ? SensorState.Warning
                    : SensorState.Healthy;

            var message = !quorate
                ? "cluster is not quorate"
                : resourceSnapshot.NodeOfflineCount > 0
                    ? $"{resourceSnapshot.NodeOnlineCount}/{resourceSnapshot.NodeCount} nodes online"
                    : $"cluster healthy ({resourceSnapshot.NodeOnlineCount} nodes online)";

            var result = state switch
            {
                SensorState.Critical => SensorExecutionResult.Critical(watch.Elapsed, message, quorate ? 1 : 0, "quorum", channels),
                SensorState.Warning => SensorExecutionResult.Warning(watch.Elapsed, message, quorate ? 1 : 0, "quorum", channels),
                _ => SensorExecutionResult.Healthy(watch.Elapsed, message, quorate ? 1 : 0, "quorum", channels)
            };

            return SensorThresholdEvaluator.ApplyChannelThresholds(settings, result);
        }
        catch (InvalidOperationException ex) when (IsPermissionDenied(ex))
        {
            var channels = BuildResourceChannels(resourceSnapshot).ToList();
            var defaultKey = resourceSnapshot.NodeCount > 0 ? "onlineNodes" : "resourcesTotal";
            var defaultValue = resourceSnapshot.NodeCount > 0 ? resourceSnapshot.NodeOnlineCount : resourceSnapshot.VisibleResourceCount;
            var state = resourceSnapshot.NodeCount == 0
                ? SensorState.Critical
                : resourceSnapshot.NodeOfflineCount > 0
                    ? SensorState.Warning
                    : SensorState.Healthy;

            var message = resourceSnapshot.NodeCount == 0
                ? "no visible nodes"
                : resourceSnapshot.NodeOfflineCount > 0
                    ? $"{resourceSnapshot.NodeOnlineCount}/{resourceSnapshot.NodeCount} visible nodes online"
                    : $"cluster resources healthy ({resourceSnapshot.NodeOnlineCount} visible nodes online)";

            var result = state switch
            {
                SensorState.Critical => SensorExecutionResult.Critical(watch.Elapsed, message, defaultValue, defaultKey, channels),
                SensorState.Warning => SensorExecutionResult.Warning(watch.Elapsed, message, defaultValue, defaultKey, channels),
                _ => SensorExecutionResult.Healthy(watch.Elapsed, message, defaultValue, defaultKey, channels)
            };

            return SensorThresholdEvaluator.ApplyChannelThresholds(settings, result);
        }
    }

    private static async ValueTask<SensorExecutionResult> ExecuteNodeAsync(
        HttpClient client,
        Uri apiBaseUri,
        MonitoringSettings settings,
        string authHeader,
        string apiUser,
        string tokenId,
        string target,
        Stopwatch watch,
        CancellationToken cancellationToken)
    {
        // An explicit node name (pve.node) wins; otherwise the target host name is used as the
        // hint and, failing a match, the first visible node is picked.
        var nodeHint = MonitoringSettings.TryReadParameter(settings, "pve.node", out var configuredNode) && !string.IsNullOrWhiteSpace(configuredNode)
            ? configuredNode.Trim()
            : target;

        var resourceSnapshot = BuildResourceSnapshot(await ReadApiDataAsync(client, new Uri(apiBaseUri, "cluster/resources"), authHeader, apiUser, tokenId, cancellationToken), ResolveNodeName(nodeHint));
        var nodeOverview = await ReadApiDataAsync(client, new Uri(apiBaseUri, "nodes"), authHeader, apiUser, tokenId, cancellationToken);
        var nodeName = ResolveNodeNameFromOverview(nodeOverview, nodeHint);

        try
        {
            var nodeStatus = await ReadApiDataAsync(client, new Uri(apiBaseUri, $"nodes/{Uri.EscapeDataString(nodeName)}/status"), authHeader, apiUser, tokenId, cancellationToken);

            var statusText = TryReadString(nodeStatus, "status", out var status) ? status : "unknown";
            var cpuPercent = TryReadDouble(nodeStatus, "cpu", out var cpu) ? cpu * 100.0 : 0;
            var memoryPercent = TryReadPercent(nodeStatus, "mem", "maxmem", out var memory) ? memory : 0;
            var swapPercent = TryReadPercent(nodeStatus, "swap", "maxswap", out var swap) ? swap : 0;
            var rootfsPercent = TryReadPercent(nodeStatus, "rootfs", "maxrootfs", out var rootfs) ? rootfs : 0;
            var uptimeHours = TryReadDouble(nodeStatus, "uptime", out var uptimeSeconds) ? uptimeSeconds / 3600.0 : 0;

            var loadValues = TryReadLoadAverage(nodeStatus);
            var channels = new List<SensorChannelValue>
            {
                new()
                {
                    Key = "cpu",
                    Label = "CPU",
                    Value = Round(cpuPercent),
                    Unit = "%",
                    IsDefault = true
                },
                new()
                {
                    Key = "memory",
                    Label = "Memory",
                    Value = Round(memoryPercent),
                    Unit = "%"
                },
                new()
                {
                    Key = "swap",
                    Label = "Swap",
                    Value = Round(swapPercent),
                    Unit = "%"
                }
            };

            if (TryReadPercent(nodeStatus, "rootfs", "maxrootfs", out var parsedRootFs))
            {
                channels.Add(new SensorChannelValue
                {
                    Key = "rootfs",
                    Label = "Root FS",
                    Value = Round(parsedRootFs),
                    Unit = "%"
                });
            }

            channels.AddRange(loadValues.Select(pair => new SensorChannelValue
            {
                Key = pair.Key,
                Label = pair.Label,
                Value = Round(pair.Value)
            }));

            channels.Add(new SensorChannelValue
            {
                Key = "uptimeHours",
                Label = "Uptime",
                Value = Round(uptimeHours),
                Unit = "h"
            });

            channels.AddRange(BuildResourceChannels(resourceSnapshot));

            var state = string.Equals(statusText, "online", StringComparison.OrdinalIgnoreCase)
                ? SensorState.Healthy
                : SensorState.Critical;

            var message = string.Equals(statusText, "online", StringComparison.OrdinalIgnoreCase)
                ? $"node {nodeName} is online"
                : $"node {nodeName} is {statusText}";

            var result = state switch
            {
                SensorState.Critical => SensorExecutionResult.Critical(watch.Elapsed, message, Round(cpuPercent), "cpu", channels),
                _ => SensorExecutionResult.Healthy(watch.Elapsed, message, Round(cpuPercent), "cpu", channels)
            };

            return SensorThresholdEvaluator.ApplyChannelThresholds(settings, result);
        }
        catch (InvalidOperationException ex) when (IsPermissionDenied(ex))
        {
            return BuildNodeOverviewFallbackResult(settings, resourceSnapshot, nodeName, watch);
        }
    }

    internal static HttpClient CreateHttpClient(bool verifySsl)
    {
        var handler = new HttpClientHandler();
        if (!verifySsl)
        {
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    internal static Uri BuildApiBaseUri(string target, int port)
    {
        if (Uri.TryCreate(target, UriKind.Absolute, out var absoluteUri))
        {
            return EnsureApiBaseUri(absoluteUri);
        }

        var normalizedTarget = target.Trim().TrimEnd('/');
        return new Uri($"https://{normalizedTarget}:{port}/api2/json/");
    }

    private static Uri EnsureApiBaseUri(Uri uri)
    {
        var builder = new UriBuilder(uri);
        var path = builder.Path?.TrimEnd('/') ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path) || path.Equals("/api2/json", StringComparison.OrdinalIgnoreCase))
        {
            builder.Path = "/api2/json/";
        }
        else if (!path.EndsWith("/api2/json", StringComparison.OrdinalIgnoreCase))
        {
            builder.Path = $"{path.TrimEnd('/')}/api2/json/";
        }
        else if (!path.EndsWith("/"))
        {
            builder.Path = $"{path}/";
        }

        return builder.Uri;
    }

    private static string ResolveScope(MonitoringSettings settings)
    {
        if (MonitoringSettings.TryReadParameter(settings, "pve.scope", out var scope) &&
            !string.IsNullOrWhiteSpace(scope))
        {
            return scope.Trim().ToLowerInvariant();
        }

        return "cluster";
    }

    private static string ResolveNodeName(string target)
    {
        if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
        {
            return uri.Host;
        }

        return target.Trim();
    }

    private static string ResolveNodeNameFromOverview(JsonElement nodeOverview, string target)
    {
        var normalizedTarget = ResolveNodeName(target);

        if (nodeOverview.ValueKind == JsonValueKind.Array)
        {
            var firstNodeName = string.Empty;

            foreach (var entry in nodeOverview.EnumerateArray())
            {
                if (TryReadString(entry, "node", out var nodeName) && !string.IsNullOrWhiteSpace(nodeName))
                {
                    if (string.Equals(nodeName, normalizedTarget, StringComparison.OrdinalIgnoreCase))
                    {
                        return nodeName;
                    }

                    if (string.IsNullOrWhiteSpace(firstNodeName))
                    {
                        firstNodeName = nodeName;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(firstNodeName))
            {
                return firstNodeName;
            }
        }

        return normalizedTarget;
    }

    private static SensorExecutionResult BuildNodeOverviewFallbackResult(MonitoringSettings settings, ResourceSnapshot resourceSnapshot, string nodeName, Stopwatch watch)
    {
        var channels = BuildNodeFallbackChannels(resourceSnapshot).ToList();
        var state = !resourceSnapshot.SelectedNodeOnline
            ? SensorState.Critical
            : resourceSnapshot.NodeOfflineCount > 0
                ? SensorState.Warning
                : SensorState.Healthy;

        var message = !resourceSnapshot.SelectedNodeOnline
            ? $"node {nodeName} is offline"
            : resourceSnapshot.NodeOfflineCount > 0
                ? $"{resourceSnapshot.NodeOnlineCount}/{resourceSnapshot.NodeCount} nodes online"
                : $"node {nodeName} is online";

        var result = state switch
        {
            SensorState.Critical => SensorExecutionResult.Critical(watch.Elapsed, message, resourceSnapshot.SelectedNodeOnline ? 1 : 0, "nodeOnline", channels),
            SensorState.Warning => SensorExecutionResult.Warning(watch.Elapsed, message, resourceSnapshot.SelectedNodeOnline ? 1 : 0, "nodeOnline", channels),
            _ => SensorExecutionResult.Healthy(watch.Elapsed, message, resourceSnapshot.SelectedNodeOnline ? 1 : 0, "nodeOnline", channels)
        };

        return SensorThresholdEvaluator.ApplyChannelThresholds(settings, result);
    }

    private static bool IsPermissionDenied(Exception exception)
    {
        return exception.Message.Contains("Permission check failed", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("403", StringComparison.OrdinalIgnoreCase);
    }

    private static ResourceSnapshot BuildResourceSnapshot(JsonElement resources, string? selectedNode = null)
    {
        var totalVisibleResources = 0;
        var nodeCount = 0;
        var nodeOnlineCount = 0;
        var selectedNodeOnline = false;
        var qemuCount = 0;
        var qemuRunningCount = 0;
        var qemuStoppedCount = 0;
        var lxcCount = 0;
        var lxcRunningCount = 0;
        var lxcStoppedCount = 0;
        var storageCount = 0;
        var storageOnlineCount = 0;
        var storageOfflineCount = 0;

        if (resources.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in resources.EnumerateArray())
            {
                totalVisibleResources++;

                var type = TryReadString(entry, "type", out var rawType)
                    ? rawType.Trim().ToLowerInvariant()
                    : string.Empty;
                var status = TryReadString(entry, "status", out var rawStatus)
                    ? rawStatus.Trim().ToLowerInvariant()
                    : string.Empty;

                switch (type)
                {
                    case "node":
                        nodeCount++;
                        if (IsNodeOnline(status))
                        {
                            nodeOnlineCount++;
                        }

                        if (!string.IsNullOrWhiteSpace(selectedNode) &&
                            TryReadString(entry, "node", out var nodeName) &&
                            string.Equals(nodeName, selectedNode, StringComparison.OrdinalIgnoreCase))
                        {
                            selectedNodeOnline = IsNodeOnline(status);
                        }
                        break;
                    case "qemu":
                        qemuCount++;
                        if (IsGuestRunning(status))
                        {
                            qemuRunningCount++;
                        }
                        else
                        {
                            qemuStoppedCount++;
                        }
                        break;
                    case "lxc":
                        lxcCount++;
                        if (IsGuestRunning(status))
                        {
                            lxcRunningCount++;
                        }
                        else
                        {
                            lxcStoppedCount++;
                        }
                        break;
                    case "storage":
                        storageCount++;
                        if (IsStorageOnline(status))
                        {
                            storageOnlineCount++;
                        }
                        else
                        {
                            storageOfflineCount++;
                        }
                        break;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(selectedNode) && nodeCount == 1 && nodeOnlineCount == 1)
        {
            selectedNodeOnline = true;
        }

        return new ResourceSnapshot(
            totalVisibleResources,
            nodeCount,
            nodeOnlineCount,
            Math.Max(0, nodeCount - nodeOnlineCount),
            selectedNodeOnline,
            qemuCount,
            qemuRunningCount,
            qemuStoppedCount,
            lxcCount,
            lxcRunningCount,
            lxcStoppedCount,
            storageCount,
            storageOnlineCount,
            storageOfflineCount);
    }

    private static IEnumerable<SensorChannelValue> BuildResourceChannels(ResourceSnapshot snapshot)
    {
        var channels = new List<SensorChannelValue>
        {
            new()
            {
                Key = "resourcesTotal",
                Label = "Visible resources",
                Value = snapshot.VisibleResourceCount,
                IsDefault = false
            },
            new()
            {
                Key = "onlineNodes",
                Label = "Online nodes",
                Value = snapshot.NodeOnlineCount
            },
            new()
            {
                Key = "offlineNodes",
                Label = "Offline nodes",
                Value = snapshot.NodeOfflineCount
            },
            new()
            {
                Key = "nodeOnlineRatio",
                Label = "Node online ratio",
                Value = snapshot.NodeCount == 0 ? 0 : Round((double)snapshot.NodeOnlineCount / snapshot.NodeCount * 100.0),
                Unit = "%"
            }
        };

        AppendResourceTypeChannels(channels, snapshot);

        return channels;
    }

    private static IEnumerable<SensorChannelValue> BuildNodeFallbackChannels(ResourceSnapshot snapshot)
    {
        var channels = new List<SensorChannelValue>
        {
            new SensorChannelValue
            {
                Key = "nodeOnline",
                Label = "Node online",
                Value = snapshot.SelectedNodeOnline ? 1 : 0,
                IsDefault = true
            },
            new SensorChannelValue
            {
                Key = "resourcesTotal",
                Label = "Visible resources",
                Value = snapshot.VisibleResourceCount
            },
            new SensorChannelValue
            {
                Key = "onlineNodes",
                Label = "Online nodes",
                Value = snapshot.NodeOnlineCount
            },
            new SensorChannelValue
            {
                Key = "offlineNodes",
                Label = "Offline nodes",
                Value = snapshot.NodeOfflineCount
            },
            new SensorChannelValue
            {
                Key = "nodeOnlineRatio",
                Label = "Node online ratio",
                Value = snapshot.NodeCount == 0 ? 0 : Round((double)snapshot.NodeOnlineCount / snapshot.NodeCount * 100.0),
                Unit = "%"
            }
        };

        AppendResourceTypeChannels(channels, snapshot);

        return channels;
    }

    private static void AppendResourceTypeChannels(List<SensorChannelValue> channels, ResourceSnapshot snapshot)
    {
        // VMs (QEMU) - always emitted so the counts are stable even at zero.
        channels.Add(new SensorChannelValue { Key = "vmOnline", Label = "VMs online", Value = snapshot.QemuRunningCount });
        channels.Add(new SensorChannelValue { Key = "vmOffline", Label = "VMs offline", Value = snapshot.QemuStoppedCount });
        channels.Add(new SensorChannelValue { Key = "vmTotal", Label = "VMs total", Value = snapshot.QemuCount, LogByDefault = false });

        // Containers (LXC).
        channels.Add(new SensorChannelValue { Key = "containerOnline", Label = "Containers online", Value = snapshot.LxcRunningCount });
        channels.Add(new SensorChannelValue { Key = "containerOffline", Label = "Containers offline", Value = snapshot.LxcStoppedCount });
        channels.Add(new SensorChannelValue { Key = "containerTotal", Label = "Containers total", Value = snapshot.LxcCount, LogByDefault = false });

        // Storages.
        channels.Add(new SensorChannelValue { Key = "storageOnline", Label = "Storages online", Value = snapshot.StorageOnlineCount });
        channels.Add(new SensorChannelValue { Key = "storageOffline", Label = "Storages offline", Value = snapshot.StorageOfflineCount });
    }

    private static bool IsNodeOnline(string status)
    {
        return string.Equals(status, "online", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGuestRunning(string status)
    {
        return string.Equals(status, "running", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStorageOnline(string status)
    {
        return string.Equals(status, "online", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "active", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "available", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ResourceSnapshot(
        int VisibleResourceCount,
        int NodeCount,
        int NodeOnlineCount,
        int NodeOfflineCount,
        bool SelectedNodeOnline,
        int QemuCount,
        int QemuRunningCount,
        int QemuStoppedCount,
        int LxcCount,
        int LxcRunningCount,
        int LxcStoppedCount,
        int StorageCount,
        int StorageOnlineCount,
        int StorageOfflineCount);

    internal static string BuildAuthorizationHeader(string user, string tokenId, string tokenSecret)
    {
        var resolvedTokenId = ResolveTokenId(user, tokenId);
        return $"PVEAPIToken={resolvedTokenId}={tokenSecret.Trim()}";
    }

    private static string ResolveTokenId(string user, string tokenId)
    {
        var normalizedUser = user.Trim();
        var normalizedTokenId = tokenId.Trim();

        if (string.IsNullOrWhiteSpace(normalizedTokenId))
        {
            return normalizedUser;
        }

        if (normalizedTokenId.Contains('!'))
        {
            return normalizedTokenId;
        }

        return string.IsNullOrWhiteSpace(normalizedUser)
            ? normalizedTokenId
            : $"{normalizedUser}!{normalizedTokenId}";
    }

    internal static async Task<JsonElement> ReadApiDataAsync(
        HttpClient client,
        Uri uri,
        string authorizationHeader,
        string apiUser,
        string tokenId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("Authorization", authorizationHeader);
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var statusText = string.IsNullOrWhiteSpace(body) ? response.ReasonPhrase : body.Trim();
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                throw new InvalidOperationException(
                    $"Proxmox authentication failed for user '{apiUser}' and token '{tokenId}'. " +
                    "Use an API user like 'root@pam' and a separate token name like 'monitoring', then paste the token secret.");
            }

            throw new InvalidOperationException($"Proxmox API request to '{uri}' failed with {(int)response.StatusCode} {response.ReasonPhrase}: {statusText}");
        }

        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("data", out var data))
        {
            throw new InvalidOperationException($"Proxmox API response from '{uri}' does not contain data");
        }

        return data.Clone();
    }

    private static bool TryReadQuorate(JsonElement element, out bool quorate)
    {
        if (TryReadBool(element, "quorate", out quorate))
        {
            return true;
        }

        if (TryReadInt(element, "quorate", out var quorateAsInt))
        {
            quorate = quorateAsInt != 0;
            return true;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in element.EnumerateArray())
            {
                if (TryReadBool(entry, "quorate", out quorate))
                {
                    return true;
                }

                if (TryReadInt(entry, "quorate", out quorateAsInt))
                {
                    quorate = quorateAsInt != 0;
                    return true;
                }
            }
        }

        quorate = true;
        return false;
    }

    private static bool TryReadPercent(
        JsonElement element,
        string valueKey,
        string maxKey,
        out double percent)
    {
        percent = 0;
        if (!TryReadDouble(element, valueKey, out var value) ||
            !TryReadDouble(element, maxKey, out var maxValue) ||
            maxValue <= 0)
        {
            return false;
        }

        percent = (value / maxValue) * 100.0;
        return true;
    }

    private static IReadOnlyList<(string Key, string Label, double Value)> TryReadLoadAverage(JsonElement element)
    {
        if (!TryReadString(element, "loadavg", out var rawLoadAvg) ||
            string.IsNullOrWhiteSpace(rawLoadAvg))
        {
            return [];
        }

        var parts = rawLoadAvg
            .Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : (double?)null)
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .ToArray();

        if (parts.Length == 0)
        {
            return [];
        }

        var items = new List<(string Key, string Label, double Value)>(parts.Length);
        if (parts.Length > 0)
        {
            items.Add(("load1", "Load 1m", parts[0]));
        }

        if (parts.Length > 1)
        {
            items.Add(("load5", "Load 5m", parts[1]));
        }

        if (parts.Length > 2)
        {
            items.Add(("load15", "Load 15m", parts[2]));
        }

        return items;
    }

    private static bool TryReadString(JsonElement element, string propertyName, out string value)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var property))
        {
            if (property.ValueKind == JsonValueKind.String)
            {
                value = property.GetString() ?? string.Empty;
                return true;
            }

            value = property.ToString();
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryReadDouble(JsonElement element, string propertyName, out double value)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var property))
        {
            if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out value))
            {
                return true;
            }

            if (property.ValueKind == JsonValueKind.String &&
                double.TryParse(property.GetString(), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value))
            {
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool TryReadInt(JsonElement element, string propertyName, out int value)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var property))
        {
            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out value))
            {
                return true;
            }

            if (property.ValueKind == JsonValueKind.String &&
                int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool TryReadBool(JsonElement element, string propertyName, out bool value)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var property))
        {
            if (property.ValueKind == JsonValueKind.True || property.ValueKind == JsonValueKind.False)
            {
                value = property.GetBoolean();
                return true;
            }

            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var intValue))
            {
                value = intValue != 0;
                return true;
            }

            if (property.ValueKind == JsonValueKind.String &&
                bool.TryParse(property.GetString(), out value))
            {
                return true;
            }
        }

        value = default;
        return false;
    }

    private static double Round(double value)
    {
        return Math.Round(value, 2, MidpointRounding.AwayFromZero);
    }
}
