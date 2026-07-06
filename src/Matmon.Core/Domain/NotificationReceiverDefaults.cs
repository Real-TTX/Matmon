namespace Matmon.Core.Domain;

/// <summary>
/// Built-in, non-persisted notification receivers offered in the receiver pickers (rules + scheduled report).
/// Each expands at send time to the e-mails of all enabled users whose role matches — so membership is always
/// current without maintaining a recipient list.
/// </summary>
public static class NotificationReceiverDefaults
{
    public static readonly Guid AllUsersReceiverId = new("00000000-0000-0000-0000-00000000a11c");
    public static readonly Guid AllAdminsReceiverId = new("00000000-0000-0000-0000-00000000ad11");
    public static readonly Guid AllOperatorsReceiverId = new("00000000-0000-0000-0000-0000000009e7");

    public const string AllUsersName = "All users (built-in)";
    public const string AllAdminsName = "All admins (built-in)";
    public const string AllOperatorsName = "All operators (built-in)";

    /// <summary>A built-in receiver: a stable id, display name and the role predicate it expands to.</summary>
    public sealed record BuiltInReceiver(Guid Id, string Name, Func<MatmonUserRole, bool> RoleMatch);

    public static readonly IReadOnlyList<BuiltInReceiver> All =
    [
        new(AllUsersReceiverId, AllUsersName, _ => true),
        new(AllAdminsReceiverId, AllAdminsName, role => role == MatmonUserRole.Admin),
        // Operators = anyone who can act on alerts (Admin or User), i.e. not read-only Viewers.
        new(AllOperatorsReceiverId, AllOperatorsName, role => role is MatmonUserRole.Admin or MatmonUserRole.User),
    ];

    public static bool IsBuiltIn(Guid? receiverId) => receiverId is { } id && All.Any(b => b.Id == id);

    public static BuiltInReceiver? Find(Guid receiverId) => All.FirstOrDefault(b => b.Id == receiverId);
}
