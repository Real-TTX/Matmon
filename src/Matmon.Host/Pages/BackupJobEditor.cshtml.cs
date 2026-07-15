using System.Globalization;
using Matmon.Core.Domain;
using Matmon.Host.Services;
using Matmon.Host.Ui;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace Matmon.Host.Pages;

public class BackupJobEditorModel : PageModel
{
    private readonly IMonitoringWorkspaceStore _workspaceStore;
    private readonly CloudBackupClient _cloudBackups;

    public BackupJobEditorModel(IMonitoringWorkspaceStore workspaceStore, CloudBackupClient cloudBackups)
    {
        _workspaceStore = workspaceStore;
        _cloudBackups = cloudBackups;
    }

    /// <summary>True when this instance is linked to Matmon.Cloud - only then is the Cloud destination usable.</summary>
    public bool CloudConnected { get; private set; }

    [BindProperty(SupportsGet = true)]
    public Guid? JobId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [BindProperty]
    public BackupJobEditorInput Input { get; set; } = new();

    public IReadOnlyList<BackupSectionChoice> SectionChoices => BackupSectionCatalog.GetChoices();

    public IReadOnlyList<SelectListItem> ScheduleModeOptions { get; } =
    [
        new("Hourly", nameof(BackupScheduleMode.Hourly)),
        new("Daily", nameof(MonitoringScheduleMode.Daily)),
        new("Weekly", nameof(MonitoringScheduleMode.Weekly)),
        new("Monthly", nameof(MonitoringScheduleMode.Monthly))
    ];

    public IReadOnlyList<SelectListItem> WeekDayOptions { get; } =
    [
        new("Sunday", nameof(DayOfWeek.Sunday)),
        new("Monday", nameof(DayOfWeek.Monday)),
        new("Tuesday", nameof(DayOfWeek.Tuesday)),
        new("Wednesday", nameof(DayOfWeek.Wednesday)),
        new("Thursday", nameof(DayOfWeek.Thursday)),
        new("Friday", nameof(DayOfWeek.Friday)),
        new("Saturday", nameof(DayOfWeek.Saturday))
    ];

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public string PageMode => JobId.HasValue ? "Edit backup job" : "New backup job";

    public IActionResult OnGet()
    {
        if (!MatmonSecurity.IsAdmin(User))
        {
            return Forbid();
        }

        CloudConnected = _cloudBackups.IsConnected;

        if (JobId is Guid jobId)
        {
            var job = _workspaceStore.FindBackupJob(jobId);
            if (job is null)
            {
                ErrorMessage = "Backup job not found.";
                return RedirectToPage("/Config", new { tab = "backup" });
            }

            Input = FromJob(job);
        }
        else
        {
            Input = new BackupJobEditorInput();
        }

        return Page();
    }

