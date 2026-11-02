using Matmon.Core.Domain;

namespace Matmon.Host.Services;

// Agent rollout: one key for many machines (an MSI pushed by Intune / a GPO, a script on a hundred PCs). A machine that
// enrols with it is answered with an identity at once but waits in the approval list until an admin approves it - only
// then is it a host and monitored. See AgentRolloutKey for why every machine is approved.
public sealed partial class InMemoryMonitoringWorkspaceStore
{
    /// <summary>More waiting machines than this and a key is not taken any more - a leaked key must not flood the workspace.</summary>
    public const int MaxPendingAgents = 1000;

    /// <summary>A machine nobody approved or rejected for this long is forgotten (it stays locked out).</summary>
    public static readonly TimeSpan PendingAgentLifetime = TimeSpan.FromDays(30);

    public AgentRolloutKeyIssue CreateAgentRolloutKey(string name, Guid? parentElementId, DateTimeOffset? expiresUtc, int? maxDevices, string? createdBy)
    {
        var now = DateTimeOffset.UtcNow;
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("Give the rollout key a name - e.g. the site or the deployment it is for.");
        }

        if (expiresUtc is { } expires && expires <= now)
        {
            throw new InvalidOperationException("The expiry must be in the future.");
        }

        if (maxDevices is < 1)
        {
            throw new InvalidOperationException("The number of machines must be at least 1 - or empty for no limit.");
        }

        var secret = AgentRolloutKeySecret.Generate();
        var key = new AgentRolloutKey
        {
            Name = name.Trim(),
            KeyHash = AgentRolloutKeySecret.Hash(secret),
            CreatedUtc = now,
            CreatedBy = string.IsNullOrWhiteSpace(createdBy) ? null : createdBy.Trim(),
            ExpiresUtc = expiresUtc,
            MaxDevices = maxDevices
        };

        lock (_gate)
        {
            if (parentElementId is Guid parentId && parentId != Guid.Empty)
            {
                if (FindElement(parentId) is not (ProbeElement or FolderElement))
                {
                    throw new InvalidOperationException("Choose a probe or a folder for the machines.");
                }

                key.ParentElementId = parentId;
            }

            (_document.AgentRolloutKeys ??= []).Add(key);
            QueueSave(SavePriority.Configuration);
        }

