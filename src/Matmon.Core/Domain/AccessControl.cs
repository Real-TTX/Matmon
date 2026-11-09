namespace Matmon.Core.Domain;

public enum MatmonUserRole
{
    Viewer = 0,
    User = 1,
    Admin = 2
}

public sealed class MatmonUser
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Optional e-mail address. The first-run account (and future accounts) sign in with their
    /// e-mail; <see cref="Username"/> is kept as the display/login fallback (older installs only
    /// have a username). Login matches either field.
    /// </summary>
    public string Email { get; set; } = string.Empty;

    public string PasswordHash { get; set; } = string.Empty;

    public MatmonUserRole Role { get; set; } = MatmonUserRole.Viewer;

    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// True when this account was provisioned/linked via "Sign in with Matmon Cloud" (SSO). Such accounts
    /// normally have no local password (SSO-only); an admin can still set one for offline login.
    /// </summary>
    public bool CloudLinked { get; set; }

    /// <summary>
    /// Set on the ONE shared user that stands for everybody who comes in through Matmon Cloud remote access as the managing
    /// partner (<c>partner:&lt;id&gt;</c>, named after the partner) or as the platform operator (<c>support</c>, "Matmon Support")
    /// - the user list shows who has access, not which of their staff opened it. Such a user has no e-mail (so no "All users"
    /// notifications) and never gets a local password. Null for every ordinary account.
    /// </summary>
    public string? RemoteAccessKey { get; set; }

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When the account last signed in (local password or cloud SSO). Null = never since tracking.</summary>
    public DateTimeOffset? LastLoginUtc { get; set; }

    /// <summary>Per-user display-timezone override (IANA id, e.g. "Europe/Berlin"). Null = use the system default.</summary>
    public string? TimeZoneId { get; set; }

    // --- Two-factor auth (TOTP). Optional, self-enrolled. An e-mailed code is the login/disable fallback. ---

    /// <summary>Whether TOTP two-factor is active for this user (requires a confirmed enrollment).</summary>
    public bool TwoFactorEnabled { get; set; }

    /// <summary>The TOTP shared secret (Base32), DataProtection-encrypted at rest in workspace.json. Null = not enrolled.</summary>
    public string? TotpSecretProtected { get; set; }

    /// <summary>When TOTP was confirmed/enabled (audit).</summary>
    public DateTimeOffset? TotpEnrolledUtc { get; set; }
}


/// <summary>
/// Remote access through Matmon Cloud as the managing partner or as Matmon Support. The cloud says which with the header
/// <c>X-Matmon-Cloud-Access</c> (<c>partner|&lt;id&gt;|&lt;name&gt;</c> or <c>support|&lt;name&gt;</c>, names URL-encoded); a member of the
/// instance comes without it and keeps a user of their own. Both kinds sign in as one shared user (<see cref="MatmonUser.RemoteAccessKey"/>).
/// </summary>
public static class RemoteAccess
{
    public const string SupportKey = "support";
    public const string SupportName = "Matmon Support";
    public const string PartnerPrefix = "partner:";

    public static bool IsPartnerKey(string? key) => key is not null && key.StartsWith(PartnerPrefix, StringComparison.Ordinal);

    /// <summary>Reads the header into the shared user's key and name; false for an empty or unknown value.</summary>
    public static bool TryParse(string? header, out string key, out string name)
    {
        key = string.Empty;
        name = string.Empty;
        var parts = (header ?? string.Empty).Split('|');
        if (parts.Length == 3 && parts[0] == "partner" && Guid.TryParse(parts[1], out var partnerId) && partnerId != Guid.Empty)
        {
            key = PartnerPrefix + partnerId.ToString("D");
            name = Decode(parts[2], "Partner");
            return true;
        }

        if (parts.Length >= 1 && parts[0] == "support")
        {
            key = SupportKey;
            name = parts.Length >= 2 ? Decode(parts[1], SupportName) : SupportName;
            return true;
        }

        return false;
    }

    private static string Decode(string value, string fallback)
    {
        string text;
        try
        {
            text = Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return fallback;
        }

        // A name on a page and in a claim - no control characters, never empty.
        text = new string(text.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return text.Length == 0 ? fallback : text;
    }
}
