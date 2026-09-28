using Matmon.Core.Domain;

namespace Matmon.Host.Services;

// Agent enrolment: single-use codes an admin creates on the Agents page and an agent redeems for a probe of
// its own. See AgentEnrollment for why it is a code and not the probe token.
public sealed partial class InMemoryMonitoringWorkspaceStore
{
    /// <summary>Upper bound on how long a code may live - a code is for an install that is about to happen.</summary>
    public static readonly TimeSpan MaxAgentEnrollmentValidity = TimeSpan.FromDays(7);

    public AgentEnrollmentIssue CreateAgentEnrollment(string? name, TimeSpan validity, string? createdBy)
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

    public AgentEnrollmentRedemption? RedeemAgentEnrollment(string? code, string? hostName)
    {
        var hash = AgentEnrollmentCode.Hash(code);
        if (hash.Length == 0)
        {
            return null;
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
                return null;
            }

            // Single use: gone before the probe exists, so a second agent racing the same code cannot also win.
            _document.AgentEnrollments.Remove(enrollment);

            var name = enrollment.Name
                ?? (string.IsNullOrWhiteSpace(hostName) ? "Agent" : hostName.Trim());
            var probe = CreateProbe(null, name, "Matmon agent");
            probe.AgentEnrolledUtc = now;
            QueueSave(SavePriority.Configuration);

            return new AgentEnrollmentRedemption(probe.Id, probe.ProbeId, probe.EnrollmentToken ?? string.Empty, probe.Name);
        }
    }

    private bool PruneExpiredAgentEnrollmentsLocked(DateTimeOffset now) =>
        _document.AgentEnrollments.RemoveAll(enrollment => enrollment.IsExpired(now)) > 0;

    private static AgentEnrollment Clone(AgentEnrollment source) => new()
    {
        Id = source.Id,
        CodeHash = source.CodeHash,
        Name = source.Name,
        CreatedUtc = source.CreatedUtc,
        ExpiresUtc = source.ExpiresUtc,
        CreatedBy = source.CreatedBy
    };
}
