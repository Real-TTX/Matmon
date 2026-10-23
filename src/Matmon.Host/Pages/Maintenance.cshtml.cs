using System.Globalization;
using Matmon.Core.Domain;
using Matmon.Host.Services;
using Matmon.Host.Ui;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace Matmon.Host.Pages;

/// <summary>One line of the maintenance list: the window, what it covers in words, its schedule and its state now.</summary>
public sealed record MaintenanceRow(MaintenanceWindow Window, string Covers, string Schedule, string StateTone, string StateLabel, DateTimeOffset? SortKey);

/// <summary>The form - times as text, read in the window's zone (a datetime-local / time input posts "2026-10-23T22:00" /
/// "22:00", which says nothing about a zone).</summary>
public sealed class MaintenanceInput
{
    public Guid? Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>An element id or "tag:&lt;name&gt;" (the element picker with tags).</summary>
    public string? TargetToken { get; set; }

    public MaintenanceRecurrence Recurrence { get; set; } = MaintenanceRecurrence.Weekly;

    public string? StartLocal { get; set; }

    public string? EndLocal { get; set; }

    public List<DayOfWeek> DaysOfWeek { get; set; } = [];

    public int DayOfMonth { get; set; } = 1;

    public string? StartTime { get; set; } = "22:00";

    public int DurationMinutes { get; set; } = 120;

    public string? TimeZoneId { get; set; }

    public string? Note { get; set; }
}

/// <summary>
/// Maintenance windows (Setup > Maintenance): times in which the elements a window covers raise no new alert - patch night,
/// a planned move. The list, and the editor on the same page (<c>?edit=new</c> / <c>?edit=&lt;id&gt;</c>), like the Plugins
/// page. Everyone may look; changes are an admin's (the page write guard).
/// </summary>
public sealed class MaintenanceModel(IMonitoringWorkspaceStore store) : PageModel
{
    private const string LocalFormat = "yyyy-MM-dd'T'HH:mm";

    [BindProperty(SupportsGet = true)]
    public string? Edit { get; set; }

    [BindProperty]
    public MaintenanceInput Input { get; set; } = new();

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    /// <summary>Why the form was refused - shown with the form, which keeps what was typed.</summary>
    public string? FormError { get; private set; }

    public bool Editing => Edit is not null;

    public bool IsNew => Input.Id is null;

    public IReadOnlyList<MaintenanceRow> Rows { get; private set; } = [];

    public IReadOnlyList<ElementPickerOption> PickerOptions { get; private set; } = [];

    public IReadOnlyList<SelectListItem> TimeZoneItems { get; private set; } = [];

    public static IReadOnlyList<(MaintenanceRecurrence Value, string Label)> Recurrences { get; } =
    [
        (MaintenanceRecurrence.Once, "Once"),
        (MaintenanceRecurrence.Daily, "Daily"),
        (MaintenanceRecurrence.Weekly, "Weekly"),
        (MaintenanceRecurrence.Monthly, "Monthly")
    ];

    public static IReadOnlyList<DayOfWeek> Week { get; } = MaintenanceSchedule.OrderedDays(Enum.GetValues<DayOfWeek>()).ToArray();

    public IActionResult OnGet()
    {
        if (Edit is null)
        {
            LoadList();
            return Page();
        }

        if (string.Equals(Edit, "new", StringComparison.OrdinalIgnoreCase))
        {
            var zone = DisplayTimeZone.Resolve(store.GetDisplayTimeZoneId()) ?? TimeZoneInfo.Local;
            var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime;
            now = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0);
            Input = new MaintenanceInput
            {
                TimeZoneId = store.GetDisplayTimeZoneId(),
                DaysOfWeek = [DayOfWeek.Tuesday],
                StartLocal = now.ToString(LocalFormat, CultureInfo.InvariantCulture),
                EndLocal = now.AddHours(2).ToString(LocalFormat, CultureInfo.InvariantCulture)
            };
        }
        else if (Guid.TryParse(Edit, out var id) && store.FindMaintenanceWindow(id) is { } window)
        {
            Input = ToInput(window, store.MaintenanceZone(window));
        }
        else
        {
            ErrorMessage = "That maintenance window no longer exists.";
            return RedirectToPage();
        }

