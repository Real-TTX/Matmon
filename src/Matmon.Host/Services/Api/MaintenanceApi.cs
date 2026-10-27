using Matmon.Core.Domain;

namespace Matmon.Host.Services.Api;

/// <summary>What a caller of <c>POST /api/v1/maintenance-windows</c> sends. Everything but the target is optional: a window
/// with no times starts now and lasts an hour - the shape of "silence the alerts while this job deploys".</summary>
public sealed record MaintenanceApiRequest(
    string? Name = null,
    string? Target = null,
    DateTimeOffset? Start = null,
    DateTimeOffset? End = null,
    int? DurationMinutes = null,
    string? Note = null,
    bool? Enabled = null);

/// <summary>An answer of the API: the HTTP status and the JSON body.</summary>
public sealed record ApiAnswer(int Status, object Body)
{
    public static ApiAnswer Ok(object body) => new(200, body);
    public static ApiAnswer Created(object body) => new(201, body);
    public static ApiAnswer Problem(int status, string error, string message) => new(status, new { error, message });
}

/// <summary>
/// The maintenance-window part of the instance's API (<c>/api/v1/maintenance-windows</c>, token scope
/// <see cref="ApiTokenScopes.Maintenance"/>): list, read, create a one-time window, switch a window on and off, end one
/// now, delete. Kept apart from the endpoint mapping so it is tested without a web host; the windows themselves are the
/// same ones System > Maintenance edits, saved through the same store method (and its validation).
/// </summary>
public sealed class MaintenanceApi(IMonitoringWorkspaceStore store)
{
    /// <summary>A window made by the API lasts this long when the caller says nothing.</summary>
    public const int DefaultDurationMinutes = 60;

    /// <summary>The longest one-time window the API makes in one go (a deploy that needs longer can extend it).</summary>
    public const int MaxDurationMinutes = 30 * 24 * 60;

    public ApiAnswer List(DateTimeOffset now) =>
        ApiAnswer.Ok(new { items = store.GetMaintenanceWindows().Select(window => Describe(window, now)).ToList() });

    public ApiAnswer Get(Guid id, DateTimeOffset now) =>
        store.FindMaintenanceWindow(id) is { } window ? ApiAnswer.Ok(Describe(window, now)) : NotFound();

    public ApiAnswer Create(MaintenanceApiRequest? request, string createdBy, DateTimeOffset now)
    {
        if (request is null)
        {
            return ApiAnswer.Problem(400, "bad_request", "Send a JSON body, at least { \"target\": \"...\" }.");
        }
        var target = ResolveTarget(request.Target);
        if (target.Problem is { } problem)
        {
            return problem;
        }

        var start = request.Start ?? now;
        DateTimeOffset end;
        if (request.End is { } explicitEnd)
        {
            end = explicitEnd;
        }
        else
        {
            var minutes = request.DurationMinutes ?? DefaultDurationMinutes;
            if (minutes < 1 || minutes > MaxDurationMinutes)
            {
                return ApiAnswer.Problem(400, "bad_duration", $"durationMinutes must be between 1 and {MaxDurationMinutes}.");
            }
            end = start.AddMinutes(minutes);
        }
        if (end <= start)
        {
            return ApiAnswer.Problem(400, "bad_times", "The end must come after the start.");
        }
        if (end - start > TimeSpan.FromMinutes(MaxDurationMinutes))
        {
            return ApiAnswer.Problem(400, "bad_times", $"A window made through the API lasts at most {MaxDurationMinutes / 1440} days.");
        }
        if (end <= now)
        {
            return ApiAnswer.Problem(400, "bad_times", "That window would already be over.");
        }

        var name = string.IsNullOrWhiteSpace(request.Name) ? $"API: {target.Label}" : request.Name.Trim();
        if (name.Length > 120)
        {
            return ApiAnswer.Problem(400, "bad_name", "A name has 120 characters at most.");
        }

        var window = new MaintenanceWindow
        {
            Name = name,
            Enabled = request.Enabled ?? true,
            TargetElementId = target.ElementId,
            TargetTag = target.Tag,
            Recurrence = MaintenanceRecurrence.Once,
            StartUtc = start.ToUniversalTime(),
            EndUtc = end.ToUniversalTime(),
            Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
            CreatedBy = createdBy,
            CreatedUtc = now
        };
        return Save(window, now, created: true);
    }

    public ApiAnswer SetEnabled(Guid id, bool enabled, DateTimeOffset now)
    {
        if (store.FindMaintenanceWindow(id) is not { } window)
        {
            return NotFound();
        }
        window.Enabled = enabled;
        return Save(window, now, created: false);
    }

