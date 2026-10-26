using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Matmon.Host.Services.SelfUpdate;

/// <summary>
/// <c>Matmon.Host docker-update-helper --container … --image … --to … --from …</c>
///
/// Runs in a short-lived container of its own, started by the instance from the image the instance RUNS - the build
/// known to work, so the thing doing a rollback is the thing that is known to work (the agent's updater follows the
/// same rule). A container cannot replace itself: stopping it ends the process that would start the new one. The
/// helper renames the old container aside, creates the new one under the old name from the old one's own settings
/// (<see cref="DockerContainerRecreation"/>), stops the old one, starts the new one and waits for its <c>/healthz</c>
/// to answer with the new version. Then the old container is removed - or, when the new one does not come up within
/// <see cref="HealthTimeout"/>, the new one is removed and the old one is started again under its own name.
///
/// The outcome is the LAST line of the helper's output (<see cref="OutcomePrefix"/> + JSON); the instance reads it
/// from the helper's logs and then removes the helper.
/// </summary>
public static class DockerUpdateHelper
{
    public const string Command = "docker-update-helper";
    public const string OutcomePrefix = "MATMON-UPDATE-OUTCOME ";
    public static readonly TimeSpan HealthTimeout = TimeSpan.FromMinutes(3);
    private const int StopTimeoutSeconds = 30;

