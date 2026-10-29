using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Matmon.Host.Services.SelfUpdate;

/// <summary>
/// The Docker way: when the engine's socket is mounted into this container, the instance can see its own container,
/// ask the registry whether the tag it runs (<c>latest</c>, <c>nightly</c>) points at a newer image, pull it and hand
/// the swap to <see cref="DockerUpdateHelper"/>. Without the socket nothing here does anything - mounting it is the
/// operator's decision (it gives the container control over the host's Docker, i.e. root on the host).
/// The same file serves Matmon.Cloud, whose image names its version differently - hence the version and the name of
/// the image's version variable are parameters.
/// </summary>
public sealed partial class DockerSelfUpdateTarget(
    string socketPath,
    string currentVersion,
    string versionVariable,
    string? registryUsername = null,
    string? registryPassword = null) : ISelfUpdateTarget
{
    /// <summary>
    /// The tag the RUNNING image gets before a new one is pulled under its tag. With the containerd image store (Docker
    /// Desktop, newer engines) an image whose last tag moved on can no longer be addressed - the container still runs,
    /// but no new container (the helper) can be created from it. Found in the live test.
    /// </summary>
    public const string RollbackTag = "matmon-rollback";

    private string? _containerId;

    public SelfUpdateMode Mode => SelfUpdateMode.Docker;

    public bool IsAvailable(out string reason)
    {
        if (!OperatingSystem.IsLinux())
        {
            reason = "not running in a Linux container";
            return false;
        }

        if (!DockerEngineClient.SocketExists(socketPath))
        {
            reason = $"the Docker socket is not mounted ({socketPath})";
            return false;
        }

        _containerId ??= FindOwnContainerId();
        if (_containerId is null)
        {
            reason = "this process does not run in a container it can find";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>Has the engine restart this container (System > Updates "Restart"). The request does not come back - this
    /// process is stopped while it waits - so it is sent and left to the engine.</summary>
    public void RestartOwnContainer()
    {
        var id = _containerId ??= FindOwnContainerId() ?? throw new InvalidOperationException("this process does not run in a container it can find");
        var client = new DockerEngineClient(socketPath, timeout: TimeSpan.FromMinutes(2));
        _ = client.RestartAsync(id, 15, CancellationToken.None).ContinueWith(_ => client.Dispose(), TaskScheduler.Default);
    }

    public async Task<SelfUpdateOffer> CheckAsync(CancellationToken cancellationToken)
    {
        using var docker = Client();
        var self = await SelfAsync(docker, cancellationToken);
        var reference = ImageReference.Parse(self["Config"]?["Image"]?.GetValue<string>() ?? string.Empty);
        if (reference.PinnedByDigest)
        {
            return SelfUpdateOffer.None($"the container is pinned to an image digest ({reference}) - there is no tag to follow");
        }

        var runningId = self["Image"]!.GetValue<string>();
        var runningImage = await docker.InspectImageAsync(runningId, cancellationToken);
        var runningDigests = RepoDigests(runningImage);

        // A registry that does not know the name: an image built on this machine (compose --build). Its registry
        // reference alone is no proof either way - an image loses it when its tag is removed by hand - so the registry
        // is asked whenever there is one.
        string? latest;
        try
        {
            latest = await docker.GetRegistryDigestAsync(reference.ToString(), cancellationToken);
        }
        catch (InvalidOperationException ex) when (runningDigests.Count == 0)
        {
            return SelfUpdateOffer.None($"the image {reference} was built locally - there is no registry to update from ({ex.Message})");
        }

        if (latest is null || runningDigests.Any(digest => digest.EndsWith("@" + latest, StringComparison.OrdinalIgnoreCase)))
        {
            return SelfUpdateOffer.None($"up to date ({reference})");
        }

        // Pulled now, not at install time: the install window then only costs the restart, and the offer can say
        // which version it is (the image carries it in its version variable).
        var local = await docker.InspectImageAsync(reference.ToString(), cancellationToken);
        if (!RepoDigests(local).Any(digest => digest.EndsWith("@" + latest, StringComparison.OrdinalIgnoreCase)))
        {
            if (runningImage is not null)
            {
                await docker.TagAsync(runningId, reference.Repository, RollbackTag, cancellationToken);
            }

            await docker.PullAsync(reference, cancellationToken);
            local = await docker.InspectImageAsync(reference.ToString(), cancellationToken);
        }

        // The image ids decide in the end: the running image may simply have lost its registry reference.
        if (string.Equals(local?["Id"]?.GetValue<string>(), runningId, StringComparison.OrdinalIgnoreCase))
        {
            return SelfUpdateOffer.None($"up to date ({reference})");
        }

        var version = (local?["Config"]?["Env"] as JsonArray)?
            .Select(entry => entry?.GetValue<string>())
            .FirstOrDefault(entry => entry?.StartsWith(versionVariable + "=", StringComparison.Ordinal) == true)?[(versionVariable.Length + 1)..];
        return new SelfUpdateOffer(true, string.IsNullOrWhiteSpace(version) ? "a newer image" : version, $"{reference} has a newer image",
            local?["Id"]?.GetValue<string>());
    }

    /// <summary>
    /// A remote Docker probe follows its primary's build: CI publishes every build under its own version tag, so the
    /// image to install is <c>&lt;this container's repository&gt;:&lt;version&gt;</c>, pulled now (with the running image
    /// tagged for the rollback first, as in <see cref="CheckAsync"/>). Throws when the registry has no such tag - the
    /// caller then falls back to <see cref="CheckAsync"/> (the newest image of the tag the container runs).
    /// </summary>
    public async Task<SelfUpdateOffer> CheckVersionAsync(string version, CancellationToken cancellationToken)
    {
        using var docker = Client();
        var self = await SelfAsync(docker, cancellationToken);
        var reference = ImageReference.Parse(self["Config"]?["Image"]?.GetValue<string>() ?? string.Empty);
        if (reference.PinnedByDigest)
        {
            return SelfUpdateOffer.None($"the container is pinned to an image digest ({reference}) - there is no tag to follow");
        }

        var wanted = reference with { Tag = version };
        var runningId = self["Image"]!.GetValue<string>();
        var local = await docker.InspectImageAsync(wanted.ToString(), cancellationToken);
        if (local is null)
        {
            if (await docker.InspectImageAsync(runningId, cancellationToken) is not null)
            {
                await docker.TagAsync(runningId, reference.Repository, RollbackTag, cancellationToken);
            }

            await docker.PullAsync(wanted, cancellationToken);
            local = await docker.InspectImageAsync(wanted.ToString(), cancellationToken)
                ?? throw new InvalidOperationException($"{wanted} is not there after the pull");
        }

        var id = local["Id"]?.GetValue<string>();
        if (string.Equals(id, runningId, StringComparison.OrdinalIgnoreCase))
        {
            return SelfUpdateOffer.None($"already runs {wanted}");
        }

        return new SelfUpdateOffer(true, version, $"{wanted}", id) { ImageReference = wanted.ToString() };
    }

    public async Task ApplyAsync(SelfUpdateOffer offer, CancellationToken cancellationToken)
    {
        using var docker = Client();
        var self = await SelfAsync(docker, cancellationToken);
        var name = self["Name"]!.GetValue<string>().TrimStart('/');
        var running = self["Config"]!["Image"]!.GetValue<string>();
        var reference = offer.ImageReference ?? running;

        // The helper needs the same socket - as mounted into THIS container (the host side of the bind).
        var socketMount = (self["Mounts"] as JsonArray)?
            .FirstOrDefault(mount => mount?["Destination"]?.GetValue<string>() == socketPath)?["Source"]?.GetValue<string>()
            ?? throw new InvalidOperationException($"cannot find how {socketPath} is mounted into this container");

        // On a network of this container, so it can reach the new one's /healthz; host networking stays host.
        var networkMode = self["HostConfig"]?["NetworkMode"]?.GetValue<string>() ?? "bridge";
        var helperNetwork = DockerContainerRecreation.IsSharedNamespace(networkMode)
            ? "host"
            : (self["NetworkSettings"]?["Networks"] as JsonObject)?.Select(entry => entry.Key).FirstOrDefault() ?? "bridge";

        // The helper is the RUNNING build - by id, else by the tag the check gave it; when even that is gone (an image
        // removed by hand), the new image has to do the job rather than nothing happening at all.
        var runningId = self["Image"]!.GetValue<string>();
        var rollbackReference = $"{ImageReference.Parse(reference).Repository}:{RollbackTag}";
        var helperImage = await docker.InspectImageAsync(runningId, cancellationToken) is not null
            ? runningId
            : await docker.InspectImageAsync(rollbackReference, cancellationToken) is not null ? rollbackReference : reference;

        var arguments = new JsonArray
        {
            DockerUpdateHelper.Command,
            "--container", self["Id"]!.GetValue<string>(),
            "--image", reference,
            "--to", offer.Version ?? "unknown",
            "--from", currentVersion,
            "--socket", socketPath,
            "--version-variable", versionVariable
        };
        var body = new JsonObject
        {
            // The image this instance RUNS: the helper is the known-good build (see DockerUpdateHelper).
            ["Image"] = helperImage,
            ["Entrypoint"] = self["Config"]?["Entrypoint"]?.DeepClone(),
            ["Cmd"] = arguments,
            ["Labels"] = new JsonObject { [DockerContainerRecreation.HelperForLabel] = name },
            ["HostConfig"] = new JsonObject
            {
                ["Binds"] = new JsonArray($"{socketMount}:{socketPath}"),
                ["NetworkMode"] = helperNetwork,
                ["AutoRemove"] = false
            }
        };

        var helperId = await docker.CreateContainerAsync($"{name}-updater-{DateTime.UtcNow:yyyyMMddHHmmss}", body, cancellationToken);
        await docker.StartAsync(helperId, cancellationToken);
    }

    /// <summary>
    /// Reads how the last update ended from the helper container it left behind, and removes the helper. A helper that
    /// is still running means an update is under way right now - nothing to report yet.
    /// </summary>
    public async Task<SelfUpdateOutcome?> ReadLastOutcomeAsync(CancellationToken cancellationToken)
    {
        using var docker = Client();
        var self = await SelfAsync(docker, cancellationToken);
        var name = self["Name"]!.GetValue<string>().TrimStart('/');
        var helpers = await docker.ListContainersByLabelAsync($"{DockerContainerRecreation.HelperForLabel}={name}", cancellationToken);

        SelfUpdateOutcome? latest = null;
        foreach (var helper in helpers.OfType<JsonObject>())
        {
            if (helper["State"]?.GetValue<string>() is "running" or "created" or "restarting")
            {
                continue;
            }

            var id = helper["Id"]!.GetValue<string>();
            var outcome = DockerUpdateHelper.ParseOutcome(await docker.ReadLogsAsync(id, 200, cancellationToken))
                ?? new SelfUpdateOutcome(SelfUpdateOutcomeKind.Failed, null, null,
                    DateTimeOffset.FromUnixTimeSeconds(helper["Created"]?.GetValue<long>() ?? 0), "the updater ended without saying how");
            if (latest is null || outcome.AtUtc > latest.AtUtc)
            {
                latest = outcome;
            }

            await docker.RemoveAsync(id, force: true, cancellationToken);
        }

        return latest;
    }

    private DockerEngineClient Client() => new(socketPath, registryUsername, registryPassword);

    private async Task<JsonObject> SelfAsync(DockerEngineClient docker, CancellationToken cancellationToken)
    {
        _containerId ??= FindOwnContainerId() ?? throw new InvalidOperationException("this process does not run in a container it can find");
        return await docker.InspectContainerAsync(_containerId, cancellationToken)
            ?? throw new InvalidOperationException($"the engine does not know this container ({_containerId}) - is the socket that of another Docker?");
    }

    private static List<string> RepoDigests(JsonObject? image) =>
        (image?["RepoDigests"] as JsonArray)?.Select(node => node?.GetValue<string>()).OfType<string>().ToList() ?? [];

    /// <summary>
    /// This container's id: the engine bind-mounts /etc/hostname, /etc/hosts and /etc/resolv.conf from
    /// <c>…/containers/&lt;id&gt;/</c>, which mountinfo shows whatever the container's host name was set to. The host
    /// name (a short id unless set otherwise) is the fallback.
    /// </summary>
    internal static string? FindOwnContainerId(string? mountInfo = null, string? hostname = null)
    {
        try
        {
            mountInfo ??= File.Exists("/proc/self/mountinfo") ? File.ReadAllText("/proc/self/mountinfo") : string.Empty;
        }
        catch (IOException)
        {
            mountInfo = string.Empty;
        }

        var match = ContainerPathPattern().Match(mountInfo);
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        hostname ??= Environment.GetEnvironmentVariable("HOSTNAME");
        return hostname is { Length: >= 12 } && ShortIdPattern().IsMatch(hostname) ? hostname : null;
    }

    [GeneratedRegex("/containers/([0-9a-f]{64})/")]
    private static partial Regex ContainerPathPattern();

    [GeneratedRegex("^[0-9a-f]{12,64}$")]
    private static partial Regex ShortIdPattern();
}
