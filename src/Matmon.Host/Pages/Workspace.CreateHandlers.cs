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
    public IActionResult OnPostCreateProbe()
    {
        try
        {
            if (!_licenseService.CanAddProbe(out var licenseReason))
            {
                ErrorMessage = licenseReason;
                LoadViewState(populateEditorValues: false);
                return Page();
            }

            var probe = _workspaceStore.CreateProbe(NewProbe.ParentId, NewProbe.Name, NewProbe.Description);
            StatusMessage = $"Probe '{probe.Name}' created. The install script is ready.";
            var backUrl = GetSafeReturnUrl("/Config?tab=probes");
            return RedirectToPage("/ProbeInstall", new { probeId = probe.Id, returnUrl = backUrl });
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            LoadViewState(populateEditorValues: false);
            return Page();
        }
    }

    public IActionResult OnPostCreateFolder()
    {
        try
        {
            var folder = _workspaceStore.CreateFolder(RequireParentId(NewFolder.ParentId, "Folder"), NewFolder.Name, NewFolder.Description);
            StatusMessage = $"Folder '{folder.Name}' created.";
            return RedirectAfterAction(ReturnUrl, "/Monitoring");
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            LoadViewState(populateEditorValues: false);
            return Page();
        }
    }

    public IActionResult OnPostCreateHost()
    {
        try
        {
            var host = _workspaceStore.CreateHost(RequireParentId(NewHost.ParentId, "Host"), NewHost.Name, NewHost.Address, NewHost.Description);
            StatusMessage = $"Host '{host.Name}' created.";
            return RedirectAfterAction(ReturnUrl, "/Monitoring");
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            LoadViewState(populateEditorValues: false);
            return Page();
        }
    }

    public IActionResult OnPostCreateSensor()
    {
        try
        {
            if (!_licenseService.CanAddSensor(out var licenseReason))
            {
                ErrorMessage = licenseReason;
                LoadViewState(populateEditorValues: false);
                return Page();
            }

            NormalizeHeartbeatSensorTarget(NewSensor);

            ApplySnmpWalkSelectionsToParameters(NewSensor);
            var selectedTemplate = ResolveSelectedSensorTemplate();
            var inheritedSettings = BuildSensorInheritedSettings(NewSensor, selectedTemplate);
            var createSettings = BuildTransientSensorExecutionSettings(NewSensor);
            MonitoringSettings.StripInheritedValues(createSettings, inheritedSettings);

            var sensor = _workspaceStore.CreateSensor(
                RequireParentId(NewSensor.ParentId, "Sensor"),
                NewSensor.Name,
                NewSensor.SensorTypeKey,
                NewSensor.Target,
                NewSensor.Description,
                createSettings);

            // Apply the post-create mutations under the store lock (tags + template copy write the
            // element's settings/tags in place - doing it outside the lock would race readers).
            _workspaceStore.UpdateElement(sensor.Id, element =>
            {
                element.Tags = MonitoringTagResolver.Parse(NewSensor.TagsText);

                if (selectedTemplate is not null)
                {
                    // Copy the template's values into the new sensor (the form values the user saw/edited
                    // win) and remember the origin so it can be restored later - no live link. Template
                    // tags merge into the user's tags inside ApplyTemplateCopy.
                    ApplyTemplateCopy(element, selectedTemplate, elementWins: true);
                }
            });

            StatusMessage = $"Sensor '{sensor.Name}' created.";
            return RedirectAfterAction(ReturnUrl, "/Monitoring");
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            LoadViewState(populateEditorValues: false);
            return Page();
        }
    }

    public IActionResult OnPostPreviewSensorFields()
    {
        try
        {
            LoadViewState(populateEditorValues: false);
            StatusMessage = null;
            ErrorMessage = null;
            // The form is redisplayed with values the server just recomputed (suggested
            // name, applied template defaults, rebuilt channel/parameter fields). Tag
            // helpers prefer the posted ModelState over the model on redisplay, which
            // would mask those changes (e.g. the name would stay "Ping 3" after switching
            // the type to HTTP), so drop ModelState and render straight from the model.
            ModelState.Clear();
            return Page();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            LoadViewState(populateEditorValues: false);
            ModelState.Clear();
            return Page();
        }
    }

    public async Task<IActionResult> OnPostDiscoverSnmpAsync()
    {
        try
        {
            if (!IsSnmpWalkSensorType(NewSensor.SensorTypeKey))
            {
                throw new InvalidOperationException("SNMP discovery is only available for SNMP and Synology sensors.");
            }

            var rootOid = string.IsNullOrWhiteSpace(NewSensor.SnmpWalkRootOid)
                ? "1.3.6.1.2.1"
                : NewSensor.SnmpWalkRootOid.Trim();
            var previousSelections = GetSelectedSnmpWalkOids(NewSensor.SnmpWalkItems);
            var discoverySettings = BuildTransientSensorExecutionSettings(NewSensor);
            var discoveryTarget = ResolveEffectiveSensorTarget(NewSensor);
            var discovered = await SnmpSensorExecutor.DiscoverAsync(
                discoveryTarget,
                discoverySettings,
                rootOid,
                TimeSpan.FromSeconds(5),
                HttpContext.RequestAborted);

            NewSensor.SnmpWalkItems = discovered
                .Select(item => new WorkspaceSnmpWalkItemInput
                {
                    Selected = item.SelectedByDefault || previousSelections.Contains(item.Oid.Trim().TrimStart('.')),
                    Oid = item.Oid,
                    Syntax = item.Syntax,
                    Value = item.Value,
                    IsNumeric = item.IsNumeric
                })
                .ToList();

            ApplySnmpWalkSelectionsToParameters(NewSensor);

            StatusMessage = discovered.Count == 0
                ? "No OIDs discovered."
                : $"Discovered {discovered.Count} OIDs.";

            LoadViewState(populateEditorValues: false);
            // Render the rebuilt walk items / applied parameters from the model, not the
            // stale posted ModelState (see OnPostPreviewSensorFields).
            ModelState.Clear();
            return Page();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            LoadViewState(populateEditorValues: false);
            ModelState.Clear();
            return Page();
        }
    }

    private void NormalizeHeartbeatSensorTarget(CreateSensorInput editor)
    {
        if (!string.Equals(editor.SensorTypeKey, ProbeHeartbeatSensorExecutor.Definition.Key, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(editor.Target))
        {
            return;
        }

        editor.Target = ResolveProbeIdForElement(editor.ParentId) ?? string.Empty;
    }

    private void EnsureCreateDefaults(MonitoringWorkspaceSnapshot snapshot, IReadOnlyList<WorkspaceNodeRow> nodes, bool populateEditorValues)
    {
        if (NewProbe.ParentId is null)
        {
            NewProbe.ParentId = snapshot.RootProbe.Id;
        }

        if (NewFolder.ParentId is null)
        {
            NewFolder.ParentId = snapshot.RootProbe.Id;
        }

        if (NewHost.ParentId is null)
        {
            NewHost.ParentId = snapshot.RootProbe.Id;
        }

        if (NewSensor.ParentId is null)
        {
            NewSensor.ParentId = snapshot.RootProbe.Id;
        }

        var isGetRequest = string.Equals(Request?.Method, "GET", StringComparison.OrdinalIgnoreCase);

        // On the initial GET the node the user clicked "Add …" on arrives as SelectedId - pre-select
        // it as the parent, clamped to the kinds each create form allows. Previously only Sensor did
        // this, so Folder/Host always fell back to the root probe (the wrong parent).
        if (isGetRequest && SelectedId is Guid selectedParentId)
        {
            if (IsSensorCreationPath())
            {
                var selectedParent = nodes.FirstOrDefault(node =>
                    node.Id == selectedParentId &&
                    node.Kind is MonitoringElementKind.Probe or MonitoringElementKind.Folder or MonitoringElementKind.Host);
                if (selectedParent is not null)
                {
                    NewSensor.ParentId = selectedParent.Id;
                }
            }
            else if (IsFolderCreationPath())
            {
                var selectedParent = nodes.FirstOrDefault(node =>
                    node.Id == selectedParentId &&
                    node.Kind is MonitoringElementKind.Probe or MonitoringElementKind.Folder);
                if (selectedParent is not null)
                {
                    NewFolder.ParentId = selectedParent.Id;
                }
            }
            else if (IsHostCreationPath())
            {
                var selectedParent = nodes.FirstOrDefault(node =>
                    node.Id == selectedParentId &&
                    node.Kind is MonitoringElementKind.Probe or MonitoringElementKind.Folder);
                if (selectedParent is not null)
                {
                    NewHost.ParentId = selectedParent.Id;
                }
            }
        }

        if (isGetRequest &&
            IsSensorCreationPath() &&
            NewSensor.TemplateId is null &&
            SelectedTemplateId is Guid selectedTemplateId)
        {
            var selectedTemplate = snapshot.Templates.FirstOrDefault(candidate =>
                candidate.Id == selectedTemplateId &&
                candidate.TargetKind == MonitoringTemplateScope.Sensor);

            if (selectedTemplate is not null)
            {
                NewSensor.TemplateId = selectedTemplate.Id;
            }
        }

        if (!isGetRequest)
        {
            NormalizeSelectedSensorTemplateForSensorType(snapshot);
        }

        if (string.Equals(NewSensor.SensorTypeKey, ProbeHeartbeatSensorExecutor.Definition.Key, StringComparison.OrdinalIgnoreCase))
        {
            var heartbeatParent = nodes.FirstOrDefault(node =>
                node.Kind == MonitoringElementKind.Probe &&
                !string.Equals(node.ProbeId, snapshot.RootProbe.ProbeId, StringComparison.OrdinalIgnoreCase));

            if (heartbeatParent is not null && NewSensor.ParentId == snapshot.RootProbe.Id)
            {
                NewSensor.ParentId = heartbeatParent.Id;
            }
        }

        if (IsSnmpWalkSensorType(NewSensor.SensorTypeKey))
        {
            var defaultRootOid = GetDefaultSnmpWalkRootOid(NewSensor.SensorTypeKey);
            if (string.IsNullOrWhiteSpace(NewSensor.SnmpWalkRootOid) ||
                string.Equals(NewSensor.SnmpWalkRootOid.Trim(), "1.3.6.1.2.1", StringComparison.OrdinalIgnoreCase))
            {
                NewSensor.SnmpWalkRootOid = defaultRootOid;
            }
        }

        NewSensor.SensorChannelMode = GetSensorChannelMode(NewSensor.SensorTypeKey);
        NormalizeHeartbeatSensorTarget(NewSensor);
        ApplySelectedSensorTemplateDefaults(snapshot, populateEditorValues);
        NewSensor.SensorChannelMode = GetSensorChannelMode(NewSensor.SensorTypeKey);
        NormalizeHeartbeatSensorTarget(NewSensor);
        EnsureSuggestedSensorName(snapshot);

        if (populateEditorValues && string.IsNullOrWhiteSpace(NewNotificationRule.Name))
        {
            NewNotificationRule.Name = "Notification rule";
        }

        if (populateEditorValues && NewNotificationRule.TriggerStates.Count == 0)
        {
            NewNotificationRule.TriggerStates.Add(SensorState.Warning);
            NewNotificationRule.TriggerStates.Add(SensorState.Critical);
        }

        if (populateEditorValues && NewNotificationRule.SenderId is null)
        {
            NewNotificationRule.SenderId = snapshot.NotificationSenders.FirstOrDefault()?.Id;
        }

        if (populateEditorValues && NewNotificationRule.ReceiverId is null)
        {
            NewNotificationRule.ReceiverId = snapshot.NotificationReceivers.FirstOrDefault()?.Id;
        }

        if (string.IsNullOrWhiteSpace(NewNotificationSender.Name))
        {
            NewNotificationSender.Name = "Email sender";
        }

        if (string.IsNullOrWhiteSpace(NewNotificationReceiver.Name))
        {
            NewNotificationReceiver.Name = "Email receiver";
        }
    }

    private bool IsSensorCreationPath()
    {
        var path = Request?.Path.Value ?? string.Empty;
        return path.EndsWith("/monitoring/sensor/new", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/monitoring/sensor/assistant", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsFolderCreationPath() =>
        (Request?.Path.Value ?? string.Empty).EndsWith("/monitoring/folder/new", StringComparison.OrdinalIgnoreCase);

    private bool IsHostCreationPath() =>
        (Request?.Path.Value ?? string.Empty).EndsWith("/monitoring/host/new", StringComparison.OrdinalIgnoreCase);

    private void EnsureSuggestedSensorName(MonitoringWorkspaceSnapshot snapshot)
    {
        if (!IsSensorCreationPath())
        {
            return;
        }

        if (!NewSensor.NameAutoGenerated && !string.IsNullOrWhiteSpace(NewSensor.Name))
        {
            return;
        }

        // No trailing counter - the type name (plus template) is the suggestion; duplicates are fine.
        NewSensor.Name = ResolveSuggestedSensorNameBase(snapshot);
        NewSensor.NameAutoGenerated = true;
    }

    private string ResolveSuggestedSensorNameBase(MonitoringWorkspaceSnapshot snapshot)
    {
        var definition = FindSensorDefinition(snapshot.SensorDefinitions, NewSensor.SensorTypeKey);
        var typeName = !string.IsNullOrWhiteSpace(definition?.DisplayName)
            ? definition.DisplayName.Trim()
            : string.IsNullOrWhiteSpace(NewSensor.SensorTypeKey)
                ? "Sensor"
                : HumanizeIdentifier(NewSensor.SensorTypeKey);

        // With a template, append its name: "HTTP – Webserver Check". Without one, just the type.
        var template = ResolveSelectedSensorTemplate(snapshot, requireSensorTypeMatch: true);
        if (!string.IsNullOrWhiteSpace(template?.Name))
        {
            return $"{typeName} – {template.Name.Trim()}";
        }

        return typeName;
    }

    private static string HumanizeIdentifier(string value)
    {
        var normalized = value.Trim()
            .Replace('_', ' ')
            .Replace('-', ' ')
            .Replace('.', ' ')
            .Replace('/', ' ');
        normalized = Regex.Replace(normalized, "\\s+", " ").Trim();

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "Sensor";
        }

        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(normalized.ToLowerInvariant());
    }
}
