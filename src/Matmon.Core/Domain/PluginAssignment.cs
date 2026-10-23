namespace Matmon.Core.Domain;

/// <summary>
/// "Run this plugin there, with these settings": one row of System > Plugins. <see cref="ProbeElementId"/> is the probe
/// it runs on - the root probe being the primary itself. Like the syslog and trap receivers' settings it is decided on
/// this instance and never carried in a backup: switching a network listener on somewhere is not something a restore
/// should do.
/// </summary>
public sealed class PluginAssignment
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string PluginId { get; set; } = string.Empty;

    public Guid ProbeElementId { get; set; }

    public bool Enabled { get; set; } = true;

    public Dictionary<string, string> Settings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public PluginAssignment Clone() => new()
    {
        Id = Id,
        PluginId = PluginId,
        ProbeElementId = ProbeElementId,
        Enabled = Enabled,
        Settings = new Dictionary<string, string>(Settings ?? [], StringComparer.OrdinalIgnoreCase)
    };
}
