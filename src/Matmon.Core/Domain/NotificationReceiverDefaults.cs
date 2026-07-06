namespace Matmon.Core.Domain;

/// <summary>
/// Built-in, non-persisted notification receivers offered in the receiver pickers (rules + scheduled report).
/// </summary>
public static class NotificationReceiverDefaults
{
    /// <summary>Virtual receiver that expands to every enabled user's e-mail address at send time — so new
    /// users are covered automatically without maintaining a recipient list.</summary>
    public static readonly Guid AllUsersReceiverId = new("00000000-0000-0000-0000-00000000a11c");

    public const string AllUsersName = "All users (built-in)";

    public static bool IsBuiltIn(Guid? receiverId) => receiverId == AllUsersReceiverId;
}