    public static async Task<int> RunAsync(string[] args)
    {
        var containerId = Option(args, "--container");
        var image = Option(args, "--image");
        var to = Option(args, "--to") ?? "unknown";
        var from = Option(args, "--from") ?? "unknown";
        var socket = Option(args, "--socket") ?? DockerEngineClient.DefaultSocketPath;
        var versionVariable = Option(args, "--version-variable");
        if (containerId is null || image is null)
        {
            Console.Error.WriteLine("docker-update-helper is started by Matmon itself; it is not meant to be run by hand.");
            return 2;
        }

        using var docker = new DockerEngineClient(socket);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var token = cancellation.Token;
        string? identity = null;

        var old = await docker.InspectContainerAsync(containerId, token);
        if (old is null)
        {
            return Finish(SelfUpdateOutcomeKind.Failed, from, to, "the container to update no longer exists", null);
        }

        var oldId = old["Id"]!.GetValue<string>();
        var newImage = await docker.InspectImageAsync(image, token);
        // The old image can be gone (its tag moved on): then the new image's defaults are the best guess at what was not
        // the operator's own setting.
        var oldImage = await docker.InspectImageAsync(old["Image"]!.GetValue<string>(), token) ?? newImage;
        if (newImage is null)
        {
            return Finish(SelfUpdateOutcomeKind.Failed, from, to, $"the image {image} is not there (was it pulled?)", null);
        }

        identity = newImage["Id"]?.GetValue<string>();
        var labels = new Dictionary<string, string>
        {
            [DockerContainerRecreation.FromVersionLabel] = from,
            [DockerContainerRecreation.UpdatedAtLabel] = DateTimeOffset.UtcNow.ToString("O")
        };
        var plan = DockerContainerRecreation.Build(old, oldImage, image, labels, versionVariable is null ? null : [versionVariable]);
        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        var asideName = $"{plan.Name}-old-{stamp}";
        Log($"updating {plan.Name} {from} -> {to} ({image})");

        // 1. Make room for the name. Nothing has changed for the running instance yet.
        await docker.RenameAsync(oldId, asideName, token);
        string newId;
        try
        {
            newId = await docker.CreateContainerAsync(plan.Name, plan.CreateBody, token);
            foreach (var (network, endpoint) in plan.ExtraNetworks)
            {
                await docker.ConnectNetworkAsync(network, newId, endpoint, token);
            }
        }
        catch (Exception ex)
        {
            await docker.RenameAsync(oldId, plan.Name, CancellationToken.None);
            return Finish(SelfUpdateOutcomeKind.Failed, from, to, $"the new container could not be created: {ex.Message}", identity);
        }

        // 2. Swap. From here on a failure means: put the old container back.
        string? failure;
        try
        {
            await docker.StopAsync(oldId, StopTimeoutSeconds, token);
            Log("old container stopped, starting the new one");
            await docker.StartAsync(newId, token);
            failure = await WaitHealthyAsync(docker, newId, old, to, token);
        }
        catch (Exception ex)
        {
            failure = ex.Message;
        }

        if (failure is null)
        {
            Log("the new container answers - removing the old one");
            await TryAsync(() => docker.RemoveAsync(oldId, force: true, CancellationToken.None));
            return Finish(SelfUpdateOutcomeKind.Updated, from, to, null, identity);
        }

        // 3. Roll back: what the new container said last goes into the outcome, then it goes.
        Log($"rolling back: {failure}");
        var lastWords = await TryReadLogsAsync(docker, newId);
        await TryAsync(() => docker.StopAsync(newId, 10, CancellationToken.None));
        await TryAsync(() => docker.RemoveAsync(newId, force: true, CancellationToken.None));
        try
        {
            await docker.RenameAsync(oldId, plan.Name, CancellationToken.None);
            await docker.StartAsync(oldId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The worst case: neither is running. Said in words, which is all an admin has to go on.
            return Finish(SelfUpdateOutcomeKind.RolledBack, from, to,
                $"{failure}; ROLLBACK FAILED: {ex.Message} - start the container '{asideName}' by hand", identity);
        }

        var detail = string.IsNullOrWhiteSpace(lastWords) ? failure : $"{failure}. Last output: {Tail(lastWords, 600)}";
        return Finish(SelfUpdateOutcomeKind.RolledBack, from, to, detail, identity);
    }

    private static async Task<string?> WaitHealthyAsync(DockerEngineClient docker, string newId, JsonObject old, string expectedVersion, CancellationToken token)
    {
        var port = DockerContainerRecreation.ListenPort(old);
        var networkMode = old["HostConfig"]?["NetworkMode"]?.GetValue<string>() ?? "default";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTimeOffset.UtcNow + HealthTimeout;
        string lastProblem = "it did not answer";
        while (DateTimeOffset.UtcNow < deadline)
        {
            var current = await docker.InspectContainerAsync(newId, token);
            var state = current?["State"];
            var exitCode = state?["ExitCode"]?.GetValue<int>().ToString() ?? "?";
            if (state?["Running"]?.GetValue<bool>() != true)
            {
                return $"the new container stopped (exit code {exitCode})";
            }

            // With a restart policy a crashing container counts as "running" between its restarts - waiting out the
            // whole timeout for one that has already died once only delays the rollback.
            if (state["Restarting"]?.GetValue<bool>() == true || current!["RestartCount"]?.GetValue<int>() > 0)
            {
                return $"the new container keeps restarting (exit code {exitCode})";
            }

            var address = DockerContainerRecreation.IsSharedNamespace(networkMode)
                ? "127.0.0.1"
                : (current["NetworkSettings"]?["Networks"] as JsonObject)?
                    .Select(network => network.Value?["IPAddress"]?.GetValue<string>())
                    .FirstOrDefault(ip => !string.IsNullOrWhiteSpace(ip));
            if (address is not null)
            {
                try
                {
                    var health = await http.GetFromJsonAsync<JsonObject>($"http://{address}:{port}/healthz", token);
                    var version = health?["version"]?.GetValue<string>();
                    if (version is null || string.Equals(version, expectedVersion, StringComparison.OrdinalIgnoreCase) || expectedVersion == "unknown")
                    {
                        return null;
                    }

                    lastProblem = $"it reports version {version}, expected {expectedVersion}";
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
                {
                    lastProblem = "it did not answer /healthz";
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(3), token);
        }

        return $"the new container did not come up within {HealthTimeout.TotalMinutes:0} minutes ({lastProblem})";
    }

    private static int Finish(SelfUpdateOutcomeKind kind, string from, string to, string? detail, string? identity)
    {
        var outcome = new SelfUpdateOutcome(kind, from, to, DateTimeOffset.UtcNow, detail, identity);
        Log($"{kind}: {detail ?? "done"}");
        Console.WriteLine(OutcomePrefix + JsonSerializer.Serialize(outcome, OutcomeJson.Options));
        return kind == SelfUpdateOutcomeKind.Updated ? 0 : 1;
    }

    /// <summary>The outcome line in a helper's output, or null when there is none (the helper did not get that far).</summary>
    public static SelfUpdateOutcome? ParseOutcome(string logs)
    {
        var line = logs.Split('\n').Select(text => text.Trim()).LastOrDefault(text => text.StartsWith(OutcomePrefix, StringComparison.Ordinal));
        if (line is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SelfUpdateOutcome>(line[OutcomePrefix.Length..], OutcomeJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task TryAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Log($"(ignored) {ex.Message}");
        }
    }

    private static async Task<string?> TryReadLogsAsync(DockerEngineClient docker, string containerId)
    {
        try
        {
            // A container that restarted prints the same lines again and again - each distinct line once, in order.
            var lines = (await docker.ReadLogsAsync(containerId, 40, CancellationToken.None))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct()
                .TakeLast(8);
            return string.Join(" / ", lines);
        }
        catch (Exception ex)
        {
            Log($"(ignored) {ex.Message}");
            return null;
        }
    }

    private static string Tail(string text, int length) => text.Length <= length ? text : "…" + text[^length..];

    private static void Log(string message) => Console.WriteLine($"{DateTimeOffset.UtcNow:u} {message}");

    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}

/// <summary>The one JSON shape an outcome travels in (helper output, the Windows updater's file, the workspace).</summary>
public static class OutcomeJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };
}
