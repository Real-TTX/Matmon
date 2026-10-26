using System.Text.Json.Nodes;

namespace Matmon.Host.Services.SelfUpdate;

/// <summary>
/// Builds the create request for the updated container from the old one's inspect output - pure, because this is
/// where an update goes quietly wrong.
///
/// A container's <c>Config</c> is the image's defaults MERGED with what the operator set, and the two cannot be told
/// apart by looking at the container alone. Copied verbatim, the new container would keep the OLD image's
/// version variable (and report the old version for ever), its old command line and its old labels. So every
/// value that is exactly the old image's default is dropped and the new image supplies its own; what the operator set
/// (an environment variable, a port, a volume, a restart policy, the networks and their aliases) is kept.
/// </summary>
public static class DockerContainerRecreation
{
    /// <summary>Label on an updated container: the version it replaced.</summary>
    public const string FromVersionLabel = "eu.matmon.update.from";

    /// <summary>Label on an updated container: when.</summary>
    public const string UpdatedAtLabel = "eu.matmon.update.at";

    /// <summary>Label on the helper container: the container it updates (its name).</summary>
    public const string HelperForLabel = "eu.matmon.update.helper-for";

    // Config fields an image provides a default for: dropped when they equal it, so the NEW image's default applies.
    private static readonly string[] ImageDefaultFields =
        ["Cmd", "Entrypoint", "WorkingDir", "User", "Healthcheck", "ExposedPorts", "Volumes", "StopSignal", "Shell", "OnBuild"];

    public sealed record Plan(string Name, JsonObject CreateBody, IReadOnlyList<(string Network, JsonObject Endpoint)> ExtraNetworks);

    /// <param name="imageOwnedVariables">Variables that are always the image's, never the operator's (the version):
    /// dropped even when the old image cannot be inspected any more - carried over, the new build reported the old version
    /// and the update was rolled back as "not up" (found in the live test).</param>
    public static Plan Build(JsonObject container, JsonObject? oldImage, string newImageReference, IReadOnlyDictionary<string, string> extraLabels,
        IReadOnlyCollection<string>? imageOwnedVariables = null)
    {
        var name = (container["Name"]?.GetValue<string>() ?? throw new InvalidOperationException("the container has no name")).TrimStart('/');
        var config = container["Config"]?.DeepClone() as JsonObject ?? throw new InvalidOperationException("the container has no Config");
        var imageConfig = oldImage?["Config"] as JsonObject;

        config["Image"] = newImageReference;

        // Environment: drop exactly the old image's entries (the version, PATH, ASPNETCORE_URLS ...); keep the rest.
        var imageEnv = (imageConfig?["Env"] as JsonArray)?.Select(entry => entry?.GetValue<string>()).ToHashSet(StringComparer.Ordinal) ?? [];
        if (config["Env"] is JsonArray env)
        {
            var kept = new JsonArray();
            var owned = (imageOwnedVariables ?? []).Select(name => name + "=").ToList();
            foreach (var entry in env)
            {
                if (entry?.GetValue<string>() is { } value && !imageEnv.Contains(value) && !owned.Any(prefix => value.StartsWith(prefix, StringComparison.Ordinal)))
                {
                    kept.Add(value);
                }
            }

            config["Env"] = kept;
        }

        // Labels: drop the old image's own (its version, its source), keep compose's and the operator's, add ours.
        var labels = config["Labels"] as JsonObject ?? new JsonObject();
        if (imageConfig?["Labels"] is JsonObject imageLabels)
        {
            foreach (var (key, value) in imageLabels)
            {
                if (labels[key] is { } current && JsonNode.DeepEquals(current, value))
                {
                    labels.Remove(key);
                }
            }
        }

        foreach (var (key, value) in extraLabels)
        {
            labels[key] = value;
        }

        config["Labels"] = labels;

        foreach (var field in ImageDefaultFields)
        {
            if (config[field] is { } own && imageConfig?[field] is { } fromImage && JsonNode.DeepEquals(own, fromImage))
            {
                config.Remove(field);
            }
        }

        // Docker names an unnamed container's host after its own id; carried over, the new container would claim
        // the old one's id as its host name.
        var id = container["Id"]?.GetValue<string>() ?? string.Empty;
        if (config["Hostname"]?.GetValue<string>() is { } hostname && id.StartsWith(hostname, StringComparison.OrdinalIgnoreCase))
        {
            config.Remove("Hostname");
        }

        var body = config;
        body["HostConfig"] = container["HostConfig"]?.DeepClone();

        var networkMode = container["HostConfig"]?["NetworkMode"]?.GetValue<string>() ?? "default";
        var extra = new List<(string, JsonObject)>();
        if (!IsSharedNamespace(networkMode) && container["NetworkSettings"]?["Networks"] is JsonObject networks && networks.Count > 0)
        {
            // The engine attaches a new container to ONE network at create; the others are connected afterwards.
            var ordered = networks.OrderByDescending(entry => entry.Key == networkMode).ToList();
            var first = ordered[0];
            body["NetworkingConfig"] = new JsonObject
            {
                ["EndpointsConfig"] = new JsonObject { [first.Key] = Endpoint(first.Value as JsonObject, id) }
            };
            foreach (var (network, settings) in ordered.Skip(1))
            {
                extra.Add((network, Endpoint(settings as JsonObject, id)));
            }
        }

        return new Plan(name, body, extra);
    }

