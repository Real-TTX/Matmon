using System.Globalization;

namespace Matmon.Probe.Plugins;

/// <summary>Reads the ports a plugin opens from its settings (for <see cref="PluginDefinition.Listeners"/>).</summary>
public static class PluginListeners
{
    /// <param name="ports">Per listener: the setting that switches it on (null = always on), the setting with its port,
    /// "udp" / "tcp", and the port when the setting is empty or not a number.</param>
    public static IReadOnlyList<PluginListener> Of(
        IReadOnlyDictionary<string, string> settings,
        params (string? EnabledKey, string PortKey, string Protocol, int DefaultPort)[] ports)
    {
        var result = new List<PluginListener>();
        foreach (var (enabledKey, portKey, protocol, defaultPort) in ports)
        {
            if (enabledKey is not null
                && settings.TryGetValue(enabledKey, out var enabled)
                && !string.IsNullOrWhiteSpace(enabled)
                && !string.Equals(enabled.Trim(), "true", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var port = settings.TryGetValue(portKey, out var text)
                && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                && parsed is > 0 and <= 65535
                    ? parsed
                    : defaultPort;
            result.Add(new PluginListener(protocol, port));
        }

        return result;
    }
}
