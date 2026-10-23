namespace Matmon.Core.Domain;

/// <summary>
/// A browser (or an installed Matmon app) that a user switched notifications on in - a Web Push subscription. It belongs to
/// the user who registered it and to THIS instance (its VAPID key pair): it is never part of a backup, and a user who is
/// deleted takes their devices along. A notification rule with the built-in "Browser notifications" sender sends to the
/// devices of the users its receiver names (a built-in user group, or a receiver with a user's e-mail address).
/// </summary>
public sealed class PushDevice
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }

    /// <summary>The push service's address for this browser (https; Google, Mozilla, Apple, Microsoft).</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>The browser's public key (base64url, an uncompressed P-256 point) - what the message is encrypted for.</summary>
    public string P256dh { get; set; } = string.Empty;

    /// <summary>The subscription's auth secret (base64url, 16 bytes).</summary>
    public string Auth { get; set; } = string.Empty;

    /// <summary>What the user sees in their list: "Chrome on Android", "Safari on iPhone" - from the user agent, or their own.</summary>
    public string Name { get; set; } = string.Empty;

    public DateTimeOffset CreatedUtc { get; set; }

    /// <summary>When a notification last reached the push service for it.</summary>
    public DateTimeOffset? LastUsedUtc { get; set; }

    public PushDevice Clone() => (PushDevice)MemberwiseClone();
}

/// <summary>A readable name for a device from its user agent - good enough to tell "my phone" from "my laptop".</summary>
public static class PushDeviceNames
{
    public static string FromUserAgent(string? userAgent)
    {
        var ua = userAgent ?? string.Empty;
        var browser = ua.Contains("Edg/", StringComparison.Ordinal) ? "Edge"
            : ua.Contains("OPR/", StringComparison.Ordinal) ? "Opera"
            : ua.Contains("Firefox/", StringComparison.Ordinal) ? "Firefox"
            : ua.Contains("SamsungBrowser/", StringComparison.Ordinal) ? "Samsung Internet"
            : ua.Contains("Chrome/", StringComparison.Ordinal) || ua.Contains("CriOS/", StringComparison.Ordinal) ? "Chrome"
            : ua.Contains("Safari/", StringComparison.Ordinal) ? "Safari"
            : "Browser";
        var system = ua.Contains("iPhone", StringComparison.Ordinal) ? "iPhone"
            : ua.Contains("iPad", StringComparison.Ordinal) ? "iPad"
            : ua.Contains("Android", StringComparison.Ordinal) ? "Android"
            : ua.Contains("Windows", StringComparison.Ordinal) ? "Windows"
            : ua.Contains("Mac OS X", StringComparison.Ordinal) || ua.Contains("Macintosh", StringComparison.Ordinal) ? "Mac"
            : ua.Contains("Linux", StringComparison.Ordinal) ? "Linux"
            : null;
        return system is null ? browser : $"{browser} on {system}";
    }
}
