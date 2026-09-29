using Matmon.Core.Domain;

namespace Matmon.Host.Services;

// Agent enrolment: single-use codes an admin creates on the Agents page and an agent redeems for a probe of
// its own. See AgentEnrollment for why it is a code and not the probe token.
public sealed partial class InMemoryMonitoringWorkspaceStore
{
    /// <summary>Upper bound on how long a code may live - a code is for an install that is about to happen.</summary>
    public static readonly TimeSpan MaxAgentEnrollmentValidity = TimeSpan.FromDays(7);

    public AgentEnrollmentIssue CreateAgentEnrollment(string? name, TimeSpan validity, string? createdBy, Guid? probeElementId = null)
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

    public AgentEnrollmentResult RedeemAgentEnrollment(string? code, string? hostName, bool allowNewProbe, string? operatingSystem = null)
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
                EnsureAgentBaselineSensorsLocked(existing, operatingSystem);
                return new AgentEnrollmentResult(AgentEnrollmentStatus.Enrolled,
                    new AgentEnrollmentRedemption(existing.Id, existing.ProbeId, existing.EnrollmentToken, existing.Name, Reenrolled: true));
            }

            // Checked BEFORE the code is consumed: a full licence must not burn the code on an install that
            // is going to be refused anyway.
            if (!allowNewProbe)
            {
                return new AgentEnrollmentResult(AgentEnrollmentStatus.ProbeLimit);
            }

            // Single use: gone before the probe exists, so a second agent racing the same code cannot also win.
            _document.AgentEnrollments.Remove(enrollment);

            var name = enrollment.Name
                ?? (string.IsNullOrWhiteSpace(hostName) ? "Agent" : hostName.Trim());
            var probe = CreateProbe(null, name, "Matmon agent");
            probe.AgentEnrolledUtc = now;
            EnsureAgentBaselineSensorsLocked(probe, operatingSystem);
            QueueSave(SavePriority.Configuration);

            return new AgentEnrollmentResult(AgentEnrollmentStatus.Enrolled,
                new AgentEnrollmentRedemption(probe.Id, probe.ProbeId, probe.EnrollmentToken ?? string.Empty, probe.Name));
        }
    }

    /// <summary>
    /// An agent's whole point is the machine it sits on, so it arrives measuring it: "System Health" (local
    /// CPU / memory / disks) and, for an OS it recognises, the pending-updates sensor for it - both run locally
    /// on the agent (empty target = this machine), so neither needs a credential. Each only if the probe has no
    /// sensor of that type yet - a re-enrolled probe keeps what it had. Created through CreateSensor, so the
    /// default thresholds and schedule apply.
    /// </summary>
    private void EnsureAgentBaselineSensorsLocked(ProbeElement probe, string? operatingSystem)
    {
        EnsureSensorOfTypeLocked(probe, LocalHealthSensorExecutor.Definition.Key, "System Health",
            "CPU, memory and disks of this machine, measured by the agent itself.");

        switch (AgentOperatingSystem(operatingSystem))
        {
            case "windows":
                EnsureSensorOfTypeLocked(probe, WindowsUpdateSensorExecutor.Definition.Key, "Windows Update",
                    "Pending Windows updates on this machine, checked locally by the agent.");
                break;
            case "linux":
                EnsureSensorOfTypeLocked(probe, LinuxUpdateSensorExecutor.Definition.Key, "Package Updates",
                    "Pending package updates on this machine, checked locally by the agent.");
                break;
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

    private void EnsureSensorOfTypeLocked(ProbeElement probe, string sensorTypeKey, string name, string description)
    {
        var hasOne = EnumerateElements(probe)
            .OfType<SensorElement>()
            .Any(sensor => string.Equals(sensor.SensorTypeKey, sensorTypeKey, StringComparison.OrdinalIgnoreCase));
        if (!hasOne)
        {
            CreateSensor(probe.Id, name, sensorTypeKey, string.Empty, description);
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
        CreatedUtc = source.CreatedUtc,
        ExpiresUtc = source.ExpiresUtc,
        CreatedBy = source.CreatedBy
    };
}
