using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Matmon.Host.Services.SelfUpdate;

/// <summary>
/// <c>Matmon.Host apply-update --install … --staged … --to … --from … --state … --service … --health …</c>
///
/// The Windows instance's updater - the agent's <c>apply-update</c> for a program FOLDER instead of one executable. It
/// runs from a copy of the running build (<c>&lt;install&gt;.updater</c>), outside the service's lifetime: stops the
/// service, moves the program folder aside (<c>.bak-&lt;time&gt;</c>), moves the new one in, starts the service and waits
/// for <c>/healthz</c> to answer with the new version. No answer within <see cref="HealthTimeout"/>, or any failure after
/// the swap: the old folder goes back and the old build is started. The data (ProgramData) is never touched - the
/// instance took a snapshot of its configuration before handing over.
/// </summary>
public static class WindowsUpdateApplier
{
    public const string Command = "apply-update";
    public static readonly TimeSpan HealthTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan ServiceTimeout = TimeSpan.FromSeconds(90);

    public static async Task<int> RunAsync(string[] args)
    {
        var install = Option(args, "--install");
        var staged = Option(args, "--staged");
        var to = Option(args, "--to");
        var from = Option(args, "--from") ?? "unknown";
        var state = Option(args, "--state");
        var service = Option(args, "--service");
        var health = Option(args, "--health");
        if (install is null || staged is null || to is null || state is null || service is null || health is null)
        {
            Console.Error.WriteLine("apply-update is started by Matmon itself; it is not meant to be run by hand.");
            return 2;
        }

        var files = new WindowsUpdateFiles(state);
        var backup = $"{install}.bak-{DateTime.UtcNow:yyyyMMddHHmmss}";
        files.Log($"update {from} -> {to}: starting (service '{service}')");
        files.MarkPending(to, from);

        var swapped = false;
        try
        {
            ServiceControl.Stop(service, ServiceTimeout);
            files.Log("service stopped");

            // "Stopped" and "every file released" are not the same instant on Windows - retry the moves for a while.
            await RetryAsync(() => Directory.Move(install, backup));
            swapped = true;
            await RetryAsync(() => Directory.Move(staged, install));
            files.Log("program folder swapped, starting the new build");

            ServiceControl.Start(service, ServiceTimeout);
            var problem = await WaitHealthyAsync(health, to);
            if (problem is null)
            {
                Finish(files, new SelfUpdateOutcome(SelfUpdateOutcomeKind.Updated, from, to, DateTimeOffset.UtcNow, null, to));
                return 0;
            }

            return await RollBackAsync(files, service, install, backup, from, to, problem);
        }
        catch (Exception ex)
        {
            files.Log($"update failed: {ex.Message}");
            if (swapped)
            {
                return await RollBackAsync(files, service, install, backup, from, to, ex.Message);
            }

            // Nothing was replaced - make sure the old build runs again. The outcome is written BEFORE the start, so the
            // build that comes up reads it (the agent learned that the hard way).
            Finish(files, new SelfUpdateOutcome(SelfUpdateOutcomeKind.Failed, from, to, DateTimeOffset.UtcNow, ex.Message, to));
            TryStart(files, service);
            return 1;
        }
    }

    private static async Task<int> RollBackAsync(WindowsUpdateFiles files, string service, string install, string backup, string from, string to, string reason)
    {
        files.Log($"rolling back to {from}: {reason}");
        try
        {
            try
            {
                ServiceControl.Stop(service, ServiceTimeout);
            }
            catch (Exception ex)
            {
                files.Log($"stopping the new build failed ({ex.Message}); swapping back anyway");
            }

            if (Directory.Exists(install))
            {
                await RetryAsync(() => Directory.Move(install, $"{install}.failed-{DateTime.UtcNow:yyyyMMddHHmmss}"));
            }

            await RetryAsync(() => Directory.Move(backup, install));
            Finish(files, new SelfUpdateOutcome(SelfUpdateOutcomeKind.RolledBack, from, to, DateTimeOffset.UtcNow, reason, to));
            TryStart(files, service);
            files.Log("rolled back");
        }
        catch (Exception ex)
        {
            files.Log($"ROLLBACK FAILED: {ex.Message} - move {backup} back to {install} by hand and start '{service}'");
            Finish(files, new SelfUpdateOutcome(SelfUpdateOutcomeKind.RolledBack, from, to, DateTimeOffset.UtcNow,
                $"{reason}; rollback failed: {ex.Message} - move {backup} back to {install} by hand", to));
        }

        return 1;
    }

    private static async Task<string?> WaitHealthyAsync(string healthUrl, string expectedVersion)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTimeOffset.UtcNow + HealthTimeout;
        var lastProblem = "it did not answer";
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var health = await http.GetFromJsonAsync<JsonObject>(healthUrl);
                var version = health?["version"]?.GetValue<string>();
                if (string.Equals(version, expectedVersion, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                lastProblem = $"it reports version {version ?? "(none)"}, expected {expectedVersion}";
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                lastProblem = $"it did not answer {healthUrl}";
            }

            await Task.Delay(TimeSpan.FromSeconds(3));
        }

        return $"the new build did not come up within {HealthTimeout.TotalMinutes:0} minutes ({lastProblem})";
    }

    private static void Finish(WindowsUpdateFiles files, SelfUpdateOutcome outcome)
    {
        files.WriteOutcome(outcome);
        files.ClearPending();
        files.Log($"{outcome.Kind}: {outcome.FromVersion} -> {outcome.ToVersion}{(outcome.Detail is null ? string.Empty : " - " + outcome.Detail)}");
    }

    private static void TryStart(WindowsUpdateFiles files, string service)
    {
        try
        {
            ServiceControl.Start(service, ServiceTimeout);
        }
        catch (Exception ex)
        {
            files.Log($"starting '{service}' failed: {ex.Message}");
        }
    }

    private static async Task RetryAsync(Action action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 30)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }
    }

    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
