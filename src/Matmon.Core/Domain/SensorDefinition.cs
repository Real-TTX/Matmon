namespace Matmon.Core.Domain;

public sealed record SensorDefinition
{
    public string Key { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public SensorUsageLevel? UsageLevel { get; init; }

    public SensorChannelMode ChannelMode { get; init; } = SensorChannelMode.Dynamic;

    public IReadOnlyList<SensorParameterDefinition> Parameters { get; init; } = [];

    // --- Custom (user-authored) script sensor type -------------------------------------------------
    /// <summary>True for an admin-authored custom script sensor type (stored in workspace.json, run via the
    /// Local Script engine). Built-in types leave this false.</summary>
    public bool IsCustomScript { get; init; }

    /// <summary>The script body for a custom type (executed on the host/probe by the Local Script engine).</summary>
    public string? ScriptBody { get; init; }

    /// <summary>pwsh | bash | sh (custom types only).</summary>
    public string? ScriptLanguage { get; init; }

    /// <summary>auto | json | xml | regex | text - how the script output is parsed into channels (custom types).</summary>
    public string? ScriptOutputFormat { get; init; }

    /// <summary>Named-capture regex used when <see cref="ScriptOutputFormat"/> is "regex" (custom types).</summary>
    public string? ScriptRegexPattern { get; init; }

    public IReadOnlyList<MonitoringCredentialKind> CredentialKinds =>
        Parameters
            .Where(parameter => parameter.CredentialKind is not null)
            .Select(parameter => parameter.CredentialKind!.Value)
            .Distinct()
            .ToArray();

    public IReadOnlyList<string> CredentialParameterKeys =>
        Parameters
            .Where(parameter => parameter.IsCredential)
            .Select(parameter => parameter.Key)
            .ToArray();
}

public enum SensorChannelMode
{
    Fixed = 0,
    Dynamic = 1
}
