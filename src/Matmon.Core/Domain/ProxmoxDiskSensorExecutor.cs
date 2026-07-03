using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Matmon.Core.Domain;

/// <summary>
/// Per-disk SMART health and wear of a Proxmox VE host/cluster via the REST API
/// (<c>/nodes/{node}/disks/list</c>). This also closes the SMART gap the compact
/// Proxmox PVE sensor leaves open. Reuses the Proxmox PVE HTTP/auth helpers.
/// </summary>
public sealed class ProxmoxDiskSensorExecutor : ISensorExecutor
{
    public static SensorDefinition Definition { get; } = new()
    {
        Key = "proxmox-disk",
        DisplayName = "Proxmox Disk",
        Description = "Per-disk SMART health and wear-out across all Proxmox VE nodes via the REST API (uses API tokens).",
        ChannelMode = SensorChannelMode.Dynamic,
        // Same credentials as the Proxmox PVE sensor, minus the cluster/node scope toggle.
        Parameters = ProxmoxPveSensorExecutor.Definition.Parameters
            .Where(parameter => !string.Equals(parameter.Key, "pve.scope", StringComparison.OrdinalIgnoreCase))
            .ToArray()
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

        var settings = new MonitoringSettings { DefaultChannelKey = "smartStatus" };
        settings.Parameters["pve.port"] = "8006";
        settings.Parameters["pve.verifySsl"] = "false";

        return ValueTask.FromResult(
            SensorDiscoveryCheckResult.Available(
                new SensorDiscoverySuggestion(
                    Definition.Key,
                    "Proxmox Disk",
                    string.Empty,
                    settings,
                    "Proxmox API port 8006 is open. API credentials can be inherited from the parent.",
                    64)));
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

        if (!MonitoringSettings.TryReadParameter(context.Settings, "pve.tokenId", out var tokenId) || string.IsNullOrWhiteSpace(tokenId) ||
            !MonitoringSettings.TryReadParameter(context.Settings, "pve.tokenSecret", out var tokenSecret) || string.IsNullOrWhiteSpace(tokenSecret))
        {
            return SensorExecutionResult.Critical(TimeSpan.Zero, "Proxmox token ID and token secret are required");
        }

        // A full token ID already carries the user@realm (root@pam!name); the separate API user is
        // only needed when the Token ID is just the bare name — mirror ProxmoxPveSensorExecutor.
        if (!tokenId.Contains('!') && string.IsNullOrWhiteSpace(user))
        {
            return SensorExecutionResult.Critical(TimeSpan.Zero, "Enter the full Token ID (user@realm!name), or set the API user separately");
        }

        var verifySsl = MonitoringSettings.TryReadParameterBool(context.Settings, "pve.verifySsl", out var configuredVerifySsl) && configuredVerifySsl;
        var port = MonitoringSettings.TryReadParameterInt(context.Settings, "pve.port", out var configuredPort) ? configuredPort : 8006;
        var timeout = context.Settings.Timeout ?? TimeSpan.FromSeconds(15);

        var watch = Stopwatch.StartNew();
        try
        {
            using var client = ProxmoxPveSensorExecutor.CreateHttpClient(verifySsl);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            var apiBaseUri = ProxmoxPveSensorExecutor.BuildApiBaseUri(context.Target, port);
            var authHeader = ProxmoxPveSensorExecutor.BuildAuthorizationHeader(user, tokenId, tokenSecret);

            var nodes = await ProxmoxPveSensorExecutor.ReadApiDataAsync(client, new Uri(apiBaseUri, "nodes"), authHeader, user, tokenId, timeoutCts.Token);
            var disks = new List<DiskSnapshot>();
            foreach (var node in EnumerateArray(nodes))
            {
                var nodeName = ReadString(node, "node");
                if (string.IsNullOrWhiteSpace(nodeName))
                {
                    continue;
                }

                var list = await ProxmoxPveSensorExecutor.ReadApiDataAsync(
                    client,
                    new Uri(apiBaseUri, $"nodes/{Uri.EscapeDataString(nodeName)}/disks/list"),
                    authHeader,
                    user,
                    tokenId,
                    timeoutCts.Token);

                foreach (var disk in EnumerateArray(list))
                {
                    disks.Add(new DiskSnapshot(
                        nodeName!,
                        ReadString(disk, "devpath") ?? ReadString(disk, "serial") ?? "disk",
                        ReadString(disk, "health"),
                        ReadDouble(disk, "wearout")));
                }
            }

            watch.Stop();
            if (disks.Count == 0)
            {
                return SensorExecutionResult.Critical(watch.Elapsed, "Proxmox returned no disks");
            }

            var channels = BuildChannels(disks);
            var state = DetermineState(disks);
            var message = BuildMessage(state, disks);
            var defaultChannel = channels.First(channel => channel.Key == "smartStatus");
            var marked = MarkDefault(channels, defaultChannel.Key);

            var result = state switch
            {
                SensorState.Critical => SensorExecutionResult.Critical(watch.Elapsed, message, defaultChannel.Value, defaultChannel.Key, marked),
                SensorState.Warning => SensorExecutionResult.Warning(watch.Elapsed, message, defaultChannel.Value, defaultChannel.Key, marked),
                _ => SensorExecutionResult.Healthy(watch.Elapsed, message, defaultChannel.Value, defaultChannel.Key, marked)
            };

            return SensorThresholdEvaluator.ApplyChannelThresholds(context.Settings, result);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            watch.Stop();
            return SensorExecutionResult.Critical(watch.Elapsed, "request timed out");
        }
        catch (OperationCanceledException)
        {
            return SensorExecutionResult.Unknown("execution cancelled");
        }
        catch (Exception ex)
        {
            watch.Stop();
            return SensorExecutionResult.Critical(watch.Elapsed, ex.Message);
        }
    }