    /// <summary>Ends a one-time window now (the job is done before its window ran out). A recurring window is switched
    /// off instead - it has no end of its own to move.</summary>
    public ApiAnswer EndNow(Guid id, DateTimeOffset now)
    {
        if (store.FindMaintenanceWindow(id) is not { } window)
        {
            return NotFound();
        }
        if (window.Recurrence != MaintenanceRecurrence.Once)
        {
            return ApiAnswer.Problem(409, "recurring", "A recurring window has no end to move - switch it off (POST .../disable) instead.");
        }
        if (window.EndUtc is { } end && end <= now)
        {
            return ApiAnswer.Ok(Describe(window, now)); // over already: nothing to do
        }
        if (window.StartUtc is { } start && start > now)
        {
            // it has not begun: it simply never happens
            window.Enabled = false;
            return Save(window, now, created: false);
        }
        window.EndUtc = now;
        return Save(window, now, created: false);
    }

    public ApiAnswer Delete(Guid id) =>
        store.DeleteMaintenanceWindow(id) ? ApiAnswer.Ok(new { deleted = true }) : NotFound();

    private ApiAnswer Save(MaintenanceWindow window, DateTimeOffset now, bool created)
    {
        try
        {
            var saved = store.SaveMaintenanceWindow(window);
            return created ? ApiAnswer.Created(Describe(saved, now)) : ApiAnswer.Ok(Describe(saved, now));
        }
        catch (InvalidOperationException ex)
        {
            return ApiAnswer.Problem(400, "invalid_window", ex.Message);
        }
    }

    private static ApiAnswer NotFound() => ApiAnswer.Problem(404, "not_found", "There is no maintenance window with that id.");

    private sealed record TargetChoice(Guid? ElementId, string? Tag, string Label, ApiAnswer? Problem);

    /// <summary>A target is an element id, <c>tag:&lt;name&gt;</c>, or the exact name of one element (refused when several share it).</summary>
    private TargetChoice ResolveTarget(string? target)
    {
        var text = target?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return new(null, null, string.Empty, ApiAnswer.Problem(400, "target_required",
                "Say what the window covers: \"target\": an element id, \"tag:<name>\" or the exact name of an element."));
        }
        if (MonitoringTargetResolver.TagName(text) is { Length: > 0 } tag)
        {
            return new(null, tag, $"#{tag}", null);
        }
        var elements = store.GetAllElements();
        if (Guid.TryParse(text, out var id))
        {
            return elements.FirstOrDefault(element => element.Id == id) is { } byId
                ? new(byId.Id, null, byId.Name, null)
                : new(null, null, text, ApiAnswer.Problem(404, "unknown_target", "There is no element with that id."));
        }
        var named = elements.Where(element => string.Equals(element.Name, text, StringComparison.OrdinalIgnoreCase)).ToList();
        return named.Count switch
        {
            1 => new(named[0].Id, null, named[0].Name, null),
            0 => new(null, null, text, ApiAnswer.Problem(404, "unknown_target", $"No element is called \"{text}\". Use its id or \"tag:<name>\".")),
            _ => new(null, null, text, new ApiAnswer(409, new
            {
                error = "ambiguous_target",
                message = $"{named.Count} elements are called \"{text}\" - use the id of the one you mean.",
                candidates = named.Select(element => new { id = element.Id, name = element.Name, kind = element.GetType().Name.Replace("Element", string.Empty).ToLowerInvariant() })
            }))
        };
    }

    private object Describe(MaintenanceWindow window, DateTimeOffset now)
    {
        var zone = store.MaintenanceZone(window);
        var active = MaintenanceSchedule.ActiveAt(window, now, zone);
        var next = MaintenanceSchedule.Next(window, now, zone);
        var targetName = window.TargetElementId is { } elementId ? store.FindElement(elementId)?.Name : null;
        return new
        {
            id = window.Id,
            name = window.Name,
            enabled = window.Enabled,
            active = active is not null,
            over = MaintenanceSchedule.IsOver(window, now),
            recurrence = window.Recurrence.ToString().ToLowerInvariant(),
            target = new { elementId = window.TargetElementId, elementName = targetName, tag = window.TargetTag },
            startUtc = window.StartUtc,
            endUtc = window.EndUtc,
            current = active is { } on ? new { startUtc = on.StartUtc, endUtc = on.EndUtc } : null,
            next = next is { } upcoming && active is null ? new { startUtc = upcoming.StartUtc, endUtc = upcoming.EndUtc } : null,
            note = window.Note,
            createdBy = window.CreatedBy,
            createdUtc = window.CreatedUtc
        };
    }
}
