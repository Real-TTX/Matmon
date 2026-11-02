using System.Security.Cryptography;
using System.Text;

namespace Matmon.Core.Domain;

/// <summary>
/// A rollout key: one secret for MANY machines - what goes into an MSI deployment (Intune, a GPO, an RMM tool) or a
/// script that installs the agent on a hundred PCs, where a single-use code per machine is impossible. Every machine that
/// enrols with it lands in the admin's approval list on the Agents page; only an approved one becomes a host (in
/// <see cref="ParentElementId"/>) and is monitored. Why approval: a key that sits in a GPO or a deployment command line is
/// read sooner or later, and without it anyone holding it could put machines of their own into the tree.
///
/// Stored only as a hash (<see cref="KeyHash"/>); the key is shown once. Revoking it stops NEW machines; agents already
/// approved keep their own identity.
/// </summary>
public sealed class AgentRolloutKey
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    /// <summary>SHA-256 of the key. The key itself is shown once and never stored.</summary>
    public string KeyHash { get; set; } = string.Empty;

    /// <summary>Where approved machines become hosts: a probe or a folder. Null = directly under the primary.</summary>
    public Guid? ParentElementId { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public string? CreatedBy { get; set; }

    /// <summary>Null = until revoked.</summary>
    public DateTimeOffset? ExpiresUtc { get; set; }

    /// <summary>How many machines may enrol with it (pending or approved; a machine enrolling again does not count twice). Null = no limit.</summary>
    public int? MaxDevices { get; set; }

    /// <summary>How many machines enrolled with it so far.</summary>
    public int DeviceCount { get; set; }

    public DateTimeOffset? LastUsedUtc { get; set; }

    public DateTimeOffset? RevokedUtc { get; set; }

    public bool IsUsable(DateTimeOffset now) =>
        RevokedUtc is null && (ExpiresUtc is not { } expires || now < expires) && (MaxDevices is not { } max || DeviceCount < max);

    public AgentRolloutKey Clone() => (AgentRolloutKey)MemberwiseClone();
}

/// <summary>
/// A machine that enrolled with a rollout key and waits for an admin. It already holds its identity (it was answered with
/// an id and a token and runs) - the instance just does not accept that identity until the machine is approved: its
/// heartbeat is answered "waiting for approval", everything else 401. Approving makes it a host with exactly that
/// identity, so the agent carries on without anything to do on the machine; rejecting forgets it (the machine then stays
/// locked out for good).
/// </summary>
public sealed class PendingAgent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid RolloutKeyId { get; set; }

    /// <summary>The id the agent reports under - reserved: unique among probes, agents and other pending machines.</summary>
    public string ProbeId { get; set; } = string.Empty;

    public string Token { get; set; } = string.Empty;

    /// <summary>The machine's host name as it reported it.</summary>
    public string? MachineName { get; set; }

    /// <summary>A hash of the machine's own id (<see cref="AgentMachineId"/>) - recognises a machine that enrols again.</summary>
    public string? MachineId { get; set; }

    public string? OperatingSystem { get; set; }

    public string? AgentVersion { get; set; }

    /// <summary>The address the enrolment came from (a relaying probe's when it came through one).</summary>
    public string? RemoteAddress { get; set; }

    public DateTimeOffset RequestedUtc { get; set; }

    /// <summary>The agent host with the same machine id, when there is one - approving re-enrols it instead of making a new host.</summary>
    public Guid? ReplacesHostId { get; set; }

    public PendingAgent Clone() => (PendingAgent)MemberwiseClone();
}

/// <summary>Generation and hashing of rollout keys - pure, so it is unit-tested.</summary>
public static class AgentRolloutKeySecret
{
    /// <summary>Every rollout key starts with this: the agent tells it from a single-use code by it, and so does a reader.</summary>
    public const string Prefix = "mmr_";

    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
    private const int Length = 40;

    /// <summary>A fresh key: <c>mmr_</c> + 40 base62 symbols (~238 bits).</summary>
    public static string Generate()
    {
        var builder = new StringBuilder(Prefix, Prefix.Length + Length);
        for (var index = 0; index < Length; index++)
        {
            builder.Append(Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]);
        }

        return builder.ToString();
    }

    /// <summary>Whether a value is SHAPED like a rollout key (prefix + 40 base62) - also what an install script may contain.</summary>
    public static bool IsKey(string? value)
    {
        var trimmed = value?.Trim();
        return trimmed is not null && trimmed.Length == Prefix.Length + Length
            && trimmed.StartsWith(Prefix, StringComparison.Ordinal)
            && trimmed[Prefix.Length..].All(symbol => Alphabet.Contains(symbol));
    }

    /// <summary>The key in its exact form, or null when the value is no key.</summary>
    public static string? Canonical(string? value) => IsKey(value) ? value!.Trim() : null;

    public static string Hash(string? key) =>
        IsKey(key) ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key!.Trim()))) : string.Empty;
}

/// <summary>The machine id an agent reports - hashed, so the raw id of the machine never leaves it.</summary>
public static class AgentMachineId
{
    /// <summary>A stable, non-reversible form of a machine's own id (Windows MachineGuid, /etc/machine-id, IOPlatformUUID).</summary>
    public static string? FromRaw(string? raw)
    {
        var trimmed = raw?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(trimmed)
            ? null
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("matmon-machine|" + trimmed)))[..32];
    }

    /// <summary>A reported value accepted as a machine id: 32 hex characters, else nothing.</summary>
    public static string? Clean(string? reported)
    {
        var trimmed = reported?.Trim().ToUpperInvariant();
        return trimmed is { Length: 32 } && trimmed.All(Uri.IsHexDigit) ? trimmed : null;
    }
}
