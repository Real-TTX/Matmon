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
    public IActionResult OnPostCreateNotificationRule()
    {
        NotificationRule? createdRule = null;

        try
        {
            createdRule = _workspaceStore.CreateNotificationRule(NewNotificationRule.Name);
            ApplyNotificationRuleEditor(createdRule, NewNotificationRule);
            SynchronizeNotificationRuleLegacyFields(createdRule);
            _workspaceStore.Save();
            StatusMessage = $"Notification rule '{createdRule.Name}' created.";
            return RedirectAfterAction(ReturnUrl, "/Notifications");
        }
        catch (Exception ex)
        {
            if (createdRule is not null)
            {
                try
                {
                    _workspaceStore.DeleteNotificationRule(createdRule.Id);
                }
                catch
                {
                    // Ignore cleanup failures and surface the original error.
                }
            }

            ErrorMessage = ex.Message;
            LoadViewState(populateEditorValues: false);
            return Page();
        }
    }

    public IActionResult OnPostSaveNotificationRule()
    {
        try
        {
            if (NotificationRuleEditor.Id == Guid.Empty)
            {
                throw new InvalidOperationException("No notification rule selected.");
            }

            var rule = _workspaceStore.FindNotificationRule(NotificationRuleEditor.Id)
                ?? throw new InvalidOperationException("Notification rule not found.");

            ApplyNotificationRuleEditor(rule, NotificationRuleEditor);
            SynchronizeNotificationRuleLegacyFields(rule);
            _workspaceStore.Save();
            StatusMessage = $"Notification rule '{rule.Name}' saved.";
            return RedirectToPage(new { selectedNotificationRuleId = rule.Id, selectedId = SelectedId, selectedTemplateId = SelectedTemplateId });
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            LoadViewState(populateEditorValues: false);
            return Page();
        }
    }

    /// <summary>
    /// Sends ONE notification through the rule as it is in the form (saved or not) - its sender, its receiver, its
    /// templates, filled with the same data as the preview and marked "[Test]". It goes out now and the page says
    /// whether it arrived or why not; that is the only way to know a webhook token or an SMTP password works before an
    /// alert depends on it.
    /// </summary>
    public async Task<IActionResult> OnPostTestNotificationRuleAsync()
    {
        var isEdit = Request.HasFormContentType && Request.Form.Keys.Any(key => key.StartsWith("NotificationRuleEditor.", StringComparison.Ordinal));
        var name = isEdit ? NotificationRuleEditor.Name : NewNotificationRule.Name;
        var route = isEdit
            ? new NotificationRoute(name, NotificationRuleEditor.SenderId, NotificationRuleEditor.ReceiverId, null,
                TestPrefix(NotificationRuleEditor.SubjectTemplate, NotificationTemplateCatalog.DefaultSubjectTemplate),
                NotificationRuleEditor.TextTemplate, NotificationRuleEditor.HtmlTemplate,
                TestPrefix(NotificationRuleEditor.ShortTemplate, NotificationTemplateCatalog.DefaultShortTemplate))
            : new NotificationRoute(name, NewNotificationRule.SenderId, NewNotificationRule.ReceiverId, null,
                TestPrefix(NewNotificationRule.SubjectTemplate, NotificationTemplateCatalog.DefaultSubjectTemplate),
                NewNotificationRule.TextTemplate, NewNotificationRule.HtmlTemplate,
                TestPrefix(NewNotificationRule.ShortTemplate, NotificationTemplateCatalog.DefaultShortTemplate));

        try
        {
            var snapshot = _workspaceStore.Workspace;
            var nodes = BuildNodeRows(
                snapshot.RootProbe,
                snapshot.Templates.ToDictionary(template => template.Id),
                new Dictionary<Guid, TelemetrySeriesSnapshot>(),
                new HashSet<Guid>());
            var (_, context) = BuildNotificationPreviewContext(
                string.IsNullOrWhiteSpace(name) ? "Test" : name,
                isEdit ? NotificationRuleEditor.TargetElementId : NewNotificationRule.TargetElementId,
                isEdit ? NotificationRuleEditor.IncludeDescendants : NewNotificationRule.IncludeDescendants,
                isEdit ? NotificationRuleEditor.TriggerStates : NewNotificationRule.TriggerStates,
                snapshot,
                nodes,
                _workspaceStore.GetLatestSensorObservations(),
                DateTimeOffset.UtcNow);

            // An SMS or a call goes out for critical problems only - the test pretends to be one, or it could never be tested.
            if (NotificationDeliveryFactory.IsPhoneRoute(route, snapshot))
            {
                NotificationRendering.SetStateValues(context, SensorState.Critical, recovered: false);
                context.SetValue("state.label", MonitoringStatePresentation.Label(SensorState.Critical));
                context.SetValue("state.key", MonitoringStatePresentation.Key(SensorState.Critical));
            }

            var services = HttpContext.RequestServices;
            var (delivery, problem) = services.GetRequiredService<NotificationDeliveryFactory>().Create(route, context, snapshot, label: "test");
            if (delivery is null)
            {
                ErrorMessage = $"Nothing would be sent: {problem}.";
            }
            else
            {
                await services.GetRequiredService<NotificationTransportClient>().SendAsync(delivery, HttpContext.RequestAborted);
                StatusMessage = $"Test notification sent ({delivery.Transport.ToString().ToLowerInvariant()}) to {delivery.RecipientLabel}.";
            }
        }
        catch (NotificationDeliveryException ex)
        {
            ErrorMessage = $"The test notification did not go out: {ex.Message}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ErrorMessage = $"The test notification did not go out: {ex.Message}";
        }

        LoadViewState(populateEditorValues: false);
        return Page();
    }

    private static string TestPrefix(string? template, string fallback) =>
        "[Test] " + (string.IsNullOrWhiteSpace(template) ? fallback : template);

    public IActionResult OnPostDeleteNotificationRule()
    {
        try
        {
            if (NotificationRuleEditor.Id == Guid.Empty)
            {
                throw new InvalidOperationException("No notification rule selected.");
            }

            var rule = _workspaceStore.FindNotificationRule(NotificationRuleEditor.Id)
                ?? throw new InvalidOperationException("Notification rule not found.");

            if (!_workspaceStore.DeleteNotificationRule(rule.Id))
            {
                throw new InvalidOperationException("The notification rule could not be deleted.");
            }

            StatusMessage = $"Notification rule '{rule.Name}' deleted.";
            return RedirectToPage(new { selectedId = SelectedId, selectedTemplateId = SelectedTemplateId });
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            LoadViewState(populateEditorValues: false);
            return Page();
        }
    }

    public IActionResult OnPostCreateNotificationSender()
    {
        NotificationSender? createdSender = null;

        try
        {
            createdSender = _workspaceStore.CreateNotificationSender(NewNotificationSender.Name);
            ApplyNotificationSenderEditor(createdSender, NewNotificationSender);
            _workspaceStore.Save();
            StatusMessage = $"Notification sender '{createdSender.Name}' created.";
            return RedirectAfterAction(ReturnUrl, "/NotificationSettings");
        }
        catch (Exception ex)
        {
            if (createdSender is not null)
            {
                try
                {
                    _workspaceStore.DeleteNotificationSender(createdSender.Id);
                }
                catch
                {
                    // Ignore cleanup failures and surface the original error.
                }
            }

            ErrorMessage = ex.Message;
            LoadViewState(populateEditorValues: false);
            return Page();
        }
    }

    public IActionResult OnPostSaveNotificationSender()
    {
        try
        {
            if (NotificationSenderEditor.Id == Guid.Empty)
            {
                throw new InvalidOperationException("No notification sender selected.");
            }

            var sender = _workspaceStore.FindNotificationSender(NotificationSenderEditor.Id)
                ?? throw new InvalidOperationException("Notification sender not found.");

            ApplyNotificationSenderEditor(sender, NotificationSenderEditor);
            _workspaceStore.Save();
            StatusMessage = $"Notification sender '{sender.Name}' saved.";
            return RedirectToPage(new { selectedNotificationSenderId = sender.Id, selectedId = SelectedId, selectedTemplateId = SelectedTemplateId, selectedNotificationRuleId = SelectedNotificationRuleId });
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            LoadViewState(populateEditorValues: false);
            return Page();
        }
    }

    public IActionResult OnPostDeleteNotificationSender()
    {
        try
        {
            if (NotificationSenderEditor.Id == Guid.Empty)
            {
                throw new InvalidOperationException("No notification sender selected.");
            }

            var sender = _workspaceStore.FindNotificationSender(NotificationSenderEditor.Id)
                ?? throw new InvalidOperationException("Notification sender not found.");

            // Built in, one per instance (it would come back at the next start) - switching it off is the way.
            if (sender.Kind == NotificationEndpointKind.Push)
            {
                throw new InvalidOperationException("The built-in browser notification sender cannot be deleted - switch it off instead.");
            }

            // A sender in use can't be deleted - tell the user which rules to repoint first (the store just
            // returns false otherwise, which reads as a mysterious "can't delete").
            var referencingRules = _workspaceStore.Workspace.NotificationRules.Count(rule => rule.SenderId == sender.Id);
            if (referencingRules > 0)
            {
                throw new InvalidOperationException(
                    $"Sender '{sender.Name}' is used by {referencingRules} notification rule(s). Remove or repoint them first, then delete the sender.");
            }

            if (!_workspaceStore.DeleteNotificationSender(sender.Id))
            {
                throw new InvalidOperationException("The notification sender could not be deleted.");
            }

            StatusMessage = $"Notification sender '{sender.Name}' deleted.";
            return RedirectToPage(new { selectedId = SelectedId, selectedTemplateId = SelectedTemplateId, selectedNotificationRuleId = SelectedNotificationRuleId });
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            LoadViewState(populateEditorValues: false);
            return Page();
        }
    }

    public IActionResult OnPostCreateNotificationReceiver()
    {
        NotificationReceiver? createdReceiver = null;

        try
        {
            createdReceiver = _workspaceStore.CreateNotificationReceiver(NewNotificationReceiver.Name);
            ApplyNotificationReceiverEditor(createdReceiver, NewNotificationReceiver);
            _workspaceStore.Save();
            StatusMessage = $"Notification receiver '{createdReceiver.Name}' created.";
            return RedirectAfterAction(ReturnUrl, "/NotificationReceivers");
        }
        catch (Exception ex)
        {
            if (createdReceiver is not null)
            {
                try
                {
                    _workspaceStore.DeleteNotificationReceiver(createdReceiver.Id);
                }
                catch
                {
                    // Ignore cleanup failures and surface the original error.
                }
            }

            ErrorMessage = ex.Message;
            LoadViewState(populateEditorValues: false);
            return Page();
        }
    }

    public IActionResult OnPostSaveNotificationReceiver()
    {
        try
        {
            if (NotificationReceiverEditor.Id == Guid.Empty)
            {
                throw new InvalidOperationException("No notification receiver selected.");
            }

            var receiver = _workspaceStore.FindNotificationReceiver(NotificationReceiverEditor.Id)
                ?? throw new InvalidOperationException("Notification receiver not found.");

            ApplyNotificationReceiverEditor(receiver, NotificationReceiverEditor);
            _workspaceStore.Save();
            StatusMessage = $"Notification receiver '{receiver.Name}' saved.";
            return RedirectToPage(new { selectedNotificationReceiverId = receiver.Id, selectedId = SelectedId, selectedTemplateId = SelectedTemplateId, selectedNotificationRuleId = SelectedNotificationRuleId });
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            LoadViewState(populateEditorValues: false);
            return Page();
        }
    }

    public IActionResult OnPostDeleteNotificationReceiver()
    {
        try
        {
            if (NotificationReceiverEditor.Id == Guid.Empty)
            {
                throw new InvalidOperationException("No notification receiver selected.");
            }

            var receiver = _workspaceStore.FindNotificationReceiver(NotificationReceiverEditor.Id)
                ?? throw new InvalidOperationException("Notification receiver not found.");

            if (!_workspaceStore.DeleteNotificationReceiver(receiver.Id))
            {
                throw new InvalidOperationException("The notification receiver could not be deleted.");
            }

            StatusMessage = $"Notification receiver '{receiver.Name}' deleted.";
            return RedirectToPage(new { selectedId = SelectedId, selectedTemplateId = SelectedTemplateId, selectedNotificationRuleId = SelectedNotificationRuleId });
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            LoadViewState(populateEditorValues: false);
            return Page();
        }
    }

    private WorkspaceNotificationRuleEditorInput BuildNotificationRuleEditor(
        NotificationRule rule,
        MonitoringWorkspaceSnapshot snapshot,
        IReadOnlyList<WorkspaceNodeRow> nodes)
    {
        return new WorkspaceNotificationRuleEditorInput
        {
            Id = rule.Id,
            Name = rule.Name,
            Enabled = rule.Enabled,
            SenderId = rule.SenderId,
            ReceiverId = rule.ReceiverId,
            TargetToken = rule.TargetTag is { Length: > 0 } tag
                ? MonitoringTargetResolver.ForTag(tag)
                : rule.TargetElementId is Guid elementId ? MonitoringTargetResolver.ForElement(elementId) : null,
            IncludeDescendants = rule.IncludeDescendants,
            TriggerStates = rule.TriggerStates.ToList(),
            CooldownMinutes = rule.CooldownMinutes,
            Threshold = rule.Threshold,
            SubjectTemplate = string.IsNullOrWhiteSpace(rule.SubjectTemplate) ? NotificationTemplateCatalog.DefaultSubjectTemplate : rule.SubjectTemplate,
            TextTemplate = string.IsNullOrWhiteSpace(rule.TextTemplate) ? NotificationTemplateCatalog.DefaultTextTemplate : rule.TextTemplate,
            HtmlTemplate = string.IsNullOrWhiteSpace(rule.HtmlTemplate) ? NotificationTemplateCatalog.DefaultHtmlTemplate : rule.HtmlTemplate,
            ShortTemplate = string.IsNullOrWhiteSpace(rule.ShortTemplate) ? NotificationTemplateCatalog.DefaultShortTemplate : rule.ShortTemplate,
            Escalations = NotificationEscalationInput.Rows(rule.Escalations),
            SenderOptions = BuildNotificationSenderOptions(snapshot.NotificationSenders, rule.SenderId),
            ReceiverOptions = BuildNotificationReceiverOptions(snapshot.NotificationReceivers, rule.ReceiverId),
            TargetOptions = BuildNotificationTargetOptions(nodes, rule.TargetElementId),
            TriggerStateOptions = BuildNotificationStateOptions(rule.TriggerStates)
        };
    }

    private WorkspaceNotificationSenderEditorInput BuildNotificationSenderEditor(NotificationSender sender)
    {
        return new WorkspaceNotificationSenderEditorInput
        {
            Id = sender.Id,
            Name = sender.Name,
            Enabled = sender.Enabled,
            Kind = sender.Kind,
            SenderName = sender.Email.SenderName,
            SenderEmail = sender.Email.SenderEmail,
            SmtpHost = sender.Email.SmtpHost,
            SmtpPort = sender.Email.SmtpPort,
            UseSsl = sender.Email.UseSsl,
            Username = sender.Email.Username,
            Password = sender.Email.Password,
            EndpointUrl = sender.Webhook.EndpointUrl,
            Secret = sender.Webhook.Secret,
            TimeoutSeconds = sender.Webhook.TimeoutSeconds,
            WebhookPreset = sender.Webhook.Preset ?? WebhookPresets.GenericKey,
            WebhookMethod = WebhookRequestBuilder.NormalizeMethod(sender.Webhook.Method),
            WebhookContentType = WebhookContentTypes.Normalize(sender.Webhook.ContentType),
            WebhookHeaders = sender.Webhook.Headers,
            // A webhook sender from before the templates has no body: show the one it actually sends.
            WebhookBody = string.IsNullOrWhiteSpace(sender.Webhook.BodyTemplate) ? WebhookRequestBuilder.DefaultJsonBody : sender.Webhook.BodyTemplate,
            WebhookSignRequests = sender.Webhook.SignRequests
        };
    }

    private WorkspaceNotificationReceiverEditorInput BuildNotificationReceiverEditor(NotificationReceiver receiver)
    {
        return new WorkspaceNotificationReceiverEditorInput
        {
            Id = receiver.Id,
            Name = receiver.Name,
            Enabled = receiver.Enabled,
            Kind = receiver.Kind,
            Target = receiver.Target,
            Secret = receiver.Secret,
            TimeoutSeconds = receiver.TimeoutSeconds
        };
    }

    private static void ApplyNotificationRuleEditor(NotificationRule rule, CreateNotificationRuleInput editor)
    {
        ApplyNotificationRuleValues(
            rule,
            editor.Name,
            editor.Enabled,
            editor.SenderId,
            editor.ReceiverId,
            editor.TargetElementId,
            editor.TargetTag,
            editor.IncludeDescendants,
            editor.TriggerStates,
            editor.CooldownMinutes,
            editor.Threshold,
            editor.SubjectTemplate,
            editor.TextTemplate,
            editor.HtmlTemplate,
            editor.ShortTemplate,
            editor.Escalations);
    }

    private static void ApplyNotificationSenderEditor(NotificationSender sender, CreateNotificationSenderInput editor)
    {
        ValidateWebhookEditor(editor, sender.Webhook.Secret);

        sender.Name = string.IsNullOrWhiteSpace(editor.Name) ? "Notification sender" : editor.Name.Trim();
        sender.Enabled = editor.Enabled;
        sender.Kind = editor.Kind;

        if (!string.IsNullOrWhiteSpace(editor.SenderName))
        {
            sender.Email.SenderName = editor.SenderName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(editor.SenderEmail))
        {
            sender.Email.SenderEmail = editor.SenderEmail.Trim();
        }

        if (!string.IsNullOrWhiteSpace(editor.SmtpHost))
        {
            sender.Email.SmtpHost = editor.SmtpHost.Trim();
        }

        if (editor.SmtpPort is int smtpPort && smtpPort > 0)
        {
            sender.Email.SmtpPort = smtpPort;
        }

        sender.Email.UseSsl = editor.UseSsl;

        if (!string.IsNullOrWhiteSpace(editor.Username))
        {
            sender.Email.Username = editor.Username.Trim();
        }

        if (!string.IsNullOrWhiteSpace(editor.Password))
        {
            sender.Email.Password = editor.Password;
        }

        if (!string.IsNullOrWhiteSpace(editor.EndpointUrl))
        {
            sender.Webhook.EndpointUrl = editor.EndpointUrl.Trim();
        }

        if (!string.IsNullOrWhiteSpace(editor.Secret))
        {
            sender.Webhook.Secret = editor.Secret.Trim();
        }

        if (editor.TimeoutSeconds is int timeout && timeout > 0)
        {
            sender.Webhook.TimeoutSeconds = timeout;
        }

        // The webhook fields are posted whole (the editor shows every one), so they are taken as they are - an emptied
        // header list or body has to stay empty.
        sender.Webhook.Preset = WebhookPresets.Find(editor.WebhookPreset)?.Key;
        sender.Webhook.Method = WebhookRequestBuilder.NormalizeMethod(editor.WebhookMethod);
        sender.Webhook.ContentType = WebhookContentTypes.Normalize(editor.WebhookContentType);
        sender.Webhook.Headers = string.IsNullOrWhiteSpace(editor.WebhookHeaders) ? null : editor.WebhookHeaders.Trim();
        sender.Webhook.BodyTemplate = string.IsNullOrWhiteSpace(editor.WebhookBody) ? null : editor.WebhookBody.Trim();
        sender.Webhook.SignRequests = editor.WebhookSignRequests;
    }

    /// <summary>Fills a posted webhook's templates with sample values once, so a missing or broken address or a body
    /// that is not JSON is reported while the form is open - not as a failed alert in the log weeks later. Runs BEFORE
    /// anything is applied: the sender being edited is the live one, and a half-applied edit would stay in memory.</summary>
    private static void ValidateWebhookEditor(CreateNotificationSenderInput editor, string? storedSecret)
    {
        if (editor.Kind != NotificationEndpointKind.Webhook)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(editor.EndpointUrl))
        {
            throw new InvalidOperationException("Enter the webhook address.");
        }

        var settings = new WebhookNotificationSettings
        {
            EndpointUrl = editor.EndpointUrl.Trim(),
            Method = editor.WebhookMethod,
            ContentType = editor.WebhookContentType,
            Headers = editor.WebhookHeaders,
            BodyTemplate = editor.WebhookBody,
            SignRequests = editor.WebhookSignRequests,
            Secret = string.IsNullOrWhiteSpace(editor.Secret) ? storedSecret ?? "sample-secret" : editor.Secret
        };
        WebhookRequestBuilder.Build(settings, NotificationTemplateCatalog.SampleValues(), "sample-receiver");
    }

    private static void ApplyNotificationReceiverEditor(NotificationReceiver receiver, CreateNotificationReceiverInput editor)
    {
        // Checked BEFORE anything is applied - the receiver being edited is the live one.
        var target = string.IsNullOrWhiteSpace(editor.Target) ? receiver.Target : editor.Target.Trim();
        if (PhoneNumbers.IsPhoneKind(editor.Kind))
        {
            target = PhoneNumbers.Normalize(target)
                ?? throw new InvalidOperationException("Enter the phone number in international format, e.g. +49 170 1234567.");
        }

        receiver.Name = string.IsNullOrWhiteSpace(editor.Name) ? "Notification receiver" : editor.Name.Trim();
        receiver.Enabled = editor.Enabled;
        receiver.Kind = editor.Kind;
        receiver.Target = target;

        if (!string.IsNullOrWhiteSpace(editor.Secret))
        {
            receiver.Secret = editor.Secret.Trim();
        }

        if (editor.TimeoutSeconds is int timeout && timeout > 0)
        {
            receiver.TimeoutSeconds = timeout;
        }
    }

    private static void ApplyNotificationRuleEditor(NotificationRule rule, WorkspaceNotificationRuleEditorInput editor)
    {
        ApplyNotificationRuleValues(
            rule,
            editor.Name,
            editor.Enabled,
            editor.SenderId,
            editor.ReceiverId,
            editor.TargetElementId,
            editor.TargetTag,
            editor.IncludeDescendants,
            editor.TriggerStates,
            editor.CooldownMinutes,
            editor.Threshold,
            editor.SubjectTemplate,
            editor.TextTemplate,
            editor.HtmlTemplate,
            editor.ShortTemplate,
            editor.Escalations);
    }

    private static void ApplyNotificationRuleValues(
        NotificationRule rule,
        string name,
        bool enabled,
        Guid? senderId,
        Guid? receiverId,
        Guid? targetElementId,
        string? targetTag,
        bool includeDescendants,
        IEnumerable<SensorState>? triggerStates,
        int? cooldownMinutes,
        int? threshold,
        string? subjectTemplate,
        string? textTemplate,
        string? htmlTemplate,
        string? shortTemplate,
        IEnumerable<NotificationEscalationInput>? escalations)
    {
        var triggerStateList = (triggerStates ?? Enumerable.Empty<SensorState>()).Distinct().ToList();
        if (triggerStateList.Count == 0)
        {
            throw new InvalidOperationException("At least one trigger state must be selected.");
        }

        rule.Name = string.IsNullOrWhiteSpace(name) ? "Notification rule" : name.Trim();
        rule.Enabled = enabled;
        rule.SenderId = senderId;
        rule.ReceiverId = receiverId;
        // One target or the other: a tag wins and clears the element, so a rule never carries both.
        var tag = MonitoringTagResolver.Normalize(string.IsNullOrWhiteSpace(targetTag) ? [] : [targetTag]).FirstOrDefault();
        rule.TargetTag = tag;
        rule.TargetElementId = tag is null ? targetElementId : null;
        rule.IncludeDescendants = includeDescendants;
        rule.CooldownMinutes = cooldownMinutes is int cooldown && cooldown > 0 ? cooldown : null;
        rule.Threshold = threshold is int t && t > 0 ? t : null;
        rule.SubjectTemplate = string.IsNullOrWhiteSpace(subjectTemplate) ? string.Empty : subjectTemplate;
        rule.TextTemplate = string.IsNullOrWhiteSpace(textTemplate) ? string.Empty : textTemplate;
        rule.HtmlTemplate = string.IsNullOrWhiteSpace(htmlTemplate) ? string.Empty : htmlTemplate;
        // The default is stored as empty, so a rule keeps following the default when it changes.
        rule.ShortTemplate = string.IsNullOrWhiteSpace(shortTemplate) || shortTemplate.Trim() == NotificationTemplateCatalog.DefaultShortTemplate ? string.Empty : shortTemplate.Trim();
        rule.Escalations = NotificationEscalationInput.ToSteps(escalations);
        rule.TriggerStates.Clear();

        foreach (var state in triggerStateList)
        {
            rule.TriggerStates.Add(state);
        }
    }

    private void SynchronizeNotificationRuleLegacyFields(NotificationRule rule)
    {
        if (rule.SenderId is Guid senderId)
        {
            var sender = _workspaceStore.FindNotificationSender(senderId);
            if (sender is not null)
            {
                rule.ChannelKind = sender.Kind == NotificationEndpointKind.Webhook
                    ? NotificationChannelKind.Webhook
                    : NotificationChannelKind.Email;
            }
        }

        if (rule.ReceiverId is Guid receiverId)
        {
            var receiver = _workspaceStore.FindNotificationReceiver(receiverId);
            if (receiver is not null)
            {
                rule.Recipient = receiver.Target;
            }
        }
    }

    private static void ApplyEmailSettings(EmailNotificationSettings settings, EmailNotificationSettingsInput input)
    {
        if (!string.IsNullOrWhiteSpace(input.SenderName))
        {
            settings.SenderName = input.SenderName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(input.SenderEmail))
        {
            settings.SenderEmail = input.SenderEmail.Trim();
        }

        if (!string.IsNullOrWhiteSpace(input.SmtpHost))
        {
            settings.SmtpHost = input.SmtpHost.Trim();
        }

        if (input.SmtpPort is int smtpPort && smtpPort > 0)
        {
            settings.SmtpPort = smtpPort;
        }

        settings.UseSsl = input.UseSsl;

        if (!string.IsNullOrWhiteSpace(input.Username))
        {
            settings.Username = input.Username.Trim();
        }

        if (!string.IsNullOrWhiteSpace(input.Password))
        {
            settings.Password = input.Password;
        }
    }

    private static void ApplyWebhookSettings(WebhookNotificationSettings settings, WebhookNotificationSettingsInput input)
    {
        if (!string.IsNullOrWhiteSpace(input.EndpointUrl))
        {
            settings.EndpointUrl = input.EndpointUrl.Trim();
        }

        if (!string.IsNullOrWhiteSpace(input.Secret))
        {
            settings.Secret = input.Secret.Trim();
        }

        if (input.TimeoutSeconds is int timeout && timeout > 0)
        {
            settings.TimeoutSeconds = timeout;
        }
    }

    private static List<SelectListItem> BuildNotificationTargetOptions(
        IReadOnlyList<WorkspaceNodeRow> nodes,
        Guid? selectedTargetId)
    {
        var options = new List<SelectListItem>
        {
            new("All workspace", string.Empty, selectedTargetId is null)
        };

        options.AddRange(nodes.Select(node => new SelectListItem(
            $"{node.Kind}: {node.Path}",
            node.Id.ToString(),
            node.Id == selectedTargetId)));

        return options;
    }

    private static List<SelectListItem> BuildNotificationStateOptions(IEnumerable<SensorState> selectedStates)
    {
        var selected = selectedStates.ToHashSet();
        return Enum.GetValues<SensorState>()
            .Where(state => state is not SensorState.Paused and not SensorState.Unknown)
            .Select(state => new SelectListItem(FormatSensorStateLabel(state), state.ToString(), selected.Contains(state)))
            .ToList();
    }

    private static List<SelectListItem> BuildNotificationSenderOptions(
        IReadOnlyList<NotificationSender> senders,
        Guid? selectedSenderId)
    {
        var options = new List<SelectListItem>
        {
            new("Select sender", string.Empty, selectedSenderId is null)
        };

        options.AddRange(senders.Select(sender => new SelectListItem(
            $"{sender.Name} ({sender.Kind})",
            sender.Id.ToString(),
            sender.Id == selectedSenderId)));

        return options;
    }

    private static List<SelectListItem> BuildNotificationReceiverOptions(
        IReadOnlyList<NotificationReceiver> receivers,
        Guid? selectedReceiverId)
    {
        var options = new List<SelectListItem>
        {
            new("Select receiver", string.Empty, selectedReceiverId is null)
        };

        // Built-in virtual receivers → all enabled users / admins / operators (resolved at send time).
        options.AddRange(NotificationReceiverDefaults.All.Select(builtIn =>
            new SelectListItem(builtIn.Name, builtIn.Id.ToString(), builtIn.Id == selectedReceiverId)));

        options.AddRange(receivers.Select(receiver => new SelectListItem(
            $"{receiver.Name} ({receiver.Kind})",
            receiver.Id.ToString(),
            receiver.Id == selectedReceiverId)));

        return options;
    }

    private IReadOnlyList<WorkspaceNotificationRuleRow> BuildNotificationRuleRows(
        MonitoringWorkspaceSnapshot snapshot,
        IReadOnlyList<WorkspaceNodeRow> nodes)
    {
        return snapshot.NotificationRules
            .Select(rule => new WorkspaceNotificationRuleRow(
                rule.Id,
                rule.Name,
                rule.Enabled,
                BuildNotificationSenderSummary(snapshot.NotificationSenders, rule.SenderId, rule.ChannelKind),
                BuildNotificationReceiverSummary(snapshot.NotificationReceivers, rule.ReceiverId, rule.ChannelKind, rule.Recipient),
                BuildNotificationTargetSummary(rule, nodes),
                BuildNotificationTriggerSummary(rule.TriggerStates),
                BuildNotificationCooldownSummary(rule.CooldownMinutes, rule.Threshold)
                    + (rule.Escalations.Count == 0 ? string.Empty : $" · escalates after {string.Join(", ", rule.Escalations.Select(step => FormatEscalationMinutes(step.AfterMinutes)))}"),
                RuleChannelLabel(snapshot.NotificationSenders, rule)))
            .ToArray();
    }

    private IReadOnlyList<WorkspaceNotificationSenderRow> BuildNotificationSenderRows(MonitoringWorkspaceSnapshot snapshot)
    {
        return snapshot.NotificationSenders
            .Select(sender => new WorkspaceNotificationSenderRow(
                sender.Id,
                sender.Name,
                sender.Enabled,
                sender.Kind.ToString(),
                BuildNotificationSenderSummary(snapshot.NotificationSenders, sender.Id, sender.Kind == NotificationEndpointKind.Webhook ? NotificationChannelKind.Webhook : NotificationChannelKind.Email)))
            .ToArray();
    }

    private IReadOnlyList<WorkspaceNotificationReceiverRow> BuildNotificationReceiverRows(MonitoringWorkspaceSnapshot snapshot)
    {
        // Built-in virtual receiver groups (All users / All admins / All operators) shown first as read-only rows,
        // so they're discoverable in the Receivers tab - not just inside the pickers. Resolved to real e-mails at send time.
        var builtIns = NotificationReceiverDefaults.All.Select(builtIn => new WorkspaceNotificationReceiverRow(
            builtIn.Id,
            builtIn.Name,
            Enabled: true,
            Kind: "Group",
            Target: "resolved to matching users' e-mails at send time",
            Summary: "Built-in group",
            IsBuiltIn: true));

        var real = snapshot.NotificationReceivers
            .Select(receiver => new WorkspaceNotificationReceiverRow(
                receiver.Id,
                receiver.Name,
                receiver.Enabled,
                receiver.Kind.ToString(),
                receiver.Target,
                BuildNotificationReceiverSummary(snapshot.NotificationReceivers, receiver.Id, receiver.Kind == NotificationEndpointKind.Webhook ? NotificationChannelKind.Webhook : NotificationChannelKind.Email, receiver.Target)));

        return builtIns.Concat(real).ToArray();
    }

    private static string BuildNotificationTargetSummary(NotificationRule rule, IReadOnlyList<WorkspaceNodeRow> nodes)
    {
        if (rule.TargetTag is { Length: > 0 } tag)
        {
            return $"tag #{tag}";
        }

        if (rule.TargetElementId is not Guid targetId)
        {
            return "all workspace";
        }

        var target = nodes.FirstOrDefault(node => node.Id == targetId);
        if (target is null)
        {
            return "missing target";
        }

        return rule.IncludeDescendants
            ? $"{target.Path} (subtree)"
            : target.Path;
    }

    private static string BuildNotificationSenderSummary(
        IReadOnlyList<NotificationSender> senders,
        Guid? senderId,
        NotificationChannelKind legacyKind)
    {
        var sender = senderId is Guid id
            ? senders.FirstOrDefault(candidate => candidate.Id == id)
            : null;

        if (sender is null)
        {
            return legacyKind.ToString().ToLowerInvariant();
        }

        return sender.Kind switch
        {
            NotificationEndpointKind.Webhook => $"{sender.Name} · webhook",
            NotificationEndpointKind.Cloud => $"{sender.Name} · cloud",
            NotificationEndpointKind.Push => $"{sender.Name} · browser",
            _ => $"{sender.Name} · email"
        };
    }

    /// <summary>The channel a rule really uses - its SENDER decides (the legacy ChannelKind on the rule gates nothing and said
    /// "Email" for a browser or webhook rule): no sender = the default SMTP.</summary>
    private static string RuleChannelLabel(IReadOnlyList<NotificationSender> senders, NotificationRule rule) =>
        (rule.SenderId is Guid senderId ? senders.FirstOrDefault(sender => sender.Id == senderId)?.Kind : null) switch
        {
            NotificationEndpointKind.Push => "Browser",
            NotificationEndpointKind.Webhook => "Webhook",
            NotificationEndpointKind.Cloud => "Cloud",
            _ => "Email"
        };

    private static string BuildNotificationReceiverSummary(
        IReadOnlyList<NotificationReceiver> receivers,
        Guid? receiverId,
        NotificationChannelKind legacyKind,
        string legacyRecipient)
    {
        // A built-in user group is no entry of the receivers list - it used to read "email · unset".
        if (receiverId is Guid groupId && NotificationReceiverDefaults.Find(groupId) is { } group)
        {
            return group.Name;
        }

        var receiver = receiverId is Guid id
            ? receivers.FirstOrDefault(candidate => candidate.Id == id)
            : null;

        if (receiver is null)
        {
            var fallback = string.IsNullOrWhiteSpace(legacyRecipient) ? "unset" : legacyRecipient;
            return $"{legacyKind.ToString().ToLowerInvariant()} · {fallback}";
        }

        return receiver.Kind switch
        {
            NotificationEndpointKind.Webhook => $"{receiver.Name} · webhook value",
            NotificationEndpointKind.Sms => $"{receiver.Name} · SMS via Matmon Cloud",
            NotificationEndpointKind.Voice => $"{receiver.Name} · call via Matmon Cloud",
            _ => $"{receiver.Name} · email"
        };
    }

    private static string FormatEscalationMinutes(int minutes) =>
        minutes % 60 == 0 ? $"{minutes / 60}h" : $"{minutes}m";

    private static string BuildNotificationTriggerSummary(IEnumerable<SensorState> triggerStates)
    {
        var labels = triggerStates
            .Distinct()
            .Select(FormatSensorStateLabel)
            .ToArray();

        return labels.Length == 0 ? "no states" : string.Join(", ", labels);
    }

    private static string BuildNotificationCooldownSummary(int? cooldownMinutes, int? threshold)
    {
        if (cooldownMinutes is not int cooldown || cooldown <= 0)
        {
            return "no cooldown";
        }

        var limit = threshold is int t && t > 0 ? t : 1;
        return limit > 1
            ? $"max {limit}/{cooldown}m"
            : $"cooldown {cooldown}m";
    }

    private (string Summary, string Subject, string Text, string Html, string Short) BuildNotificationRulePreview(
        string ruleName,
        string subjectTemplate,
        string textTemplate,
        string htmlTemplate,
        string shortTemplate,
        Guid? targetElementId,
        bool includeDescendants,
        IEnumerable<SensorState> triggerStates,
        MonitoringWorkspaceSnapshot snapshot,
        IReadOnlyList<WorkspaceNodeRow> nodes,
        IReadOnlyDictionary<Guid, SensorObservation> latestSensorObservations,
        DateTimeOffset now)
    {
        var (summary, context) = BuildNotificationPreviewContext(ruleName, targetElementId, includeDescendants, triggerStates, snapshot, nodes, latestSensorObservations, now);
        var rendered = NotificationRendering.Render(context, subjectTemplate, textTemplate, htmlTemplate, shortTemplate);
        return (summary, rendered.Subject, rendered.Text, rendered.Html, rendered.Summary);
    }

    /// <summary>The data a preview (and the test button) fills the templates with: the latest matching alert, else a
    /// sensor under the target, else any sensor with data - plus a sentence saying which it is.</summary>
    private (string Summary, NotificationTemplateContext Context) BuildNotificationPreviewContext(
        string ruleName,
        Guid? targetElementId,
        bool includeDescendants,
        IEnumerable<SensorState> triggerStates,
        MonitoringWorkspaceSnapshot snapshot,
        IReadOnlyList<WorkspaceNodeRow> nodes,
        IReadOnlyDictionary<Guid, SensorObservation> latestSensorObservations,
        DateTimeOffset now)
    {
        var targetNode = targetElementId is Guid id
            ? nodes.FirstOrDefault(node => node.Id == id)
            : null;

        var alertCandidates = snapshot.Alerts
            .Where(alert => targetNode is null || IsAlertWithinRuleScope(alert.ElementPath, targetNode.Path, includeDescendants))
            .ToArray();

        var selectedAlert = alertCandidates
            .Where(alert => alert.IsActive)
            .OrderByDescending(alert => GetNotificationSeverityRank(alert.State))
            .ThenByDescending(alert => alert.LastSeenUtc)
            .FirstOrDefault()
            ?? alertCandidates.OrderByDescending(alert => alert.LastSeenUtc).FirstOrDefault();

        var selectedSensorNode = selectedAlert is not null
            ? nodes.FirstOrDefault(node => node.Id == selectedAlert.ElementId)
            : targetNode?.Kind == MonitoringElementKind.Sensor
                ? targetNode
                : null;

        if (selectedSensorNode is null && targetNode is not null && targetNode.Kind is MonitoringElementKind.Probe or MonitoringElementKind.Folder or MonitoringElementKind.Host)
        {
            selectedSensorNode = nodes.FirstOrDefault(node =>
                node.Kind == MonitoringElementKind.Sensor &&
                IsAlertWithinRuleScope(node.Path, targetNode.Path, includeDescendants));
        }

        if (selectedSensorNode is null)
        {
            selectedSensorNode = nodes.FirstOrDefault(node =>
                    node.Kind == MonitoringElementKind.Sensor && latestSensorObservations.ContainsKey(node.Id))
                ?? nodes.FirstOrDefault(node => node.Kind == MonitoringElementKind.Sensor);
        }

        latestSensorObservations.TryGetValue(selectedSensorNode?.Id ?? Guid.Empty, out var latestObservation);

        var templateContext = BuildNotificationTemplateContext(
            ruleName,
            targetNode,
            selectedSensorNode,
            selectedAlert,
            latestObservation,
            now);

        var summary = selectedAlert is null
            ? targetNode is null
                ? "Preview uses sample sensor data from the workspace."
                : $"Preview uses data from {targetNode.Path}."
            : $"Preview uses alert data from {selectedAlert.ElementPath}.";

        if (!triggerStates.Any())
        {
            summary += " No trigger states selected.";
        }

        return (summary, templateContext);
    }

    private static NotificationTemplateContext BuildNotificationTemplateContext(
        string ruleName,
        WorkspaceNodeRow? targetNode,
        WorkspaceNodeRow? sensorNode,
        MonitoringAlert? alert,
        SensorObservation? observation,
        DateTimeOffset now)
    {
        var context = new NotificationTemplateContext();
        var elementNode = sensorNode ?? targetNode;
        var defaultChannelKey = observation?.DefaultChannelKey;
        var defaultChannel = observation?.Channels.FirstOrDefault(channel =>
            channel.IsDefault ||
            (!string.IsNullOrWhiteSpace(defaultChannelKey) &&
             string.Equals(channel.Key, defaultChannelKey, StringComparison.OrdinalIgnoreCase)))
            ?? observation?.Channels.FirstOrDefault();
        var sensorMeasurementKind = defaultChannel?.MeasurementKind ?? SensorUnitConverter.GuessMeasurementKind(defaultChannel?.Unit);
        var sensorUnit = defaultChannel?.Unit ?? string.Empty;
        var sensorScale = SensorUnitConverter.CreateScale(GetScaleReferenceValue(observation, defaultChannelKey), sensorUnit, sensorMeasurementKind);
        var sensorValueDisplay = SensorUnitConverter.Format(observation?.DefaultValue, sensorScale, sensorMeasurementKind);
        var state = alert?.State ?? observation?.State ?? (elementNode?.IsPaused == true ? SensorState.Paused : SensorState.Unknown);
        var stateLabel = alert is not null
            ? FormatSensorStateLabel(alert.State)
            : observation is not null
                ? FormatSensorStateLabel(observation.State)
                : elementNode?.StateLabel ?? MonitoringStatePresentation.Label(state);
        var stateColor = alert is not null
            ? MonitoringStatePresentation.Color(alert.State)
            : observation is not null
                ? MonitoringStatePresentation.Color(observation.State)
                : MonitoringStatePresentation.Color(state);
        var stateKey = alert is not null
            ? GetAlertStateKey(alert.State)
            : observation is not null
                ? MonitoringStatePresentation.Key(observation.State)
                : elementNode?.StateKey ?? string.Empty;

        context.SetValue("rule.name", ruleName);
        context.SetValue("state.label", stateLabel);
        context.SetValue("state.key", stateKey);
        context.SetValue("state.color", stateColor);
        NotificationRendering.SetStateValues(context, state, recovered: false);
        context.SetValue("message", alert?.Message ?? observation?.Message ?? elementNode?.StateMessage ?? string.Empty);
        context.SetValue("rendered_at", now);

        context.SetValue("element.name", elementNode?.Name ?? string.Empty);
        context.SetValue("element.path", elementNode?.Path ?? targetNode?.Path ?? string.Empty);
        context.SetValue("element.kind", elementNode?.Kind.ToString() ?? string.Empty);
        context.SetValue("element.details", elementNode?.Details ?? string.Empty);

        context.SetValue("sensor.name", sensorNode?.Name ?? string.Empty);
        context.SetValue("sensor.type", sensorNode?.SensorTypeKey ?? string.Empty);
        context.SetValue("sensor.target", sensorNode?.Target ?? string.Empty);
        context.SetValue("sensor.value", sensorValueDisplay.Text);
        context.SetValue("sensor.unit", sensorValueDisplay.Unit);
        context.SetValue("sensor.value_with_unit", sensorValueDisplay.CombinedText);
        context.SetValue("sensor.last_check", observation?.TimestampUtc);

        context.SetValue("alert.first_seen", alert?.FirstSeenUtc);
        context.SetValue("alert.last_seen", alert?.LastSeenUtc);
        context.SetValue("alert.acknowledged_at", alert?.AcknowledgedUtc);
        context.SetValue("alert.acknowledged_by", alert?.AcknowledgedBy ?? string.Empty);
        context.SetValue("alert.resolved_at", alert?.ResolvedUtc);
        context.SetValue("problem.since", alert?.FirstSeenUtc ?? observation?.TimestampUtc);
        context.SetValue("problem.age", alert is not null ? now - alert.FirstSeenUtc : observation is not null ? now - observation.TimestampUtc : null);

        context.SetValue("probe.name", observation?.ExecutedByProbeName ?? DeriveProbeName(sensorNode, targetNode));
        context.SetValue("probe.id", observation?.ExecutedByProbeId ?? string.Empty);
        context.SetValue("probe.last_seen", observation?.TimestampUtc);

        context.SetValue("channels.summary", BuildChannelsSummary(observation));
        context.SetRawHtml("state.badge_html", BuildStateBadgeHtml(stateLabel, stateColor));
        context.SetRawHtml("channels.table_html", BuildChannelsTableHtml(observation, state));

        return context;
    }

    private static string BuildChannelsSummary(SensorObservation? observation)
    {
        if (observation is null || observation.Channels.Count == 0)
        {
            return string.Empty;
        }

        var channels = observation.Channels.Where(channel => !channel.IsVirtual).ToArray();
        if (channels.Length == 0)
        {
            return string.Empty;
        }

        return string.Join(" · ", channels.Select(channel =>
        {
            var display = SensorUnitConverter.Format(channel.Value, channel.Unit, channel.MeasurementKind);
            return string.IsNullOrWhiteSpace(channel.Label)
                ? $"{channel.Key}: {display.CombinedText}"
                : $"{channel.Label}: {display.CombinedText}";
        }));
    }

    private static string BuildStateBadgeHtml(string stateLabel, string stateColor)
    {
        var background = string.IsNullOrWhiteSpace(stateColor) ? "#4567d2" : stateColor.Trim();
        var foreground = IsLightHexColor(background) ? "#16202c" : "#ffffff";

        return $"<span style=\"display:inline-flex;align-items:center;gap:0.4rem;border-radius:999px;padding:0.35rem 0.75rem;font-size:0.78rem;font-weight:700;color:{foreground};background:{background};\">{WebUtility.HtmlEncode(stateLabel)}</span>";
    }

    private static string BuildChannelsTableHtml(SensorObservation? observation, SensorState state)
    {
        if (observation is null || observation.Channels.Count == 0)
        {
            return string.Empty;
        }

        var channels = observation.Channels.Where(channel => !channel.IsVirtual).ToArray();
        if (channels.Length == 0)
        {
            return string.Empty;
        }

        var rows = channels.Select(channel =>
        {
            var label = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(channel.Label) ? channel.Key : channel.Label);
            var display = SensorUnitConverter.Format(channel.Value, channel.Unit, channel.MeasurementKind);
            var value = WebUtility.HtmlEncode(display.Text);
            var unit = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(display.Unit) ? string.Empty : $" {display.Unit}");
            var channelState = channel.State ?? state;
            var badge = WebUtility.HtmlEncode(FormatSensorStateLabel(channelState));
            var rowStyle = channel.IsDefault ? "font-weight:600;" : string.Empty;
            return $"<tr style=\"{rowStyle}\"><td style=\"padding:0.35rem 0;border-bottom:1px solid rgba(148,163,184,0.18);\">{label}</td><td style=\"padding:0.35rem 0;border-bottom:1px solid rgba(148,163,184,0.18);text-align:right;\">{value}{unit}</td><td style=\"padding:0.35rem 0;border-bottom:1px solid rgba(148,163,184,0.18);text-align:right;\">{badge}</td></tr>";
        });

        return $"""
<table style="width:100%;border-collapse:collapse;font-size:0.9rem;">
  <thead>
    <tr>
      <th style="text-align:left;padding:0 0 0.45rem 0;color:#6b7280;font-weight:600;">Channel</th>
      <th style="text-align:right;padding:0 0 0.45rem 0;color:#6b7280;font-weight:600;">Value</th>
      <th style="text-align:right;padding:0 0 0.45rem 0;color:#6b7280;font-weight:600;">State</th>
    </tr>
  </thead>
  <tbody>
    {string.Join(Environment.NewLine, rows)}
  </tbody>
</table>
""";
    }

    private static double? GetScaleReferenceValue(SensorObservation? observation, string? defaultChannelKey)
    {
        if (observation is null)
        {
            return null;
        }

        var values = new List<double>();

        var defaultValue = SensorHistoryAnalytics.GetDefaultValue(observation, defaultChannelKey);
        if (defaultValue.HasValue)
        {
            values.Add(Math.Abs(defaultValue.Value));
        }

        values.AddRange(observation.Channels
            .Where(channel => !channel.IsVirtual && channel.Value.HasValue)
            .Select(channel => Math.Abs(channel.Value!.Value)));

        return values.Count == 0 ? null : values.Max();
    }

    private static string DeriveProbeName(WorkspaceNodeRow? sensorNode, WorkspaceNodeRow? targetNode)
    {
        var source = sensorNode ?? targetNode;
        if (source is null || string.IsNullOrWhiteSpace(source.Path))
        {
            return string.Empty;
        }

        return source.Path.Split(" / ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
    }

    private static DateTimeOffset? ParseLastCheck(string? lastCheck)
    {
        if (string.IsNullOrWhiteSpace(lastCheck))
        {
            return null;
        }

        return DateTimeOffset.TryParse(lastCheck, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var parsed)
            ? parsed
            : null;
    }

    private static bool IsAlertWithinRuleScope(string alertPath, string targetPath, bool includeDescendants)
    {
        if (string.IsNullOrWhiteSpace(alertPath) || string.IsNullOrWhiteSpace(targetPath))
        {
            return true;
        }

        if (string.Equals(alertPath, targetPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return includeDescendants && alertPath.StartsWith(targetPath + " /", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLightHexColor(string color)
    {
        if (string.IsNullOrWhiteSpace(color) || !color.StartsWith('#'))
        {
            return false;
        }

        var hex = color.Trim().TrimStart('#');
        if (hex.Length != 6 && hex.Length != 8)
        {
            return false;
        }

        try
        {
            var r = Convert.ToInt32(hex[..2], 16);
            var g = Convert.ToInt32(hex.Substring(2, 2), 16);
            var b = Convert.ToInt32(hex.Substring(4, 2), 16);
            var brightness = ((r * 299) + (g * 587) + (b * 114)) / 1000;
            return brightness >= 180;
        }
        catch
        {
            return false;
        }
    }

    private static int GetNotificationSeverityRank(SensorState state)
    {
        return state switch
        {
            SensorState.Critical => 3,
            SensorState.Warning => 2,
            SensorState.Healthy => 1,
            _ => 0
        };
    }
}
