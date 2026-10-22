namespace Matmon.Core.Domain;

/// <summary>A notification with every template filled in - what each channel takes its parts from.</summary>
public sealed record RenderedNotification(string Subject, string Text, string Html, string Summary);

/// <summary>
/// The one place a notification's templates are filled in, for the dispatcher, the escalation and the rule editor's
/// test button alike. The subject and the short message are rendered FIRST and put back into the context as
/// <c>{{subject}}</c> / <c>{{summary}}</c>, so a webhook body (or the text template) can use them.
/// </summary>
public static class NotificationRendering
{
    /// <summary>A push or chat line, not a mail: longer than this is cut off with an ellipsis.</summary>
    public const int MaxSummaryLength = 300;

    /// <summary>The values a push service needs besides the state's name: <c>state.priority</c> (1-5) and
    /// <c>state.urgent</c> (1/0).</summary>
    public static void SetStateValues(NotificationTemplateContext context, SensorState state, bool recovered)
    {
        ArgumentNullException.ThrowIfNull(context);
        var priority = recovered ? 3 : state switch
        {
            SensorState.Critical => 5,
            SensorState.Warning => 4,
            _ => 3
        };

        context.SetValue("state.priority", priority.ToString(System.Globalization.CultureInfo.InvariantCulture));
        context.SetValue("state.urgent", !recovered && state == SensorState.Critical ? "1" : "0");
    }

    public static RenderedNotification Render(
        NotificationTemplateContext context,
        string? subjectTemplate,
        string? textTemplate,
        string? htmlTemplate,
        string? shortTemplate)
    {
        ArgumentNullException.ThrowIfNull(context);

        // A template can put the priority where JSON wants a number (ntfy, Gotify) - so it is never left empty.
        if (!context.Values.ContainsKey("state.priority"))
        {
            context.SetValue("state.priority", "3");
        }

        if (!context.Values.ContainsKey("state.urgent"))
        {
            context.SetValue("state.urgent", "0");
        }

        var subject = NotificationTemplateRenderer.RenderText(subjectTemplate, context, NotificationTemplateCatalog.DefaultSubjectTemplate);
        context.SetValue("subject", subject);

        var summary = Shorten(NotificationTemplateRenderer.RenderText(shortTemplate, context, NotificationTemplateCatalog.DefaultShortTemplate));
        context.SetValue("summary", summary);

        var text = NotificationTemplateRenderer.RenderText(textTemplate, context, NotificationTemplateCatalog.DefaultTextTemplate);
        var html = NotificationTemplateRenderer.RenderHtml(htmlTemplate, context, NotificationTemplateCatalog.DefaultHtmlTemplate);
        return new RenderedNotification(subject, text, html, summary);
    }

    private static string Shorten(string text)
    {
        var line = string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return line.Length <= MaxSummaryLength ? line : line[..(MaxSummaryLength - 1)].TrimEnd() + "…";
    }
}
