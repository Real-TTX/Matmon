namespace Matmon.Core.Domain;

public sealed record SensorParameterDefinition
{
    public string Key { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;

    /// <summary>Optional group name. Parameters sharing a group are rendered together under that heading/tab in the editors.</summary>
    public string? Group { get; init; }

    public SensorParameterKind Kind { get; init; } = SensorParameterKind.Text;

    public string? Description { get; init; }

    public bool Required { get; init; }

    public string? DefaultValue { get; init; }

    public string? Placeholder { get; init; }

    public int? Min { get; init; }

    public int? Max { get; init; }

    public string? Step { get; init; }

    public IReadOnlyList<SensorParameterOption> Options { get; init; } = [];

    public MonitoringCredentialKind? CredentialKind { get; init; }

    public string? VisibleWhenParameterKey { get; init; }

    public IReadOnlyList<string> VisibleWhenValues { get; init; } = [];

    public bool IsCredential => CredentialKind is not null;
}
