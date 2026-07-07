namespace Matmon.Core.Domain;

public sealed record NotificationTemplatePlaceholderGroup(
    string Title,
    IReadOnlyList<NotificationTemplatePlaceholder> Placeholders);

public sealed record NotificationTemplatePlaceholder(
    string Key,
    string Description,
    string Example,
    bool IsHtmlSnippet = false);

public static class NotificationTemplateCatalog
{
    public const string DefaultSubjectTemplate = "{{state.label}}: {{element.path}}";

    public const string DefaultTextTemplate = """
{{state.label}} on {{element.path}}

Since: {{problem.since}}
Value: {{sensor.value_with_unit}}
Message: {{message}}
Probe: {{probe.name}}
Channels: {{channels.summary}}
""";

    public const string DefaultHtmlTemplate = """
<div style="font-family:Segoe UI,Arial,sans-serif;border:1px solid #d0d7de;border-radius:14px;padding:18px;background:#fff;color:#111827;">
  <div style="margin-bottom:14px;">{{{state.badge_html}}}</div>
  <h2 style="margin:0 0 6px 0;font-size:20px;">{{element.path}}</h2>
  <div style="color:#6b7280;margin-bottom:16px;">Problem since {{problem.since}} · {{message}}</div>
  <table style="width:100%;border-collapse:collapse;">
    <tr><th style="text-align:left;padding:6px 0;">Value</th><td style="padding:6px 0;">{{sensor.value_with_unit}}</td></tr>
    <tr><th style="text-align:left;padding:6px 0;">Last check</th><td style="padding:6px 0;">{{sensor.last_check}}</td></tr>
    <tr><th style="text-align:left;padding:6px 0;">Probe</th><td style="padding:6px 0;">{{probe.name}}</td></tr>
  </table>
  <div style="margin-top:16px;">{{{channels.table_html}}}</div>
  <div style="margin-top:16px;padding-top:12px;border-top:1px solid #e5e7eb;color:#9ca3af;font-size:11px;">Sent by Matmon · automated monitoring alert.</div>
</div>
""";

    private static readonly IReadOnlyList<NotificationTemplatePlaceholderGroup> Groups =
    [
        new NotificationTemplatePlaceholderGroup("General", [
            new NotificationTemplatePlaceholder("rule.name", "Rule name", "Disk full alert"),
            new NotificationTemplatePlaceholder("state.label", "State label", "Warning"),
            new NotificationTemplatePlaceholder("state.key", "State key", "warning"),
            new NotificationTemplatePlaceholder("message", "Main message", "disk usage above threshold"),
            new NotificationTemplatePlaceholder("rendered_at", "Render time", "31.05.2026 12:45")
        ]),
        new NotificationTemplatePlaceholderGroup("Element", [
            new NotificationTemplatePlaceholder("element.name", "Element name", "NAS-01"),
            new NotificationTemplatePlaceholder("element.path", "Element path", "Main / Office / NAS-01"),
            new NotificationTemplatePlaceholder("element.kind", "Element kind", "Sensor"),
            new NotificationTemplatePlaceholder("element.details", "Element details", "SNMP target / target info")
        ]),
        new NotificationTemplatePlaceholderGroup("Sensor", [
            new NotificationTemplatePlaceholder("sensor.name", "Sensor name", "Windows Health"),
            new NotificationTemplatePlaceholder("sensor.type", "Sensor type", "windows-health"),
            new NotificationTemplatePlaceholder("sensor.target", "Sensor target", "pc-terminal"),
            new NotificationTemplatePlaceholder("sensor.value", "Default value", "87.4"),
            new NotificationTemplatePlaceholder("sensor.unit", "Default unit", "%"),
            new NotificationTemplatePlaceholder("sensor.value_with_unit", "Value with unit", "87.4 %"),
            new NotificationTemplatePlaceholder("sensor.last_check", "Last check", "31.05.2026 12:44"),
            new NotificationTemplatePlaceholder("channels.summary", "Channel summary", "CPU: 87.4 % · RAM: 74.2 %")
        ]),
        new NotificationTemplatePlaceholderGroup("Problem", [
            new NotificationTemplatePlaceholder("problem.since", "Since when the problem exists", "31.05.2026 12:41"),
            new NotificationTemplatePlaceholder("problem.age", "Problem age", "4m"),
            new NotificationTemplatePlaceholder("alert.first_seen", "Alert first seen", "31.05.2026 12:41"),
            new NotificationTemplatePlaceholder("alert.last_seen", "Alert last seen", "31.05.2026 12:44"),
            new NotificationTemplatePlaceholder("alert.acknowledged_at", "Acknowledged at", "31.05.2026 12:43"),
            new NotificationTemplatePlaceholder("alert.acknowledged_by", "Acknowledged by", "Matthias"),
            new NotificationTemplatePlaceholder("alert.resolved_at", "Resolved at", "31.05.2026 12:58")
        ]),
        new NotificationTemplatePlaceholderGroup("Probe", [
            new NotificationTemplatePlaceholder("probe.name", "Probe name", "remote-probe-01"),
            new NotificationTemplatePlaceholder("probe.id", "Probe id", "probe-01"),
            new NotificationTemplatePlaceholder("probe.last_seen", "Probe last seen", "31.05.2026 12:44")
        ]),
        new NotificationTemplatePlaceholderGroup("HTML helpers", [
            new NotificationTemplatePlaceholder("state.badge_html", "State badge snippet", "<span class=\"...\">Warning</span>", true),
            new NotificationTemplatePlaceholder("channels.table_html", "Channel table snippet", "<table>...</table>", true)
        ])
    ];

    public static IReadOnlyList<NotificationTemplatePlaceholderGroup> GetGroups()
    {
        return Groups;
    }
}
