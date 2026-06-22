namespace Matmon.Core.Domain;

public enum SensorParameterKind
{
    Text = 0,
    Integer = 1,
    Decimal = 2,
    Boolean = 3,
    ValueList = 4,
    Multiline = 5,
    Secret = 6,

    /// <summary>Multiline code editor with syntax highlighting (for script bodies).</summary>
    ScriptEditor = 7
}
