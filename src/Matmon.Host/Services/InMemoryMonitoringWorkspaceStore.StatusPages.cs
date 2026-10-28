using Matmon.Core.Domain;

namespace Matmon.Host.Services;

// Status pages: the public "is it working" pages (Maps > Status pages). Configuration lives in the workspace and rides
// the Maps backup section; the daily state counts behind their bars live in the telemetry database.
public sealed partial class InMemoryMonitoringWorkspaceStore
{
    /// <summary>Incident messages older than this (resolved) are tidied away.</summary>
    private static readonly TimeSpan ResolvedIncidentKept = TimeSpan.FromDays(90);

    public IReadOnlyList<StatusPage> GetStatusPages()
    {
        lock (_gate)
        {
            return (_document.StatusPages ??= [])
                .OrderBy(page => page.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(page => page.Clone())
                .ToList();
        }
    }

    public StatusPage? FindStatusPage(Guid id)
    {
        lock (_gate)
        {
            return _document.StatusPages?.FirstOrDefault(page => page.Id == id)?.Clone();
        }
    }

    public StatusPage? FindStatusPageByPublicToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var normalized = token.Trim();
        lock (_gate)
        {
            return _document.StatusPages?
                .FirstOrDefault(page => page.PublicEnabled
                    && string.Equals(page.PublicToken, normalized, StringComparison.OrdinalIgnoreCase))?
                .Clone();
        }
    }

    public StatusPage SaveStatusPage(StatusPage page)
    {
        var copy = page.Clone();
        copy.Name = copy.Name.Trim();
        copy.Description = string.IsNullOrWhiteSpace(copy.Description) ? null : copy.Description.Trim();
        foreach (var component in copy.Components)
        {
            component.Name = component.Name.Trim();
            component.Group = string.IsNullOrWhiteSpace(component.Group) ? null : component.Group.Trim();
            component.Description = string.IsNullOrWhiteSpace(component.Description) ? null : component.Description.Trim();
            component.TargetToken = string.IsNullOrWhiteSpace(component.TargetToken) ? null : component.TargetToken.Trim();
        }

        if (StatusPageRules.Validate(copy) is { } problem)
        {
            throw new InvalidOperationException(problem);
        }

        lock (_gate)
        {
            _document.StatusPages ??= [];
            var index = _document.StatusPages.FindIndex(existing => existing.Id == copy.Id);
            copy.UpdatedUtc = DateTimeOffset.UtcNow;
            if (index >= 0)
            {
                // The editor does not carry the token or the incidents; they are kept from what is stored.
                var existing = _document.StatusPages[index];
                copy.PublicToken = existing.PublicToken;
                copy.CreatedUtc = existing.CreatedUtc;
                copy.Incidents = existing.Incidents;
                var componentIds = copy.Components.Select(component => component.Id).ToHashSet();
                foreach (var incident in copy.Incidents)
                {
                    incident.ComponentIds.RemoveAll(id => !componentIds.Contains(id));
                }

                _document.StatusPages[index] = copy;
            }
            else
            {
                copy.PublicToken = CreateToken();
                copy.CreatedUtc = copy.UpdatedUtc;
                copy.Incidents = [];
                _document.StatusPages.Add(copy);
            }

            QueueSave(SavePriority.Configuration);
            return copy.Clone();
        }
    }

    public bool DeleteStatusPage(Guid id)
    {
        lock (_gate)
        {
            var removed = _document.StatusPages?.RemoveAll(page => page.Id == id) > 0;
            if (removed)
            {
                QueueSave(SavePriority.Configuration);
            }

            return removed;
        }
    }

    public string? RotateStatusPageToken(Guid id)
    {
        lock (_gate)
        {
            var page = _document.StatusPages?.FirstOrDefault(candidate => candidate.Id == id);
            if (page is null)
            {
                return null;
            }

            page.PublicToken = CreateToken();
            page.UpdatedUtc = DateTimeOffset.UtcNow;
            QueueSave(SavePriority.Configuration);
            return page.PublicToken;
        }
    }

    public StatusIncident PostStatusIncident(Guid pageId, string title, StatusIncidentImpact impact, IEnumerable<Guid> componentIds, StatusIncidentPhase phase, string message, string? by)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new InvalidOperationException("Give the incident a title.");
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            throw new InvalidOperationException("Say what is happening.");
        }

        lock (_gate)
        {
            var page = _document.StatusPages?.FirstOrDefault(candidate => candidate.Id == pageId)
                ?? throw new InvalidOperationException("That status page no longer exists.");
            var known = page.Components.Select(component => component.Id).ToHashSet();
            var now = DateTimeOffset.UtcNow;
            var incident = new StatusIncident
            {
                Title = title.Trim(),
                Impact = impact,
                ComponentIds = componentIds.Where(known.Contains).Distinct().ToList(),
                CreatedUtc = now,
                ResolvedUtc = phase == StatusIncidentPhase.Resolved ? now : null,
                Updates = [new StatusIncidentUpdate { AtUtc = now, Phase = phase, Message = message.Trim(), By = by }]
            };
            page.Incidents.Add(incident);
            PruneIncidentsLocked(page, now);
            page.UpdatedUtc = now;
            QueueSave(SavePriority.Configuration);
            return incident.Clone();
        }
    }

    public bool AddStatusIncidentUpdate(Guid pageId, Guid incidentId, StatusIncidentPhase phase, string message, string? by)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new InvalidOperationException("Say what is happening.");
        }

        lock (_gate)
        {
            var incident = _document.StatusPages?.FirstOrDefault(candidate => candidate.Id == pageId)?
                .Incidents.FirstOrDefault(candidate => candidate.Id == incidentId);
            if (incident is null)
            {
                return false;
            }

            var now = DateTimeOffset.UtcNow;
            incident.Updates.Add(new StatusIncidentUpdate { AtUtc = now, Phase = phase, Message = message.Trim(), By = by });
            // Resolved closes it; any other phase (re)opens it - an incident that flares up again is the same incident.
            incident.ResolvedUtc = phase == StatusIncidentPhase.Resolved ? now : null;
            QueueSave(SavePriority.Configuration);
            return true;
        }
    }

    public bool DeleteStatusIncident(Guid pageId, Guid incidentId)
    {
        lock (_gate)
        {
            var removed = _document.StatusPages?.FirstOrDefault(candidate => candidate.Id == pageId)?
                .Incidents.RemoveAll(candidate => candidate.Id == incidentId) > 0;
            if (removed)
            {
                QueueSave(SavePriority.Configuration);
            }

            return removed;
        }
    }

    public IReadOnlyList<SensorDailyState> GetDailyStates(IReadOnlyCollection<Guid> sensorIds, DateOnly fromDay) =>
        _telemetry.GetDailyStates(sensorIds, fromDay);

    private static void PruneIncidentsLocked(StatusPage page, DateTimeOffset now) =>
        page.Incidents.RemoveAll(incident => incident.ResolvedUtc is { } resolved && now - resolved > ResolvedIncidentKept);

    /// <summary>Boot invariant: every status page has a token (a page restored from a hand-edited package might not).</summary>
    private void EnsureStatusPageTokens()
    {
        _document.StatusPages ??= [];
        foreach (var page in _document.StatusPages.Where(page => string.IsNullOrWhiteSpace(page.PublicToken)))
        {
            page.PublicToken = CreateToken();
        }
    }
}
