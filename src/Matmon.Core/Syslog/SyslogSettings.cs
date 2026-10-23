namespace Matmon.Core.Syslog;

/// <summary>
/// The central log's settings - how long and how much it keeps (System > Syslog), stored in workspace.json. Receiving is
/// the syslog collector plugin's job now (System > Plugins, on the main probe or a probe near the devices); the receiver
/// fields below are what the primary's own receiver used before - read once by migration 9, which turns a receiver that
/// was on into the collector on the main probe, and not used otherwise.
/// </summary>
public sealed class SyslogSettings
{
    public const int DefaultPort = 514;
    public const int MinRetentionDays = 1;
    public const int MaxRetentionDays = 365;
    public const int DefaultRetentionDays = 14;
    public const int MinMessages = 10_000;
    public const int MaxMessagesLimit = 50_000_000;
    public const int DefaultMaxMessages = 2_000_000;

    /// <summary>Before migration 9: the primary's own receiver was on. Cleared by the migration.</summary>
    public bool Enabled { get; set; }

    public bool UdpEnabled { get; set; } = true;

    public int UdpPort { get; set; } = DefaultPort;

    public bool TcpEnabled { get; set; } = true;

    public int TcpPort { get; set; } = DefaultPort;

    /// <summary>Messages older than this are deleted (by RECEIVE time).</summary>
    public int RetentionDays { get; set; } = DefaultRetentionDays;

    /// <summary>A hard cap besides the age: a reboot storm or a chatty device must not fill the disk before the
    /// retention catches up. The oldest messages go first.</summary>
    public int MaxMessages { get; set; } = DefaultMaxMessages;

    /// <summary>IP addresses and CIDR ranges allowed to send, separated by commas, spaces or new lines. Empty =
    /// anyone who can reach the port (syslog has no authentication to fall back on).</summary>
    public string? AllowedSources { get; set; }

    public SyslogSettings Clone() => new()
    {
        Enabled = Enabled,
        UdpEnabled = UdpEnabled,
        UdpPort = UdpPort,
        TcpEnabled = TcpEnabled,
        TcpPort = TcpPort,
        RetentionDays = RetentionDays,
        MaxMessages = MaxMessages,
        AllowedSources = AllowedSources
    };

    /// <summary>A copy with every value pulled into its legal range - what is stored and what the listeners use.</summary>
    public SyslogSettings Normalized()
    {
        var copy = Clone();
        copy.UdpPort = ClampPort(copy.UdpPort);
        copy.TcpPort = ClampPort(copy.TcpPort);
        copy.RetentionDays = Math.Clamp(copy.RetentionDays, MinRetentionDays, MaxRetentionDays);
        copy.MaxMessages = Math.Clamp(copy.MaxMessages, MinMessages, MaxMessagesLimit);
        copy.AllowedSources = string.IsNullOrWhiteSpace(copy.AllowedSources) ? null : copy.AllowedSources.Trim();
        return copy;
    }

    /// <summary>Everything that decides which sockets are open and who may use them - a change to any of it means the
    /// listeners have to be rebuilt; a change to retention does not.</summary>
    public string ListenerSignature() =>
        $"{Enabled}|{UdpEnabled}:{UdpPort}|{TcpEnabled}:{TcpPort}|{AllowedSources?.Trim()}";

    private static int ClampPort(int port) => port is >= 1 and <= 65535 ? port : DefaultPort;
}
