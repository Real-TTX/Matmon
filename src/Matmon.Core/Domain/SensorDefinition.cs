namespace Matmon.Core.Domain;

public sealed record SensorDefinition
{
    public string Key { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public SensorChannelMode ChannelMode { get; init; } = SensorChannelMode.Dynamic;

    public IReadOnlyList<SensorParameterDefinition> Parameters { get; init; } = [];

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
