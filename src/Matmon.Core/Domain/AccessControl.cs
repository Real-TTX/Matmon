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

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When the account last signed in (local password or cloud SSO). Null = never since tracking.</summary>
    public DateTimeOffset? LastLoginUtc { get; set; }
}

