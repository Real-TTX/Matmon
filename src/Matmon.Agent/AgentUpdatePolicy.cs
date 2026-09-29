using System.Text.Json;

namespace Matmon.Agent;

/// <summary>
/// Whether the agent should update to the version its instance offers - pure, so the rules are tested
/// rather than discovered on a customer's machine.
///
/// The instance is the authority: the agent follows the instance's version EXACTLY, up or down. Versions are
/// build labels ("0.1.42-20260929-1200", "nightly-310-..."), not something to order - and an agent that is
/// "newer" than its instance is just as mismatched as one that is older.
/// </summary>
public static class AgentUpdatePolicy
{
    /// <summary>After a rollback, the same version is not tried again for this long - a build that broke
    /// once breaks again, and an hourly update/rollback loop would take the probe offline every hour.</summary>
    public static readonly TimeSpan RetryAfterRollback = TimeSpan.FromHours(24);

    public static AgentUpdateDecision Decide(
        string currentVersion,
        string? offeredVersion,
        bool packageAvailable,
        AgentUpdateRecord? lastAttempt,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(offeredVersion))
        {
            return AgentUpdateDecision.Skip("the instance offers no version");
        }

        if (string.Equals(currentVersion, offeredVersion, StringComparison.OrdinalIgnoreCase))
        {
            return AgentUpdateDecision.Skip("up to date");
        }

        // A plain local build of the instance labels itself with its build time, so every rebuild would look
        // like a new release and every developer's agent would chase it.
        if (offeredVersion.StartsWith("local-", StringComparison.OrdinalIgnoreCase))
        {
            return AgentUpdateDecision.Skip($"the instance runs an unversioned local build ({offeredVersion})");
        }

        if (!packageAvailable)
        {
            return AgentUpdateDecision.Skip($"the instance carries no agent package for this platform ({offeredVersion})");
        }

        if (lastAttempt is { Outcome: AgentUpdateOutcome.RolledBack or AgentUpdateOutcome.Failed } failed &&
            string.Equals(failed.Version, offeredVersion, StringComparison.OrdinalIgnoreCase) &&
            now - failed.FinishedUtc < RetryAfterRollback)
        {
            return AgentUpdateDecision.Skip(
                $"{offeredVersion} was rolled back at {failed.FinishedUtc:u}; retrying after {failed.FinishedUtc + RetryAfterRollback:u}");
        }

        return AgentUpdateDecision.Apply(offeredVersion);
    }

    /// <summary>What the agent reports on its heartbeat about the last update - shown on the Agents page.</summary>
    public static string? Describe(AgentUpdateRecord? record) => record switch
    {
        null => null,
        { Outcome: AgentUpdateOutcome.Updated } => $"updated from {record.FromVersion} at {record.FinishedUtc:u}",
        { Outcome: AgentUpdateOutcome.RolledBack } => $"update to {record.Version} rolled back at {record.FinishedUtc:u}: {record.Reason}",
        _ => $"update to {record.Version} failed at {record.FinishedUtc:u}: {record.Reason}"
    };
}

public sealed record AgentUpdateDecision(bool ShouldUpdate, string? Version, string Reason)
{
    public static AgentUpdateDecision Skip(string reason) => new(false, null, reason);

    public static AgentUpdateDecision Apply(string version) => new(true, version, $"updating to {version}");
}

public enum AgentUpdateOutcome
{
    Updated,
    RolledBack,
    Failed
}

/// <summary>The outcome of the last update, written by the updater (<c>last-update.json</c>).</summary>
public sealed record AgentUpdateRecord(
    string Version,
    string FromVersion,
    AgentUpdateOutcome Outcome,
    string? Reason,
    DateTimeOffset FinishedUtc);

/// <summary>Written by the updater before it starts the new build; the new build answers with
/// <c>confirmed</c> once it has reached its primary (see <see cref="AgentUpdateFiles"/>).</summary>
public sealed record AgentUpdatePending(string Version, string FromVersion, DateTimeOffset StartedUtc);

/// <summary>
/// The hand-shake between the running agent, the updater and the new build - plain files in the agent's state
/// directory, because the three are separate processes and the middle one outlives the first.
/// </summary>
public sealed class AgentUpdateFiles(string stateDirectory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public string Directory { get; } = Path.Combine(stateDirectory, "update");

    public string PendingPath => Path.Combine(Directory, "pending.json");

    public string ConfirmedPath => Path.Combine(Directory, "confirmed");

    public string LastUpdatePath => Path.Combine(Directory, "last-update.json");

    public string LogPath => Path.Combine(Directory, "update.log");

    public AgentUpdatePending? ReadPending() => Read<AgentUpdatePending>(PendingPath);

    public AgentUpdateRecord? ReadLastUpdate() => Read<AgentUpdateRecord>(LastUpdatePath);

    public void WritePending(AgentUpdatePending pending) => Write(PendingPath, pending);

    public void WriteLastUpdate(AgentUpdateRecord record) => Write(LastUpdatePath, record);

    public void Confirm(string version)
    {
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(ConfirmedPath, version);
    }

    public bool IsConfirmed(string version) =>
        File.Exists(ConfirmedPath) &&
        string.Equals(File.ReadAllText(ConfirmedPath).Trim(), version, StringComparison.OrdinalIgnoreCase);

    public void Log(string message)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.AppendAllText(LogPath, $"{DateTimeOffset.UtcNow:u} {message}{Environment.NewLine}");
        }
        catch
        {
            // The log is for support; failing to write it must never fail the update.
        }
    }

    public static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }

    private static T? Read<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : null;
        }
        catch
        {
            return null;
        }
    }

    private void Write<T>(string path, T value)
    {
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(path, JsonSerializer.Serialize(value, Json));
    }
}
