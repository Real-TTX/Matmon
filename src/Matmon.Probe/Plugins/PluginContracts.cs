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
    MultilineText,

    /// <summary>IP addresses and CIDR ranges (commas or new lines). The Plugins page refuses an entry that is not one
    /// instead of ignoring it - for an allow-list, "ignored" means "wider than the admin meant".</summary>
    AddressList
}

/// <summary>Where a plugin's menu entry goes.</summary>
public enum PluginUiArea
{
    /// <summary>A tab of the System section, linking to <see cref="PluginUiLink.Path"/>.</summary>
    System,

    /// <summary>A tab on the Events page (the host knows what it shows; <see cref="PluginUiLink.Key"/> names it).</summary>
    Events
}

/// <summary>A menu entry a plugin brings along. It is shown only while the plugin runs somewhere (an enabled assignment),
/// so a plugin that is not switched on adds nothing to the menus. <see cref="Path"/> must be a path on the instance.</summary>
public sealed record PluginUiLink(PluginUiArea Area, string Key, string Title, string Path);

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
    string Version = "1")
{
    /// <summary>The menu entries that come with the plugin (shown while it runs somewhere). An init property rather than
    /// a constructor parameter, so a plugin built against the constructor without it still loads.</summary>
    public IReadOnlyList<PluginUiLink> Links { get; init; } = [];

    /// <summary>The network ports the plugin opens for the given settings - what a firewall (or a container's port
    /// mapping) must let in before any device reaches it. Null = it opens none.</summary>
    public Func<IReadOnlyDictionary<string, string>, IReadOnlyList<PluginListener>>? Listeners { get; init; }
}

/// <summary>A port a plugin listens on: "udp" or "tcp" and the number.</summary>
public sealed record PluginListener(string Protocol, int Port);

/// <summary>
/// Something a plugin saw and the instance has to know: a log line, a trap, a status change. Created where the plugin
/// runs - on a probe it waits in the probe's on-disk queue while the primary is unreachable and arrives later with its
/// real time (<see cref="OccurredUtc"/>). <see cref="Payload"/> is the plugin's own JSON, read by the matching handler
/// on the primary (by <see cref="Kind"/>). <see cref="Id"/> is given on a probe (<c>ProbePluginEventSink</c>) and lets the
/// primary recognise an event it already took when the probe sends a batch again; empty from an older probe.
/// </summary>
public sealed record PluginEvent(
    string PluginId,
    string Kind,
    DateTimeOffset OccurredUtc,
    string? Source,
    string Payload,
    Guid AssignmentId = default,
    Guid Id = default);

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
