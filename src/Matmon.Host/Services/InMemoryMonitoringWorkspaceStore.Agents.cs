using Matmon.Core.Domain;

namespace Matmon.Host.Services;

// Agent enrolment: single-use codes an admin creates on the Agents page and an agent redeems for a probe of
// its own. See AgentEnrollment for why it is a code and not the probe token.
public sealed partial class InMemoryMonitoringWorkspaceStore
{
    /// <summary>Upper bound on how long a code may live - a code is for an install that is about to happen.</summary>
    public static readonly TimeSpan MaxAgentEnrollmentValidity = TimeSpan.FromDays(7);

    public AgentEnrollmentIssue CreateAgentEnrollment(string? name, TimeSpan validity, string? createdBy, Guid? probeElementId = null, Guid? hostElementId = null)
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
            if (probeElementId is Guid targetId)
            {
                // Only a remote probe can be re-enrolled - the local root is this instance itself.
                var target = FindElement(targetId) as ProbeElement;
                if (target is null || target.ParentId is null)
                {
                    throw new InvalidOperationException("Only an existing remote probe can be re-enrolled as an agent.");
                }

                enrollment.ProbeElementId = target.Id;
                enrollment.Name = target.Name;
            }

            if (hostElementId is Guid chosenId)
            {
                var chosen = FindElement(chosenId) as HostElement
                    ?? throw new InvalidOperationException("Choose an existing host for the agent to belong to.");
                if (IsAgentBoundHostLocked(chosen.Id, exceptProbeId: enrollment.ProbeElementId))
                {
                    throw new InvalidOperationException($"'{chosen.Name}' already has an agent - a host belongs to one agent.");
                }

                enrollment.HostElementId = chosen.Id;
                enrollment.Name ??= chosen.Name;
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

            if (enrollment.ProbeElementId is Guid targetId)
            {
                // Single use either way; a code whose probe was deleted meanwhile is simply dead.
                _document.AgentEnrollments.Remove(enrollment);
                QueueSave(SavePriority.Configuration);
                if (FindElement(targetId) is not ProbeElement existing || existing.ParentId is null)
                {
                    return invalid;
                }

                // A NEW token, not the old one handed out again: the previous install (a replaced machine, the
                // Docker container this agent takes over from) is locked out on its next request, so two
                // processes can never report as one probe.
                existing.EnrollmentToken = CreateToken();
                existing.AgentEnrolledUtc = now;
                BindAgentHostLocked(existing, enrollment.HostElementId, hostName);
                EnsureAgentBaselineSensorsLocked(existing, operatingSystem);
                return new AgentEnrollmentResult(AgentEnrollmentStatus.Enrolled,
                    new AgentEnrollmentRedemption(existing.Id, existing.ProbeId, existing.EnrollmentToken, existing.Name, Reenrolled: true));
            }

            // No licence check: agents are not probes in the licence model, so a plan's probe limit never stops one.
            // Single use: gone before the probe exists, so a second agent racing the same code cannot also win.
            _document.AgentEnrollments.Remove(enrollment);

            var name = enrollment.Name
                ?? (string.IsNullOrWhiteSpace(hostName) ? "Agent" : hostName.Trim());
            var probe = CreateProbe(null, name, "Matmon agent");
            probe.AgentEnrolledUtc = now;
            BindAgentHostLocked(probe, enrollment.HostElementId, hostName);
            EnsureAgentBaselineSensorsLocked(probe, operatingSystem);
            QueueSave(SavePriority.Configuration);

            return new AgentEnrollmentResult(AgentEnrollmentStatus.Enrolled,
                new AgentEnrollmentRedemption(probe.Id, probe.ProbeId, probe.EnrollmentToken ?? string.Empty, probe.Name));
        }
    }

    /// <summary>
    /// An agent belongs to a HOST: the machine it sits on. Binds <paramref name="probe"/> to one and returns it - the
    /// host the code named (moved under the probe, so its sensors run on the agent), the one the probe is already
    /// bound to (a re-enrolment), or a new host named after the machine. A named host that has gone, or that
    /// another agent already measures, is not an error: the agent still enrols, with a host of its own.
    /// </summary>
    private HostElement BindAgentHostLocked(ProbeElement probe, Guid? chosenHostId, string? machineName)
    {
        if (chosenHostId is Guid id
            && FindElement(id) is HostElement chosen
            && !IsAgentBoundHostLocked(chosen.Id, probe.Id)
            && MoveHostUnderProbeLocked(chosen, probe))
        {
            probe.BoundHostId = chosen.Id;
            return chosen;
        }

        if (probe.FindBoundHost() is { } bound)
        {
            return bound;
        }

        // Under an agent the machine is "this machine" whatever it is called, so without a reported name the
        // address is localhost - which every machine-local sensor already treats as itself.
        var hasName = !string.IsNullOrWhiteSpace(machineName);
        var host = CreateHost(probe.Id, hasName ? machineName!.Trim() : probe.Name, hasName ? machineName!.Trim() : "localhost",
            "The machine this agent runs on.");
        probe.BoundHostId = host.Id;
        return host;
    }

    /// <summary>Whether some agent probe other than <paramref name="exceptProbeId"/> has this host as its machine.</summary>
    private bool IsAgentBoundHostLocked(Guid hostId, Guid? exceptProbeId = null) =>
        EnumerateElements(_document.RootProbe)
            .OfType<ProbeElement>()
            .Any(probe => probe.Id != exceptProbeId && probe.BoundHostId == hostId && probe.FindBoundHost() is not null);

    /// <summary>Puts a host into the agent's probe - unless it is already somewhere inside it, where it sits is then
    /// the admin's business. A host that cannot move (a refusal, not a crash) simply is not bound.</summary>
    private bool MoveHostUnderProbeLocked(HostElement host, ProbeElement probe)
    {
        if (EnumerateElements(probe).Any(element => element.Id == host.Id))
        {
            return true;
        }

        try
        {
            return MoveElement(host.Id, probe.Id);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// An agent's whole point is the machine it sits on, so it arrives measuring it: "System Health" (local
    /// CPU / memory / disks) and, for an OS it recognises, the pending-updates sensor for it - both run locally
    /// on the agent, so neither needs a credential. They live under the HOST the agent is bound to, not loose
    /// under the probe. Each only if the probe has no sensor of that type yet - a re-enrolled probe keeps what it
    /// had. Created through CreateSensor, so the default thresholds and schedule apply.
    /// </summary>
    private void EnsureAgentBaselineSensorsLocked(ProbeElement probe, string? operatingSystem)
    {
        MonitoringContainerElement under = probe.FindBoundHost() ?? (MonitoringContainerElement)probe;

        EnsureSensorOfTypeLocked(probe, under, LocalHealthSensorExecutor.Definition.Key, "System Health", string.Empty,
            "CPU, memory and disks of this machine, measured by the agent itself.");

        // An explicit "localhost": with no target the sensor would inherit its host's address, and a host that
        // was chosen by its IP address is not recognised as this machine - the sensor would go out over WinRM/SSH
        // to itself instead of simply running here.
        switch (AgentOperatingSystem(operatingSystem))
        {
            case "windows":
                EnsureSensorOfTypeLocked(probe, under, WindowsUpdateSensorExecutor.Definition.Key, "Windows Update", "localhost",
                    "Pending Windows updates on this machine, checked locally by the agent.");
                break;
            case "linux":
                EnsureSensorOfTypeLocked(probe, under, LinuxUpdateSensorExecutor.Definition.Key, "Package Updates", "localhost",
                    "Pending package updates on this machine, checked locally by the agent.");
                break;
        }
    }

    /// <summary>
    /// Migration 6. An agent used to be a probe with its machine's sensors loose underneath it; now it is bound to a
    /// host. Every agent that has none gets one - named after the probe, address localhost (the machine's real name
    /// is only known while the agent is connected, and under an agent "localhost" is exactly right) - and its
    /// baseline sensors move into it. Everything else the probe holds (its heartbeat and health sensors, whatever
    /// the admin added) stays where it is.
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

            var machine = probe.Children
                .OfType<SensorElement>()
                .Where(sensor => machineSensors.Contains(sensor.SensorTypeKey, StringComparer.OrdinalIgnoreCase))
                .ToList();
            foreach (var sensor in machine)
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

    /// <summary>One sensor of a type per probe: looked for in the whole probe, created under <paramref name="under"/>.</summary>
    private void EnsureSensorOfTypeLocked(ProbeElement probe, MonitoringContainerElement under, string sensorTypeKey, string name, string target, string description)
    {
        var hasOne = EnumerateElements(probe)
            .OfType<SensorElement>()
            .Any(sensor => string.Equals(sensor.SensorTypeKey, sensorTypeKey, StringComparison.OrdinalIgnoreCase));
        if (!hasOne)
        {
            CreateSensor(under.Id, name, sensorTypeKey, target, description);
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
        HostElementId = source.HostElementId,
        CreatedUtc = source.CreatedUtc,
        ExpiresUtc = source.ExpiresUtc,
        CreatedBy = source.CreatedBy
    };
}