    /// <summary>Whether the container shares another namespace (host, none, container:x) - then it has no networks of its own to recreate.</summary>
    public static bool IsSharedNamespace(string networkMode) =>
        networkMode is "host" or "none" || networkMode.StartsWith("container:", StringComparison.Ordinal);

    /// <summary>
    /// The HTTP port the app listens on inside the container (8099 unless changed). ASPNETCORE_URLS wins over
    /// ASPNETCORE_HTTP_PORTS, as in ASP.NET itself: the .NET base image sets HTTP_PORTS=8080 and Matmon's image overrides
    /// it with URLS - read the other way round, the helper waited on a port nothing listens on and rolled every update back.
    /// </summary>
    public static int ListenPort(JsonObject container)
    {
        var env = (container["Config"]?["Env"] as JsonArray ?? []).Select(node => node?.GetValue<string>() ?? string.Empty).ToList();
        foreach (var entry in env.Where(entry => entry.StartsWith("ASPNETCORE_URLS=", StringComparison.Ordinal)))
        {
            var url = entry["ASPNETCORE_URLS=".Length..].Split(';')[0];
            var colon = url.LastIndexOf(':');
            if (colon > 0 && int.TryParse(url[(colon + 1)..].TrimEnd('/'), out var urlPort))
            {
                return urlPort;
            }
        }

        foreach (var entry in env.Where(entry => entry.StartsWith("ASPNETCORE_HTTP_PORTS=", StringComparison.Ordinal)))
        {
            if (int.TryParse(entry["ASPNETCORE_HTTP_PORTS=".Length..].Split(';', ',')[0], out var port))
            {
                return port;
            }
        }

        return 8099;
    }

    // Only what the operator decided about the endpoint: its aliases (minus the container id Docker adds by itself),
    // a fixed address, links. The rest (the address it happened to get, the gateway) belongs to the old container.
    private static JsonObject Endpoint(JsonObject? settings, string containerId)
    {
        var endpoint = new JsonObject();
        if (settings?["Aliases"] is JsonArray aliases)
        {
            var kept = new JsonArray();
            foreach (var alias in aliases.Select(node => node?.GetValue<string>()).OfType<string>())
            {
                if (!containerId.StartsWith(alias, StringComparison.OrdinalIgnoreCase))
                {
                    kept.Add(alias);
                }
            }

            if (kept.Count > 0)
            {
                endpoint["Aliases"] = kept;
            }
        }

        foreach (var field in new[] { "IPAMConfig", "Links", "DriverOpts" })
        {
            if (settings?[field] is { } value && value.GetValueKind() != System.Text.Json.JsonValueKind.Null)
            {
                endpoint[field] = value.DeepClone();
            }
        }

        return endpoint;
    }
}
