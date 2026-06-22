namespace Matmon.Core.Domain;

/// <summary>One environment variable a local script / program sensor can read.</summary>
public sealed record ScriptSensorVariable(string Name, string Description, string Group);

/// <summary>
/// The context environment variables exposed to the locally-executed script/program sensors
/// (<c>local-script</c>, <c>local-program</c>) — see
/// <c>LocalScriptSensorExecutor.ApplyContextEnvironment</c>. Used to render a "variables you
/// can use" reference in the sensor editors. Remote sensors (PowerShell Remote) run their
/// script in the remote session and do <em>not</em> get these, so they return nothing.
/// </summary>
public static class ScriptSensorVariables
{
    public const string ContextGroup = "Context";
    public const string CredentialGroup = "From the selected credential";

    private static readonly IReadOnlyList<ScriptSensorVariable> LocalVariables =
    [
        new("MATMON_HOST", "The sensor's target / host address", ContextGroup),
        new("MATMON_TARGET", "Same as MATMON_HOST", ContextGroup),
        new("MATMON_SENSOR_TYPE", "This sensor's type key", ContextGroup),
        new("MATMON_USERNAME", "Username from the selected credential", CredentialGroup),
        new("MATMON_PASSWORD", "Password / secret from the selected credential", CredentialGroup),
        new("MATMON_TOKEN", "Token / API key from the selected credential", CredentialGroup),
        new("MATMON_CRED_KIND", "Kind of the selected credential (Windows / Linux / …)", CredentialGroup),
        new("MATMON_CRED_NAME", "Name of the selected credential", CredentialGroup),
        new("MATMON_CRED_<FIELD>", "Each raw credential field, e.g. MATMON_CRED_WINRM_USERNAME", CredentialGroup),
    ];

    /// <summary>The variables available to the given sensor type, or empty when not applicable.</summary>
    public static IReadOnlyList<ScriptSensorVariable> For(string? sensorTypeKey)
    {
        return sensorTypeKey?.Trim().ToLowerInvariant() switch
        {
            "local-script" or "local-program" => LocalVariables,
            _ => []
        };
    }
}
