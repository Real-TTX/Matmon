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

    public string PasswordHash { get; set; } = string.Empty;

    public MatmonUserRole Role { get; set; } = MatmonUserRole.Viewer;

    public bool IsEnabled { get; set; } = true;

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