        return new AgentRolloutKeyIssue(key.Clone(), secret);
    }

    public IReadOnlyList<AgentRolloutKey> GetAgentRolloutKeys()
    {
        lock (_gate)
        {
            return (_document.AgentRolloutKeys ??= []).OrderByDescending(key => key.CreatedUtc).Select(key => key.Clone()).ToArray();
        }
    }

    public bool RevokeAgentRolloutKey(Guid keyId)
    {
        lock (_gate)
        {
            if ((_document.AgentRolloutKeys ??= []).FirstOrDefault(key => key.Id == keyId) is not { RevokedUtc: null } key)
            {
                return false;
            }

            key.RevokedUtc = DateTimeOffset.UtcNow;
            QueueSave(SavePriority.Configuration);
            return true;
        }
    }

    /// <summary>Removes a key from the list - only a revoked or expired one, so a key in use is never removed by accident.</summary>
    public bool DeleteAgentRolloutKey(Guid keyId)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            var removed = (_document.AgentRolloutKeys ??= []).RemoveAll(key =>
                key.Id == keyId && (key.RevokedUtc is not null || key.ExpiresUtc is { } expires && expires <= now));
            if (removed == 0)
            {
                return false;
            }

            QueueSave(SavePriority.Configuration);
            return true;
        }
    }

    /// <summary>Whether a rollout key is usable right now (not consumed by asking) - lets an install script download the agent with it.</summary>
    public bool IsAgentRolloutKeyValid(string? secret)
    {
        var hash = AgentRolloutKeySecret.Hash(secret);
        if (hash.Length == 0)
        {
            return false;
        }

        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            return (_document.AgentRolloutKeys ??= []).Any(key => key.RevokedUtc is null
                && (key.ExpiresUtc is not { } expires || now < expires)
                && string.Equals(key.KeyHash, hash, StringComparison.Ordinal));
        }
    }

    public AgentRolloutKey? FindAgentRolloutKey(string? secret)
    {
        var hash = AgentRolloutKeySecret.Hash(secret);
        if (hash.Length == 0)
        {
            return null;
        }

        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            return (_document.AgentRolloutKeys ??= []).FirstOrDefault(key => key.RevokedUtc is null
                && (key.ExpiresUtc is not { } expires || now < expires)
                && string.Equals(key.KeyHash, hash, StringComparison.Ordinal))?.Clone();
        }
    }

    public PendingAgent? EnrollWithRolloutKey(string? secret, string? hostName, string? operatingSystem, string? agentVersion, string? machineId, string? remoteAddress)
    {
        var hash = AgentRolloutKeySecret.Hash(secret);
        if (hash.Length == 0)
        {
            return null;
        }

        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            var key = (_document.AgentRolloutKeys ??= []).FirstOrDefault(candidate => string.Equals(candidate.KeyHash, hash, StringComparison.Ordinal));
            if (key is null || key.RevokedUtc is not null || key.ExpiresUtc is { } expires && now >= expires)
            {
                return null;
            }

            _document.PendingAgents ??= [];
            PrunePendingAgentsLocked(now);

            // The same machine again (a reinstall, the MSI pushed once more): its earlier request is replaced, and a machine
            // the tree already has as an agent is offered as a re-install of that host. Neither counts as another machine.
            var machine = AgentMachineId.Clean(machineId);
            var earlier = machine is null ? null : _document.PendingAgents.FirstOrDefault(pending => pending.MachineId == machine);
            var existingHost = machine is null
                ? null
                : EnumerateElements(_document.RootProbe).OfType<HostElement>().FirstOrDefault(host => host.Agent?.MachineId == machine);
            var known = earlier is not null || existingHost is not null;
            if (!known && !key.IsUsable(now))
            {
                return null;
            }

            if (earlier is null && _document.PendingAgents.Count >= MaxPendingAgents)
            {
                _logger.LogWarning("Rollout key {Key}: {Count} machines already wait for approval - refusing more", key.Name, MaxPendingAgents);
                return null;
            }

            if (earlier is not null)
            {
                _document.PendingAgents.Remove(earlier);
            }

            if (!known)
            {
                key.DeviceCount++;
            }

            key.LastUsedUtc = now;
            var name = string.IsNullOrWhiteSpace(hostName) ? null : hostName.Trim();
            var pending = new PendingAgent
            {
                RolloutKeyId = key.Id,
                ProbeId = GenerateUniqueProbeId(name ?? "agent"),
                Token = CreateToken(),
                MachineName = name,
                MachineId = machine,
                OperatingSystem = Trim(operatingSystem, 200),
                AgentVersion = Trim(agentVersion, 100),
                RemoteAddress = Trim(remoteAddress, 100),
                RequestedUtc = now,
                ReplacesHostId = existingHost?.Id
            };
            _document.PendingAgents.Add(pending);
            QueueSave(SavePriority.Configuration);
            return pending.Clone();
        }

        static string? Trim(string? value, int length) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim() is var trimmed && trimmed.Length > length ? trimmed[..length] : value.Trim();
    }

    public IReadOnlyList<PendingAgent> GetPendingAgents()
    {
        lock (_gate)
        {
            _document.PendingAgents ??= [];
            if (PrunePendingAgentsLocked(DateTimeOffset.UtcNow))
            {
                QueueSave(SavePriority.Configuration);
            }

            return _document.PendingAgents.OrderBy(pending => pending.RequestedUtc).Select(pending => pending.Clone()).ToArray();
        }
    }

    /// <summary>Whether an id + token belong to a machine that waits for approval (its heartbeat is answered so).</summary>
    public bool IsPendingAgent(string? probeId, string? probeToken)
    {
        if (string.IsNullOrWhiteSpace(probeId) || string.IsNullOrEmpty(probeToken))
        {
            return false;
        }

        lock (_gate)
        {
            return (_document.PendingAgents ??= []).Any(pending =>
                string.Equals(pending.ProbeId, probeId, StringComparison.OrdinalIgnoreCase) && FixedTimeEquals(pending.Token, probeToken));
        }
    }

    public AgentApproval ApprovePendingAgent(Guid pendingId)
    {
        lock (_gate)
        {
            if ((_document.PendingAgents ??= []).FirstOrDefault(pending => pending.Id == pendingId) is not { } pending)
            {
                return new AgentApproval(null, "That machine is no longer waiting - approved or rejected meanwhile.");
            }

            var now = DateTimeOffset.UtcNow;
            HostElement host;
            if (pending.ReplacesHostId is Guid replaced && FindElement(replaced) is HostElement existing)
            {
                // A reinstall: the host keeps its sensors, history and alerts; whatever still runs under the old identity is locked out.
                host = existing;
            }
            else
            {
                var key = (_document.AgentRolloutKeys ??= []).FirstOrDefault(candidate => candidate.Id == pending.RolloutKeyId);
                MonitoringContainerElement parent = key?.ParentElementId is Guid parentId
                    && FindElement(parentId) is (ProbeElement or FolderElement) and MonitoringContainerElement found
                        ? found
                        : _document.RootProbe;
                host = CreateHost(parent.Id, pending.MachineName ?? "Agent", pending.MachineName ?? "localhost", "The machine this agent runs on.");
            }

            host.Agent = new AgentIdentity
            {
                ProbeId = pending.ProbeId,
                Token = pending.Token,
                EnrolledUtc = now,
                MachineId = pending.MachineId
            };
            _document.PendingAgents.Remove(pending);
            EnsureAgentHeartbeatSensor(host);
            EnsureAgentBaselineSensorsLocked(host, pending.OperatingSystem);
            QueueSave(SavePriority.Configuration);
            return new AgentApproval(host.Id, null);
        }
    }

    public bool RejectPendingAgent(Guid pendingId)
    {
        lock (_gate)
        {
            if ((_document.PendingAgents ??= []).RemoveAll(pending => pending.Id == pendingId) == 0)
            {
                return false;
            }

            QueueSave(SavePriority.Configuration);
            return true;
        }
    }

    private bool PrunePendingAgentsLocked(DateTimeOffset now) =>
        _document.PendingAgents.RemoveAll(pending => now - pending.RequestedUtc > PendingAgentLifetime) > 0;
}
