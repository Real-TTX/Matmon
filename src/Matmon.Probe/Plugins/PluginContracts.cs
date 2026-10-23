using Microsoft.Extensions.Logging;

namespace Matmon.Probe.Plugins;

/// <summary>Where a plugin can run: on the primary, on a remote probe or agent, or on both.</summary>
[Flags]
public enum PluginPlacement
{
    Primary = 1,
    Probe = 2,
    Both = Primary | Probe
}

public enum PluginSettingKind
{
    Text,
    Number,
    Boolean,
    Port,
    MultilineText
}

/// <summary>One setting of a plugin, as the instance's Plugins page renders and checks it.</summary>
public sealed record PluginSettingDefinition(
    string Key,
    string Label,
    PluginSettingKind Kind,
    string? DefaultValue = null,
    string? Description = null,
    bool Required = false);

/// <summary>What a plugin is and what it needs - the same on the primary, on a probe and on an agent.</summary>
public sealed record PluginDefinition(
    string Id,
    string DisplayName,
    string Description,
    PluginPlacement Placement,
    IReadOnlyList<PluginSettingDefinition> Settings,
    string Version = "1");

/// <summary>
/// Something a plugin saw and the instance has to know: a log line, a trap, a status change. Created where the plugin
/// runs - on a probe it waits in the probe's on-disk queue while the primary is unreachable and arrives later with its
/// real time (<see cref="OccurredUtc"/>). <see cref="Payload"/> is the plugin's own JSON, read by the matching handler
/// on the primary (by <see cref="Kind"/>).
/// </summary>
public sealed record PluginEvent(
    string PluginId,
    string Kind,
    DateTimeOffset OccurredUtc,
    string? Source,
    string Payload,
    Guid AssignmentId = default);

public interface IPluginEventSink
{
    /// <summary>Hands an event on. Never blocks and never throws at the plugin: a full queue drops (and counts) instead.</summary>
    void Publish(PluginEvent pluginEvent);
}

/// <summary>What a running plugin instance reports: running / error / starting, with a sentence for the Plugins page.</summary>
public sealed record PluginStatus(string State, string? Detail)
{
    public static PluginStatus Running(string? detail = null) => new("running", detail);

    public static PluginStatus Error(string detail) => new("error", detail);
}

/// <summary>Everything a plugin instance gets from its host.</summary>
public sealed class PluginContext
{
    public required Guid AssignmentId { get; init; }

    public required IReadOnlyDictionary<string, string> Settings { get; init; }

    public required IPluginEventSink Events { get; init; }

    public required ILogger Logger { get; init; }

    /// <summary>A directory the plugin may keep files in (its own sub-directory of the host's data/state directory).</summary>
    public required string DataDirectory { get; init; }

    /// <summary>The probe it runs on; null on the primary.</summary>
    public string? ProbeId { get; init; }

    public string Setting(string key, string fallback = "") =>
        Settings.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : fallback;

    public int IntSetting(string key, int fallback) =>
        int.TryParse(Setting(key), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : fallback;

    public bool BoolSetting(string key, bool fallback) =>
        bool.TryParse(Setting(key), out var value) ? value : fallback;
}

/// <summary>
/// A plugin: a service that runs inside Matmon - on the primary, on a probe or on an agent - and reports what it sees as
/// <see cref="PluginEvent"/>s. Built-in plugins are registered in DI; others are loaded at runtime from the host's
/// plugins directory (<see cref="PluginCatalog"/>). The instance decides where each one runs and with which settings.
/// </summary>
public interface IMatmonPlugin
{
    PluginDefinition Definition { get; }

    /// <summary>Starts one instance with these settings. Throw (with a message a user understands) when it cannot run -
    /// the host reports it on the Plugins page and tries again later.</summary>
    Task<IPluginInstance> StartAsync(PluginContext context, CancellationToken cancellationToken);
}

public interface IPluginInstance : IAsyncDisposable
{
    PluginStatus Status { get; }
}
