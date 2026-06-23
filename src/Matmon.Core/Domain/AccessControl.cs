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

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

