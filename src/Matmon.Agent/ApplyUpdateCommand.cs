namespace Matmon.Agent;

/// <summary>
/// <c>matmon-agent apply-update --target … --staged … --version … --from … --state … --service …</c>
///
/// The updater. It runs from a COPY of the agent build that was working (never from the new one), outside the
/// service's lifetime, so it can do what the running service cannot: stop it, swap the executable, start the
/// new build - and put the old one back when the new build does not reach its primary within
/// <see cref="ConfirmationTimeout"/>. "Reached its primary" is the new build's own word for it (the
/// <c>confirmed</c> file, written after its first successful heartbeat): a process that starts and then
/// cannot talk to anyone is not a working agent.
/// </summary>
public static class ApplyUpdateCommand
{
    public static readonly TimeSpan ConfirmationTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan ServiceTimeout = TimeSpan.FromSeconds(90);

    public static async Task<int> RunAsync(string[] args)
    {
        var target = Option(args, "--target");
        var staged = Option(args, "--staged");
        var version = Option(args, "--version");
        var from = Option(args, "--from") ?? "unknown";
        var state = Option(args, "--state");
        var service = Option(args, "--service");
        if (target is null || staged is null || version is null || state is null || service is null)
        {
            Console.Error.WriteLine("apply-update is started by the agent itself; it is not meant to be run by hand.");
            return 2;
        }

        var files = new AgentUpdateFiles(state);
        var backup = target + ".bak";
        files.Log($"update {from} -> {version}: starting (service '{service}')");
        AgentUpdateFiles.TryDelete(files.ConfirmedPath);
        files.WritePending(new AgentUpdatePending(version, from, DateTimeOffset.UtcNow));

        var swapped = false;
        try
        {
            AgentServiceControl.Stop(service, ServiceTimeout);
            files.Log("service stopped");

            // The service reporting "stopped" and its process letting go of the executable are not the same
            // instant on Windows - retry the swap for a while rather than failing on the first sharing error.
            await RetryAsync(() =>
            {
                File.Copy(target, backup, overwrite: true);
                File.Move(staged, target, overwrite: true);
            });
            swapped = true;
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(target, (UnixFileMode)0b111_101_101);
            }

            files.Log("executable swapped, starting the new build");
            AgentServiceControl.Start(service, ServiceTimeout);

            var deadline = DateTimeOffset.UtcNow + ConfirmationTimeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (files.IsConfirmed(version))
                {
                    files.WriteLastUpdate(new AgentUpdateRecord(version, from, AgentUpdateOutcome.Updated, null, DateTimeOffset.UtcNow));
                    AgentUpdateFiles.TryDelete(files.PendingPath);
                    files.Log($"update {from} -> {version}: confirmed by the new build");
                    return 0;
                }

                await Task.Delay(TimeSpan.FromSeconds(2));
            }

            return await RollBackAsync(files, service, target, backup, version, from,
                $"the new build did not reach its primary within {ConfirmationTimeout.TotalMinutes:0} minutes");
        }
        catch (Exception ex)
        {
            files.Log($"update failed: {ex.Message}");
            if (swapped)
            {
                return await RollBackAsync(files, service, target, backup, version, from, ex.Message);
            }

            // Nothing was replaced - just make sure the old build is running again.
            TryStart(files, service);
            files.WriteLastUpdate(new AgentUpdateRecord(version, from, AgentUpdateOutcome.Failed, ex.Message, DateTimeOffset.UtcNow));
            AgentUpdateFiles.TryDelete(files.PendingPath);
            AgentUpdateFiles.TryDelete(staged);
            return 1;
        }
    }

    private static async Task<int> RollBackAsync(
        AgentUpdateFiles files, string service, string target, string backup, string version, string from, string reason)
    {
        files.Log($"rolling back to {from}: {reason}");
        try
        {
            try
            {
                AgentServiceControl.Stop(service, ServiceTimeout);
            }
            catch (Exception ex)
            {
                files.Log($"stopping the new build failed ({ex.Message}); swapping anyway");
            }

            await RetryAsync(() => File.Copy(backup, target, overwrite: true));
            TryStart(files, service);
            files.Log("rolled back");
        }
        catch (Exception ex)
        {
            // The worst case: neither build is running. Said loudly in the log, which is where support looks.
            files.Log($"ROLLBACK FAILED: {ex.Message} - restore {backup} to {target} by hand and start '{service}'");
        }

        files.WriteLastUpdate(new AgentUpdateRecord(version, from, AgentUpdateOutcome.RolledBack, reason, DateTimeOffset.UtcNow));
        AgentUpdateFiles.TryDelete(files.PendingPath);
        return 1;
    }

    private static void TryStart(AgentUpdateFiles files, string service)
    {
        try
        {
            AgentServiceControl.Start(service, ServiceTimeout);
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
            catch (IOException) when (attempt < 30)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
            catch (UnauthorizedAccessException) when (attempt < 30)
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