    private static IReadOnlyList<SensorChannelValue> BuildChannels(IReadOnlyList<DiskSnapshot> disks)
    {
        var worst = disks.Select(HealthSeverity).DefaultIfEmpty(0).Max();
        var channels = new List<SensorChannelValue>
        {
            new()
            {
                Key = "smartStatus",
                Label = "SMART status",
                Value = worst,
                MeasurementKind = SensorMeasurementKind.Count,
                LogByDefault = false
            },
            new()
            {
                Key = "diskCount",
                Label = "Disks",
                Value = disks.Count,
                MeasurementKind = SensorMeasurementKind.Count,
                LogByDefault = false
            }
        };

        foreach (var disk in disks)
        {
            var keyBase = SanitizeKey($"{disk.Node}_{disk.DevPath}");
            var label = $"{disk.Node} {ShortDev(disk.DevPath)}";
            channels.Add(new SensorChannelValue
            {
                Key = $"{keyBase}Status",
                Label = label,
                Value = HealthSeverity(disk),
                MeasurementKind = SensorMeasurementKind.Count,
                LogByDefault = false
            });
            if (disk.Wearout.HasValue)
            {
                channels.Add(new SensorChannelValue
                {
                    Key = $"{keyBase}Wearout",
                    Label = $"{label} wear",
                    Value = disk.Wearout,
                    Unit = "%",
                    MeasurementKind = SensorMeasurementKind.Percent
                });
            }
        }

        return channels;
    }

    // PASSED/OK -> healthy, FAILED -> critical, anything else (e.g. UNKNOWN on USB) -> healthy.
    private static int HealthSeverity(DiskSnapshot disk)
    {
        var health = disk.Health?.Trim();
        if (string.IsNullOrEmpty(health))
        {
            return 0;
        }

        if (health.Contains("FAIL", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return 0;
    }

    private static SensorState DetermineState(IReadOnlyList<DiskSnapshot> disks)
    {
        return disks.Select(HealthSeverity).DefaultIfEmpty(0).Max() switch
        {
            >= 2 => SensorState.Critical,
            1 => SensorState.Warning,
            _ => SensorState.Healthy
        };
    }

    private static string BuildMessage(SensorState state, IReadOnlyList<DiskSnapshot> disks)
    {
        if (state == SensorState.Critical)
        {
            var failing = disks.Where(disk => HealthSeverity(disk) >= 2)
                .Select(disk => $"{disk.Node} {ShortDev(disk.DevPath)}")
                .ToArray();
            return $"disks critical - SMART failed: {string.Join(", ", failing)}";
        }

        return $"disks ok - {disks.Count} disks across {disks.Select(disk => disk.Node).Distinct(StringComparer.OrdinalIgnoreCase).Count()} node(s)";
    }

    private static IEnumerable<JsonElement> EnumerateArray(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                yield return item;
            }
        }
    }

    private static string? ReadString(JsonElement element, string property)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(property, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static double? ReadDouble(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var number) => number,
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    private static string ShortDev(string devPath)
    {
        var slash = devPath.LastIndexOf('/');
        return slash >= 0 && slash < devPath.Length - 1 ? devPath[(slash + 1)..] : devPath;
    }

    private static string SanitizeKey(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(char.IsLetterOrDigit(character) ? character : '_');
        }

        return builder.ToString().Trim('_');
    }

    private static IReadOnlyList<SensorChannelValue> MarkDefault(IReadOnlyList<SensorChannelValue> channels, string key)
    {
        return channels.Select(channel => channel with
        {
            IsDefault = string.Equals(channel.Key, key, StringComparison.OrdinalIgnoreCase)
        }).ToArray();
    }

    private sealed record DiskSnapshot(string Node, string DevPath, string? Health, double? Wearout);
}
