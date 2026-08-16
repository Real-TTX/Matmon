using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Matmon.Core.Domain;
using Matmon.Core.Sample;
using Matmon.Host.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;
using Matmon.Host.Ui;

namespace Matmon.Host.Pages;

public sealed partial class WorkspaceModel
{
    public IActionResult OnPostAcknowledgeAlert(Guid alertId, string? returnUrl)
    {
        try
        {
            if (!_workspaceStore.AcknowledgeAlert(alertId, User.Identity?.Name))
            {
                throw new InvalidOperationException("Alert not found.");
            }

            StatusMessage = "Alert confirmed.";
            return RedirectAfterAction(returnUrl, "/Alerts");
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            LoadViewState(populateEditorValues: false);
            return Page();
        }
    }

    public IActionResult OnPostAcknowledgeAlerts(Guid[] alertIds, string? returnUrl)
    {
        try
        {
            var ids = (alertIds ?? []).Distinct().ToArray();
            if (ids.Length == 0)
            {
                throw new InvalidOperationException("No alerts selected.");
            }

            var acknowledged = ids.Count(id => _workspaceStore.AcknowledgeAlert(id, User.Identity?.Name));

            StatusMessage = acknowledged == 1
                ? "Alert confirmed."
                : $"{acknowledged} alerts confirmed.";
            return RedirectAfterAction(returnUrl, "/Alerts");
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            LoadViewState(populateEditorValues: false);
            return Page();
        }
    }

    // Mute = acknowledge + stop re-opening. The button carries "{elementId}:{duration}" (duration = perm|1h|24h|7d)
    // because it rides inside the shared alerts form and a submit button can only send one name/value pair.
    public IActionResult OnPostMuteAlert(string mute, string? returnUrl)
    {
        try
        {
            var parts = (mute ?? string.Empty).Split(':', 2);
            if (parts.Length != 2 || !Guid.TryParse(parts[0], out var elementId))
            {
                throw new InvalidOperationException("Invalid mute request.");
            }

            TimeSpan? duration = parts[1] switch
            {
                "perm" => null,
                "1h" => TimeSpan.FromHours(1),
                "24h" => TimeSpan.FromHours(24),
                "7d" => TimeSpan.FromDays(7),
                _ => throw new InvalidOperationException("Unknown mute duration.")
            };

            _workspaceStore.MuteElementAlerts(elementId, duration, User.Identity?.Name);
            StatusMessage = duration is null
                ? "Alert muted - it won't re-open until you un-mute it."
                : $"Alert muted for {parts[1]} - it won't re-open in that window.";
            return RedirectAfterAction(returnUrl, "/Alerts");
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            LoadViewState(populateEditorValues: false);
            return Page();
        }
    }

    public IActionResult OnPostUnmuteAlert(Guid elementId, string? returnUrl)
    {
        try
        {
            StatusMessage = _workspaceStore.UnmuteElement(elementId, User.Identity?.Name)
                ? "Alert un-muted - it can alarm again."
                : "That element was not muted.";
            return RedirectAfterAction(returnUrl, "/Alerts");
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            LoadViewState(populateEditorValues: false);
            return Page();
        }
    }

    private IReadOnlyList<WorkspaceAlertRow> BuildAlertRows(MonitoringWorkspaceSnapshot snapshot)
    {
        return snapshot.Alerts
            .OrderByDescending(alert => alert.IsActive)
            .ThenByDescending(alert => alert.LastSeenUtc)
            .Select(alert => new WorkspaceAlertRow(
                alert.Id,
                alert.ElementId,
                alert.ElementKind,
                alert.ElementName,
                alert.ElementPath,
                alert.State,
                GetAlertStateKey(alert.State),
                FormatSensorStateLabel(alert.State),
                alert.Message,
                alert.FirstSeenUtc.ToDisplay().ToString("g"),
                alert.LastSeenUtc.ToDisplay().ToString("g"),
                alert.IsActive,
                alert.IsAcknowledged,
                alert.IsRecovered,
                alert.AcknowledgedUtc?.ToDisplay().ToString("g"),
                alert.AcknowledgedBy,
                alert.RecoveredUtc?.ToDisplay().ToString("g"),
                alert.ResolvedUtc?.ToDisplay().ToString("g"),
                alert.FirstSeenUtc.ToUnixTimeMilliseconds(),
                alert.LastSeenUtc.ToUnixTimeMilliseconds()))
            .ToArray();
    }

    private static string GetAlertStateKey(SensorState state)
    {
        return state switch
        {
            SensorState.Warning => "warning",
            SensorState.Critical => "error",
            SensorState.Paused => MonitoringStatePresentation.PausedKey,
            SensorState.Disabled => "disabled",
            SensorState.Healthy => "ok",
            SensorState.Unknown => MonitoringStatePresentation.UnknownKey,
            _ => "error"
        };
    }
}
