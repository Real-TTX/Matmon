using Matmon.Core.Domain;
using Matmon.Host.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace Matmon.Host.Pages;

/// <summary>
/// The scheduled "summary report" notification — the single scheduled entry shown in the unified Notifications
/// list (moved here from System → Config). Uses the same Sender/Receiver endpoints as trigger rules, falling
/// back to a free-text recipients list + the workspace default SMTP.
/// </summary>
public class NotificationReportEditorModel : PageModel
{
    private readonly IMonitoringWorkspaceStore _workspaceStore;
    private readonly SummaryReportSender _summaryReportSender;

    public NotificationReportEditorModel(IMonitoringWorkspaceStore workspaceStore, SummaryReportSender summaryReportSender)
    {
        _workspaceStore = workspaceStore;
        _summaryReportSender = summaryReportSender;
    }

    [BindProperty] public bool Enabled { get; set; }
    [BindProperty] public SummaryReportCadence Cadence { get; set; } = SummaryReportCadence.Daily;
    [BindProperty] public int HourOfDay { get; set; } = 7;
    [BindProperty] public DayOfWeek DayOfWeek { get; set; } = DayOfWeek.Monday;
    [BindProperty] public Guid? SenderId { get; set; }
    [BindProperty] public Guid? ReceiverId { get; set; }
    [BindProperty] public string Recipients { get; set; } = string.Empty;
    [BindProperty] public string Subject { get; set; } = "Matmon summary report";
    [BindProperty] public bool AttachPdf { get; set; }

    public IReadOnlyList<SelectListItem> Senders { get; private set; } = [];
    public IReadOnlyList<SelectListItem> Receivers { get; private set; } = [];
    public bool HasSmtp { get; private set; }
    public DateTimeOffset? LastSentUtc { get; private set; }

    [TempData] public string? StatusMessage { get; set; }
    public string? ErrorMessage { get; private set; }

    public void OnGet() => Load(fromStore: true);

    public IActionResult OnPostSave()
    {
        _workspaceStore.UpdateSummaryReportSettings(new SummaryReportSettings
        {
            Enabled = Enabled,
            Cadence = Cadence,
            HourOfDay = Math.Clamp(HourOfDay, 0, 23),
            DayOfWeek = DayOfWeek,
            SenderId = SenderId,
            ReceiverId = ReceiverId,
            Recipients = (Recipients ?? string.Empty).Trim(),
            Subject = string.IsNullOrWhiteSpace(Subject) ? "Matmon summary report" : Subject.Trim(),
            AttachPdf = AttachPdf
        });
        StatusMessage = "Scheduled report saved.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSendTestAsync(CancellationToken cancellationToken)
    {
        var settings = _workspaceStore.GetSummaryReportSettings();
        try
        {
            var sent = await _summaryReportSender.SendAsync(settings, cancellationToken);
            StatusMessage = sent
                ? "Report sent."
                : "Report not sent — check the recipient/receiver and that an SMTP sender is configured.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Report not sent: {ex.Message}";
        }
        return RedirectToPage();
    }

    public IActionResult OnPostDownloadPdf()
    {
        var pdf = _summaryReportSender.BuildAuditPdf(Cadence);
        return File(pdf, "application/pdf", $"matmon-audit-{DateTimeOffset.UtcNow:yyyyMMdd}.pdf");
    }

    private void Load(bool fromStore)
    {
        if (fromStore)
        {
            var s = _workspaceStore.GetSummaryReportSettings();
            Enabled = s.Enabled;
            Cadence = s.Cadence;
            HourOfDay = s.HourOfDay;
            DayOfWeek = s.DayOfWeek;
            SenderId = s.SenderId;
            ReceiverId = s.ReceiverId;
            Recipients = s.Recipients;
            Subject = s.Subject;
            AttachPdf = s.AttachPdf;
            LastSentUtc = s.LastSentUtc;
        }

        var workspace = _workspaceStore.Workspace;
        Senders = workspace.NotificationSenders
            .Where(sender => sender.Kind == NotificationEndpointKind.Email)
            .Select(sender => new SelectListItem($"{sender.Name}{(sender.Enabled ? "" : " (disabled)")}", sender.Id.ToString()))
            .ToList();
        Receivers = workspace.NotificationReceivers
            .Select(receiver => new SelectListItem($"{receiver.Name} — {receiver.Target}", receiver.Id.ToString()))
            .ToList();
        HasSmtp = _workspaceStore.HasEmailNotifications()
            || !string.IsNullOrWhiteSpace(workspace.NotificationConfiguration.Email.SmtpHost);
    }
}
