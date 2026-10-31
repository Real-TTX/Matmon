using Matmon.Core.Domain;

namespace Matmon.Host.Services;

// Agents: an agent IS a host - the machine it is installed on - with an identity of its own (HostElement.Agent). Its
// sensors live under the host and run on the agent; where the host sits says which probe the agent talks through (a
// remote probe relays its requests to the primary). Enrolment: single-use codes an admin creates on the Agents page and
// an agent redeems for that identity - see AgentEnrollment for why it is a code and not the token.
public sealed partial class InMemoryMonitoringWorkspaceStore
{
    /// <summary>Upper bound on how long a code may live - a code is for an install that is about to happen.</summary>
    public static readonly TimeSpan MaxAgentEnrollmentValidity = TimeSpan.FromDays(7);

    /// <summary>The types an agent's machine is measured with - they move along when the agent moves to another host.</summary>
    private static readonly string[] AgentMachineSensorTypes =
    [
        LocalHealthSensorExecutor.Definition.Key,
        WindowsUpdateSensorExecutor.Definition.Key,
        LinuxUpdateSensorExecutor.Definition.Key,
        ProbeHeartbeatSensorExecutor.Definition.Key,
        ProbeHealthSensorExecutor.Definition.Key
    ];

    public AgentEnrollmentIssue CreateAgentEnrollment(string? name, TimeSpan validity, string? createdBy, Guid? parentElementId = null, Guid? hostElementId = null)
    {
        var now = DateTimeOffset.UtcNow;
        var clamped = validity <= TimeSpan.Zero
            ? TimeSpan.FromHours(24)
            : validity > MaxAgentEnrollmentValidity ? MaxAgentEnrollmentValidity : validity;
        var code = AgentEnrollmentCode.Generate();
        var enrollment = new AgentEnrollment
        {
            CodeHash = AgentEnrollmentCode.Hash(code),
            Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
            CreatedUtc = now,
            ExpiresUtc = now + clamped,
            CreatedBy = string.IsNullOrWhiteSpace(createdBy) ? null : createdBy.Trim()
        };

        lock (_gate)
        {
            if (hostElementId is Guid chosenId)
            {
                var chosen = FindElement(chosenId) as HostElement
                    ?? throw new InvalidOperationException("Choose an existing host for the agent.");
                enrollment.HostElementId = chosen.Id;
                enrollment.Name ??= chosen.Name;
            }
            else if (parentElementId is Guid parentId && parentId != Guid.Empty)
            {
                var parent = FindElement(parentId);
                if (parent is not (ProbeElement or FolderElement))
                {
                    throw new InvalidOperationException("Choose a probe or a folder for the new agent.");
                }

                enrollment.ParentElementId = parent.Id;
            }

            _document.AgentEnrollments ??= [];
            PruneExpiredAgentEnrollmentsLocked(now);
            _document.AgentEnrollments.Add(enrollment);
            QueueSave(SavePriority.Configuration);
        }

        return new AgentEnrollmentIssue(Clone(enrollment), code);
    }

    public IReadOnlyList<AgentEnrollment> GetPendingAgentEnrollments()
    {
        lock (_gate)
        {
            _document.AgentEnrollments ??= [];
            if (PruneExpiredAgentEnrollmentsLocked(DateTimeOffset.UtcNow))
            {
                QueueSave(SavePriority.Configuration);
            }

            return _document.AgentEnrollments
                .OrderByDescending(enrollment => enrollment.CreatedUtc)
                .Select(Clone)
                .ToArray();
        }
    }

    public bool RevokeAgentEnrollment(Guid enrollmentId)
    {
        lock (_gate)
        {
            _document.AgentEnrollments ??= [];
            if (_document.AgentEnrollments.RemoveAll(enrollment => enrollment.Id == enrollmentId) == 0)
            {
                return false;
            }

            QueueSave(SavePriority.Configuration);
            return true;
        }
    }