    public IActionResult OnPost()
    {
        if (!MatmonSecurity.IsAdmin(User))
        {
            return Forbid();
        }

        CloudConnected = _cloudBackups.IsConnected;

        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            var sections = Input.Sections.ToSections();
            var schedule = BuildSchedule(Input);
            if (JobId is Guid jobId)
            {
                var existing = _workspaceStore.FindBackupJob(jobId) ?? throw new InvalidOperationException("Backup job not found.");
                existing.Name = NormalizeName(Input.Name);
                existing.Description = NormalizeDescription(Input.Description);
                existing.Enabled = Input.Enabled;
                existing.Schedule = schedule;
                existing.Sections = sections;
                existing.Destination = Input.Destination;
                existing.RetentionCount = Math.Clamp(Input.RetentionCount, 1, 100);
                _workspaceStore.UpdateBackupJob(existing);
                StatusMessage = $"Backup job '{existing.Name}' updated.";
            }
            else
            {
                var created = _workspaceStore.CreateBackupJob(new WorkspaceBackupJob
                {
                    Name = NormalizeName(Input.Name),
                    Description = NormalizeDescription(Input.Description),
                    Enabled = Input.Enabled,
                    Schedule = schedule,
                    Sections = sections,
                    Destination = Input.Destination,
                    RetentionCount = Math.Clamp(Input.RetentionCount, 1, 100)
                });
                StatusMessage = $"Backup job '{created.Name}' created.";
                JobId = created.Id;
            }

            return RedirectToReturnUrl();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            return Page();
        }
    }

    public string FormatScheduleSummary(MonitoringSchedule schedule)
    {
        return schedule.Summary();
    }

    private IActionResult RedirectToReturnUrl()
    {
        if (!string.IsNullOrWhiteSpace(ReturnUrl))
        {
            return Redirect(ReturnUrl);
        }

        return RedirectToPage("/Config", new { tab = "backup" });
    }

    private static BackupJobEditorInput FromJob(WorkspaceBackupJob job)
    {
        var sections = new BackupSectionSelectionModel();
        sections.ApplySections(job.Sections);

        var mode = job.Schedule.Mode switch
        {
            MonitoringScheduleMode.Every => BackupScheduleMode.Hourly,
            MonitoringScheduleMode.Daily => BackupScheduleMode.Daily,
            MonitoringScheduleMode.Weekly => BackupScheduleMode.Weekly,
            MonitoringScheduleMode.Monthly => BackupScheduleMode.Monthly,
            _ => BackupScheduleMode.Hourly
        };

        return new BackupJobEditorInput
        {
            Name = job.Name,
            Description = job.Description,
            Enabled = job.Enabled,
            Destination = job.Destination,
            RetentionCount = job.RetentionCount,
            ScheduleMode = mode,
            EveryHours = Math.Max(1, (int)Math.Ceiling((job.Schedule.EverySeconds ?? 3600) / 3600.0)),
            WeekDay = job.Schedule.DayOfWeek ?? DayOfWeek.Sunday,
            DayOfMonth = job.Schedule.DayOfMonth ?? 1,
            TimeOfDay = job.Schedule.TimeOfDay?.ToString(@"hh\:mm", CultureInfo.InvariantCulture) ?? "02:00",
            Sections = sections
        };
    }

    private static string NormalizeName(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "Backup job" : value.Trim();
    }

    private static string? NormalizeDescription(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static MonitoringSchedule BuildSchedule(BackupJobEditorInput input)
    {
        var schedule = new MonitoringSchedule
        {
            Mode = input.ScheduleMode switch
            {
                BackupScheduleMode.Hourly => MonitoringScheduleMode.Every,
                BackupScheduleMode.Daily => MonitoringScheduleMode.Daily,
                BackupScheduleMode.Weekly => MonitoringScheduleMode.Weekly,
                BackupScheduleMode.Monthly => MonitoringScheduleMode.Monthly,
                _ => MonitoringScheduleMode.Every
            }
        };

        switch (input.ScheduleMode)
        {
            case BackupScheduleMode.Hourly:
                schedule.EverySeconds = Math.Max(input.EveryHours, 1) * 3600;
                break;
            case BackupScheduleMode.Daily:
                schedule.TimeOfDay = ParseTime(input.TimeOfDay);
                break;
            case BackupScheduleMode.Weekly:
                schedule.DayOfWeek = input.WeekDay;
                schedule.TimeOfDay = ParseTime(input.TimeOfDay);
                break;
            case BackupScheduleMode.Monthly:
                schedule.DayOfMonth = Math.Clamp(input.DayOfMonth, 1, 31);
                schedule.TimeOfDay = ParseTime(input.TimeOfDay);
                break;
        }

        return schedule;
    }

    private static TimeSpan ParseTime(string? value)
    {
        if (TimeOnly.TryParse(value, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed.ToTimeSpan();
        }

        return TimeSpan.FromHours(2);
    }
}

public sealed class BackupJobEditorInput
{
    public string Name { get; set; } = "Backup job";

    public string? Description { get; set; }

    public bool Enabled { get; set; } = true;

    public BackupDestination Destination { get; set; } = BackupDestination.Local;

    public int RetentionCount { get; set; } = 10;

    public BackupScheduleMode ScheduleMode { get; set; } = BackupScheduleMode.Hourly;

    public int EveryHours { get; set; } = 24;

    public DayOfWeek WeekDay { get; set; } = DayOfWeek.Sunday;

    public int DayOfMonth { get; set; } = 1;

    public string TimeOfDay { get; set; } = "02:00";

    public BackupSectionSelectionModel Sections { get; set; } = new();
}

public enum BackupScheduleMode
{
    Hourly = 0,
    Daily = 1,
    Weekly = 2,
    Monthly = 3
}
