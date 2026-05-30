using Matmon.Core.Domain;

namespace Matmon.Host.Services;

public sealed record DashboardNodeViewModel(
    Guid Id,
    string Name,
    MonitoringElementKind Kind,
    int Depth,
    string Details,
    string SettingsSummary,
    string TemplateSummary,
    bool IsHighlighted,
    string StateKey,
    string StateLabel,
    string StateColor,
    string? StateMessage);
