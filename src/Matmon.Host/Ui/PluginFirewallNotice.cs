using Matmon.Probe.Plugins;

namespace Matmon.Host.Ui;

/// <summary>
/// What has to be opened before a listening plugin (the syslog / SNMP trap collector) receives anything: the ports in
/// the firewall of the machine it runs on, and - when that machine runs Matmon in a container - the same ports
/// published by the container. A collector that is switched on but unreachable looks exactly like a quiet network,
/// which is why this is said when it is set up and again after saving.
/// </summary>
public sealed record PluginFirewallNotice(
    string Location,
    IReadOnlyList<PluginListener> Listeners,
    bool InContainer,
    bool ContainerUnknown)
{
    /// <summary>"UDP 514 and TCP 514".</summary>
    public string Ports => string.Join(" and ", Listeners.Select(listener => $"{listener.Protocol.ToUpperInvariant()} {listener.Port}"));

    /// <summary>The <c>docker run</c> flags: "-p 514:514/udp -p 514:514/tcp".</summary>
    public string DockerRun => string.Join(" ", Listeners.Select(listener => $"-p {listener.Port}:{listener.Port}/{listener.Protocol}"));

    /// <summary>The Compose lines under <c>ports:</c>.</summary>
    public string Compose => string.Join("\n", Listeners.Select(listener => $"  - \"{listener.Port}:{listener.Port}/{listener.Protocol}\""));

    /// <summary>One sentence for a status line after saving.</summary>
    public string Sentence => InContainer
        ? $"Let {Ports} in: publish them from the container ({DockerRun}, then recreate it) and allow them in the host's firewall."
        : ContainerUnknown
            ? $"Let {Ports} in on {Location}: allow them in its firewall, and if it runs in a container, publish them there too ({DockerRun})."
            : $"Let {Ports} in: allow them in the firewall of {Location}.";

    /// <summary>The notice for a plugin with these settings on this location, or null when it opens no port.</summary>
    public static PluginFirewallNotice? For(PluginDefinition? definition, IReadOnlyDictionary<string, string> settings, string location, bool isPrimary)
    {
        var listeners = definition?.Listeners?.Invoke(settings) ?? [];
        if (listeners.Count == 0)
        {
            return null;
        }

        // The primary knows whether it is in a container (the .NET images set this); a remote probe or agent is not
        // known from here, so both cases are named for it.
        var inContainer = isPrimary && string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase);
        return new PluginFirewallNotice(isPrimary ? "this machine" : location, listeners, inContainer, ContainerUnknown: !isPrimary);
    }
}