        LoadEditor();
        return Page();
    }

    public IActionResult OnPostSave()
    {
        Edit ??= Input.Id?.ToString() ?? "new";
        try
        {
            var window = ToWindow(Input);
            window.CreatedBy ??= User.Identity?.Name;
            var saved = store.SaveMaintenanceWindow(window);
            StatusMessage = $"Maintenance window '{saved.Name}' saved." + (MaintenanceSchedule.ActiveAt(saved, DateTimeOffset.UtcNow, store.MaintenanceZone(saved)) is not null
                ? " It is on now - the elements it covers raise no new alert."
                : string.Empty);
            return RedirectToPage(new { edit = (string?)null });
        }
        catch (InvalidOperationException exception)
        {
            FormError = exception.Message;
            LoadEditor();
            return Page();
        }
    }

    public IActionResult OnPostDelete(Guid id)
    {
        StatusMessage = store.DeleteMaintenanceWindow(id) ? "Maintenance window deleted." : null;
        return RedirectToPage(new { edit = (string?)null });
    }

    /// <summary>Ends a one-time window that is on now (the work was done sooner) - from now on its elements alert again.</summary>
    public IActionResult OnPostEndNow(Guid id)
    {
        if (store.FindMaintenanceWindow(id) is { Recurrence: MaintenanceRecurrence.Once } window && window.StartUtc is { } start)
        {
            var now = DateTimeOffset.UtcNow;
            window.EndUtc = now > start ? now : start.AddMinutes(1);
            store.SaveMaintenanceWindow(window);
            StatusMessage = $"Maintenance window '{window.Name}' ended - its elements alert again from the next check.";
        }

        return RedirectToPage(new { edit = (string?)null });
    }

    private void LoadList()
    {
        var now = DateTimeOffset.UtcNow;
        var display = DisplayTimeZone.Current;
        var elements = store.GetAllElements().ToDictionary(element => element.Id);
        Rows = store.GetMaintenanceWindows()
            .Select(window =>
            {
                var zone = store.MaintenanceZone(window);
                var covers = window.TargetTag is { } tag
                    ? $"# {tag}"
                    : window.TargetElementId is { } targetId && elements.TryGetValue(targetId, out var target)
                        ? string.Join(" / ", MonitoringTopology.BuildLineage(target, elements).Select(element => element.Name))
                        : "an element that was deleted";
                var active = MaintenanceSchedule.ActiveAt(window, now, zone);
                var next = active is null ? MaintenanceSchedule.Next(window, now, zone) : null;
                var (tone, label, sort) = (window.Enabled, active, next) switch
                {
                    (false, _, _) => ("plain", "Off", (DateTimeOffset?)DateTimeOffset.MaxValue),
                    (_, { } on, _) => ("warning", $"On until {MaintenanceSchedule.Format(on.EndUtc, display)}", (DateTimeOffset?)DateTimeOffset.MinValue),
                    (_, _, { } coming) => ("info", $"Next {MaintenanceSchedule.Format(coming.StartUtc, display)}", coming.StartUtc),
                    _ => ("plain", "Ended", (DateTimeOffset?)DateTimeOffset.MaxValue)
                };
                return new MaintenanceRow(window, covers, MaintenanceSchedule.Describe(window, display), tone, label, sort);
            })
            .OrderBy(row => row.SortKey)
            .ThenBy(row => row.Window.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void LoadEditor()
    {
        PickerOptions = ElementPickerOptions.Build(store.GetWorkspaceClone().RootProbe);
        TimeZoneItems = TimeZoneOptions.Build(Input.TimeZoneId, $"Workspace default ({(DisplayTimeZone.Resolve(store.GetDisplayTimeZoneId()) ?? TimeZoneInfo.Local).Id})");
    }

    private static MaintenanceInput ToInput(MaintenanceWindow window, TimeZoneInfo zone) => new()
    {
        Id = window.Id,
        Name = window.Name,
        Enabled = window.Enabled,
        TargetToken = window.TargetTag is { } tag ? MonitoringTargetResolver.ForTag(tag) : window.TargetElementId?.ToString(),
        Recurrence = window.Recurrence,
        StartLocal = window.StartUtc is { } start ? TimeZoneInfo.ConvertTime(start, zone).ToString(LocalFormat, CultureInfo.InvariantCulture) : null,
        EndLocal = window.EndUtc is { } end ? TimeZoneInfo.ConvertTime(end, zone).ToString(LocalFormat, CultureInfo.InvariantCulture) : null,
        DaysOfWeek = [.. window.DaysOfWeek],
        DayOfMonth = window.DayOfMonth,
        StartTime = window.StartTime.ToString("HH:mm", CultureInfo.InvariantCulture),
        DurationMinutes = window.DurationMinutes,
        TimeZoneId = window.TimeZoneId,
        Note = window.Note
    };

    private MaintenanceWindow ToWindow(MaintenanceInput input)
    {
        var existing = input.Id is { } id ? store.FindMaintenanceWindow(id) : null;
        var window = existing ?? new MaintenanceWindow();
        window.Name = input.Name ?? string.Empty;
        window.Enabled = input.Enabled;
        window.TargetTag = MonitoringTargetResolver.TagName(input.TargetToken);
        window.TargetElementId = window.TargetTag is null ? MonitoringTargetResolver.ElementId(input.TargetToken) : null;
        window.Recurrence = input.Recurrence;
        window.TimeZoneId = string.IsNullOrWhiteSpace(input.TimeZoneId) ? null : input.TimeZoneId.Trim();
        window.Note = input.Note;
        if (window.TimeZoneId is not null && DisplayTimeZone.Resolve(window.TimeZoneId) is null)
        {
            throw new InvalidOperationException("That time zone is not known on this machine.");
        }

        var zone = store.MaintenanceZone(window);
        if (input.Recurrence == MaintenanceRecurrence.Once)
        {
            window.StartUtc = ParseLocal(input.StartLocal, zone, "start");
            window.EndUtc = ParseLocal(input.EndLocal, zone, "end");
            return window;
        }

        window.StartUtc = null;
        window.EndUtc = null;
        window.DaysOfWeek = input.Recurrence == MaintenanceRecurrence.Weekly ? [.. input.DaysOfWeek] : [];
        window.DayOfMonth = input.DayOfMonth;
        window.DurationMinutes = input.DurationMinutes;
        window.StartTime = TimeOnly.TryParseExact(input.StartTime?.Trim(), ["HH:mm", "H:mm", "HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time
            : throw new InvalidOperationException("Enter the start time as hours and minutes, for example 22:00.");
        return window;
    }

    /// <summary>A datetime-local value, read as wall-clock time in <paramref name="zone"/>. A time in the hour the clocks skip
    /// in spring moves to the first minute that exists.</summary>
    private static DateTimeOffset ParseLocal(string? text, TimeZoneInfo zone, string what)
    {
        if (!DateTime.TryParseExact(text?.Trim(), [LocalFormat, "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            throw new InvalidOperationException($"Enter the {what} as a date and a time.");
        }

        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        for (var guard = 0; zone.IsInvalidTime(local) && guard < 180; guard++)
        {
            local = local.AddMinutes(1);
        }

        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}