    public AgentEnrollment? FindAgentEnrollmentByCode(string? code)
    {
        var hash = AgentEnrollmentCode.Hash(code);
        if (hash.Length == 0)
        {
            return null;
        }

        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            var found = (_document.AgentEnrollments ??= []).FirstOrDefault(candidate =>
                !candidate.IsExpired(now) && string.Equals(candidate.CodeHash, hash, StringComparison.Ordinal));
            return found is null ? null : Clone(found);
        }
    }

    public bool IsAgentEnrollmentCodeValid(string? code)
    {
        var hash = AgentEnrollmentCode.Hash(code);
        if (hash.Length == 0)
        {
            return false;
        }

        lock (_gate)
        {
            _document.AgentEnrollments ??= [];
            var now = DateTimeOffset.UtcNow;
            return _document.AgentEnrollments.Any(candidate =>
                !candidate.IsExpired(now) && string.Equals(candidate.CodeHash, hash, StringComparison.Ordinal));
        }
    }

    public ProbeElement? FindAgentRelayProbe(AgentEnrollment enrollment)
    {
        lock (_gate)
        {
            var place = enrollment.HostElementId is Guid hostId
                ? FindElementInternal(hostId)
                : enrollment.ParentElementId is Guid parentId ? FindElementInternal(parentId) : null;
            return place is null ? _document.RootProbe : MonitoringTopology.FindProbe(BuildLineage(place)) ?? _document.RootProbe;
        }
    }

    public AgentEnrollmentResult RedeemAgentEnrollment(string? code, string? hostName, string? operatingSystem = null)
    {
        var invalid = new AgentEnrollmentResult(AgentEnrollmentStatus.Invalid);
        var hash = AgentEnrollmentCode.Hash(code);
        if (hash.Length == 0)
        {
            return invalid;
        }

        lock (_gate)
        {
            _document.AgentEnrollments ??= [];
            var now = DateTimeOffset.UtcNow;
            PruneExpiredAgentEnrollmentsLocked(now);

            // Hashes are compared as hashes: equal-length hex of a secret the caller does not know, so a
            // timing difference says nothing about the code. An expired code was pruned above and fails here
            // exactly like a wrong one - the endpoint must not tell a guesser which codes once existed.
            var enrollment = _document.AgentEnrollments.FirstOrDefault(candidate =>
                string.Equals(candidate.CodeHash, hash, StringComparison.Ordinal));
            if (enrollment is null)
            {
                return invalid;
            }

            // Single use: gone before anything is created, so a second agent racing the same code cannot also win.
            // No licence check: agents are not probes in the licence model.
            _document.AgentEnrollments.Remove(enrollment);

            var machine = string.IsNullOrWhiteSpace(hostName) ? null : hostName.Trim();
            var reenrolled = false;
            HostElement host;
            if (enrollment.HostElementId is Guid hostId && FindElement(hostId) is HostElement existing)
            {
                host = existing;
                reenrolled = existing.Agent is not null;
            }
            else
            {
                MonitoringContainerElement parent = enrollment.ParentElementId is Guid parentId
                    && FindElement(parentId) is (ProbeElement or FolderElement) and MonitoringContainerElement found
                        ? found
                        : _document.RootProbe;
                var name = enrollment.Name ?? machine ?? "Agent";
                host = CreateHost(parent.Id, name, machine ?? "localhost", "The machine this agent runs on.");
            }

            // A NEW token every time, never the old one handed out again: a previous install (a replaced machine) is
            // locked out on its next request, so two processes can never report as one agent.
            host.Agent = new AgentIdentity
            {
                ProbeId = host.Agent?.ProbeId is { Length: > 0 } kept ? kept : GenerateUniqueProbeId(host.Name),
                Token = CreateToken(),
                EnrolledUtc = now
            };
            EnsureAgentHeartbeatSensor(host);
            EnsureAgentBaselineSensorsLocked(host, operatingSystem);
            QueueSave(SavePriority.Configuration);

            return new AgentEnrollmentResult(AgentEnrollmentStatus.Enrolled,
                new AgentEnrollmentRedemption(host.Id, host.Agent.ProbeId, host.Agent.Token!, host.Name, reenrolled));
        }
    }

    /// <summary>
    /// Moves an agent to another host later on - the machine is already in the tree under a host of its own, or the wrong
    /// one was picked. The identity goes over (the installed agent keeps working, nothing to reinstall), and so do the
    /// agent's machine sensors (System Health, the update sensors, its heartbeat) unless the new host already has one of
    /// that type. The new host stays where it is - so the probe above it is the one the agent talks through from then on -
    /// and the old host keeps everything else as an ordinary host.
    /// </summary>
    public string? RebindAgentHost(Guid agentHostId, Guid hostElementId)
    {
        lock (_gate)
        {
            if (FindElement(agentHostId) is not HostElement { Agent: not null } old)
            {
                return "That agent no longer exists.";
            }

            if (FindElement(hostElementId) is not HostElement host)
            {
                return "That host no longer exists.";
            }

            if (host.Id == old.Id)
            {
                return $"The agent already runs on '{host.Name}'.";
            }

            if (host.Agent is not null)
            {
                return $"'{host.Name}' already has an agent of its own.";
            }

            host.Agent = old.Agent;
            old.Agent = null;

            var moving = old.Children
                .OfType<SensorElement>()
                .Where(sensor => AgentMachineSensorTypes.Contains(sensor.SensorTypeKey, StringComparer.OrdinalIgnoreCase)
                    && !host.Children.OfType<SensorElement>().Any(present =>
                        string.Equals(present.SensorTypeKey, sensor.SensorTypeKey, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            foreach (var sensor in moving)
            {
                MoveElement(sensor.Id, host.Id);
            }

            EnsureAgentHeartbeatSensor(host);
            QueueSave(SavePriority.Configuration);
            return null;
        }
    }

    /// <summary>
    /// An agent's whole point is the machine it sits on, so it arrives measuring it: "System Health" (local CPU / memory /
    /// disks) and, for an OS it recognises, the pending-updates sensor for it - both run locally on the agent, so neither
    /// needs a credential. Each only if the host has no sensor of that type yet - a re-enrolled host keeps what it had.
    /// Created through CreateSensor, so the default thresholds and schedule apply.
    /// </summary>
    private void EnsureAgentBaselineSensorsLocked(HostElement host, string? operatingSystem)
    {
        EnsureSensorOfTypeLocked(host, LocalHealthSensorExecutor.Definition.Key, "System Health", string.Empty,
            "CPU, memory and disks of this machine, measured by the agent itself.");

        // An explicit "localhost": with no target the sensor would inherit its host's address, and a host that was
        // chosen by its IP address is not recognised as this machine - the sensor would go out over WinRM/SSH to itself
        // instead of simply running here.
        switch (AgentOperatingSystem(operatingSystem))
        {
            case "windows":
                EnsureSensorOfTypeLocked(host, WindowsUpdateSensorExecutor.Definition.Key, "Windows Update", "localhost",
                    "Pending Windows updates on this machine, checked locally by the agent.");
                break;
            case "linux":
                EnsureSensorOfTypeLocked(host, LinuxUpdateSensorExecutor.Definition.Key, "Package Updates", "localhost",
                    "Pending package updates on this machine, checked locally by the agent.");
                break;
        }
    }

    /// <summary>The agent's connection, judged by the primary: the heartbeat sensor of a probe, under the agent's host.</summary>
    private void EnsureAgentHeartbeatSensor(HostElement host)
    {
        if (host.Agent is not { } agent)
        {
            return;
        }

        var sensor = host.Children
            .OfType<SensorElement>()
            .FirstOrDefault(candidate => string.Equals(candidate.SensorTypeKey, ProbeHeartbeatSensorExecutor.Definition.Key, StringComparison.OrdinalIgnoreCase));
        if (sensor is null)
        {
            sensor = new SensorElement("Agent connection", ProbeHeartbeatSensorExecutor.Definition.Key, agent.ProbeId)
            {
                Description = "Whether the agent on this machine reports in"
            };
            AddChild(host, sensor);
        }

        sensor.Target = agent.ProbeId;
        ApplyParameterDefaults(sensor.Settings, ProbeHeartbeatSensorExecutor.Definition);
        EnsureHeartbeatThresholdDefaults(sensor.Settings, Math.Max(_runtimeOptions.HeartbeatIntervalSeconds, 30));
    }

    /// <summary>Boot invariant: every agent host has an id, a token and its heartbeat sensor.</summary>
    private void EnsureAgentHostsLocked()
    {
        foreach (var host in EnumerateElements(_document.RootProbe).OfType<HostElement>().Where(host => host.Agent is not null).ToList())
        {
            if (string.IsNullOrWhiteSpace(host.Agent!.ProbeId))
            {
                host.Agent.ProbeId = GenerateUniqueProbeId(host.Name);
            }

            if (string.IsNullOrWhiteSpace(host.Agent.Token))
            {
                host.Agent.Token = CreateToken();
            }

            EnsureAgentHeartbeatSensor(host);
        }
    }

    /// <summary>
    /// Migration 12. An agent used to be a PROBE (with its machine as a bound host under it); now it IS that host. For every
    /// agent probe: its host gets the agent's identity (same id and token - the installed agent keeps working), moves to
    /// where the probe was (so the agent still talks to the same place), takes the probe's own sensors along (heartbeat,
    /// health, whatever sat directly under it), and anything else the probe held - other hosts, folders - moves up next to
    /// it, where the probe above runs it. References to the probe (rules, maintenance windows, map tiles) point at the host;
    /// a plugin placed on the agent is dropped (an agent measures its machine and runs no collector); pending codes that
    /// would re-enrol it re-enrol the host, any other re-enrolment code is dropped. Then the probe is gone.
    /// </summary>
    private void MigrateAgentProbesToHosts()
    {
        var agents = EnumerateElements(_document.RootProbe)
            .OfType<ProbeElement>()
            .Where(probe => probe.ParentId is not null && probe.AgentEnrolledUtc is not null)
            .ToList();

        foreach (var probe in agents)
        {
            var parentId = probe.ParentId!.Value;
            var host = probe.FindBoundHost()
                ?? CreateHost(probe.Id, probe.Name, "localhost", "The machine this agent runs on.");
            host.Agent = new AgentIdentity
            {
                ProbeId = probe.ProbeId,
                Token = probe.EnrollmentToken,
                EnrolledUtc = probe.AgentEnrolledUtc!.Value
            };
            MoveElement(host.Id, parentId);

            foreach (var child in probe.Children.ToList())
            {
                MoveElement(child.Id, child is SensorElement ? host.Id : parentId);
            }

            RetargetElementLocked(probe.Id, host.Id);
            var dropped = (_document.Plugins ??= []).RemoveAll(assignment => assignment.ProbeElementId == probe.Id);
            if (dropped > 0)
            {
                _logger.LogWarning("Agent {Agent}: {Count} plugin assignment(s) removed - an agent runs no collector any more", probe.Name, dropped);
            }

            foreach (var enrollment in (_document.AgentEnrollments ??= []).Where(enrollment => enrollment.ProbeElementId == probe.Id))
            {
                enrollment.ProbeElementId = null;
                enrollment.HostElementId = host.Id;
            }

            RemoveChild(_document.RootProbe, probe.Id);
        }

        // A code that would have turned a Docker probe into an agent means nothing now.
        (_document.AgentEnrollments ??= []).RemoveAll(enrollment => enrollment.ProbeElementId is not null);
    }

    /// <summary>Points what referred to one element (notification rules, maintenance windows, map tiles) at another.</summary>
    private void RetargetElementLocked(Guid from, Guid to)
    {
        foreach (var rule in _document.NotificationRules.Where(rule => rule.TargetElementId == from))
        {
            rule.TargetElementId = to;
        }

        foreach (var window in (_document.MaintenanceWindows ??= []).Where(window => window.TargetElementId == from))
        {
            window.TargetElementId = to;
        }

        foreach (var tile in (_document.Maps ??= []).SelectMany(map => map.Tiles.Concat(map.Slides.SelectMany(slide => slide.Tiles))).Where(tile => tile.ElementId == from))
        {
            tile.ElementId = to;
        }
    }

    /// <summary>
    /// Migration 6 (LEGACY - from when an agent was a probe): every agent probe without a bound host got one, named after
    /// the probe, and its machine sensors moved into it. Migration 12 then turns that host into the agent.
    /// </summary>
    private void MigrateAgentProbesToBoundHosts()
    {
        string[] machineSensors =
        [
            LocalHealthSensorExecutor.Definition.Key,
            WindowsUpdateSensorExecutor.Definition.Key,
            LinuxUpdateSensorExecutor.Definition.Key
        ];

        var unbound = EnumerateElements(_document.RootProbe)
            .OfType<ProbeElement>()
            .Where(probe => probe.AgentEnrolledUtc is not null && probe.FindBoundHost() is null)
            .ToList();

        foreach (var probe in unbound)
        {
            var host = CreateHost(probe.Id, probe.Name, "localhost", "The machine this agent runs on.");
            probe.BoundHostId = host.Id;
            foreach (var sensor in probe.Children.OfType<SensorElement>()
                         .Where(sensor => machineSensors.Contains(sensor.SensorTypeKey, StringComparer.OrdinalIgnoreCase)).ToList())
            {
                MoveElement(sensor.Id, host.Id);
            }
        }
    }

    /// <summary>"windows" / "linux" / null from the agent's reported OS description.</summary>
    public static string? AgentOperatingSystem(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        if (description.Contains("Windows", StringComparison.OrdinalIgnoreCase))
        {
            return "windows";
        }

        // RuntimeInformation.OSDescription on Linux is the distro ("Ubuntu 24.04.1 LTS", "Debian GNU/Linux 12")
        // or the kernel ("Linux 6.8.0-..."); macOS says "Darwin" and has no update sensor.
        string[] linux = ["Linux", "Ubuntu", "Debian", "Fedora", "CentOS", "Red Hat", "Rocky", "Alma", "SUSE", "Alpine", "Raspbian", "Arch"];
        return linux.Any(name => description.Contains(name, StringComparison.OrdinalIgnoreCase)) ? "linux" : null;
    }

    /// <summary>One sensor of a type per agent host.</summary>
    private void EnsureSensorOfTypeLocked(HostElement host, string sensorTypeKey, string name, string target, string description)
    {
        var hasOne = host.Children
            .OfType<SensorElement>()
            .Any(sensor => string.Equals(sensor.SensorTypeKey, sensorTypeKey, StringComparison.OrdinalIgnoreCase));
        if (!hasOne)
        {
            CreateSensor(host.Id, name, sensorTypeKey, target, description);
        }
    }

    private bool PruneExpiredAgentEnrollmentsLocked(DateTimeOffset now) =>
        _document.AgentEnrollments.RemoveAll(enrollment => enrollment.IsExpired(now)) > 0;

    private static AgentEnrollment Clone(AgentEnrollment source) => new()
    {
        Id = source.Id,
        CodeHash = source.CodeHash,
        Name = source.Name,
        ProbeElementId = source.ProbeElementId,
        ParentElementId = source.ParentElementId,
        HostElementId = source.HostElementId,
        CreatedUtc = source.CreatedUtc,
        ExpiresUtc = source.ExpiresUtc,
        CreatedBy = source.CreatedBy
    };
}
