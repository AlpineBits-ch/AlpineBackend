namespace Messaging.Domain.Enums;

public enum MessageType
{
    Message,
    Invite,
    GuildMemberJoin,
    GuildMemberLeave,
}

/// <summary>
/// System message types carry no real Content - clients render one of a fixed set of
/// localized copy variants (own i18n strings) picked at random server-side via
/// Message.SystemMessageVariant, the same way Discord's system messages work.
/// </summary>
public static class SystemMessageVariants
{
    public const int GuildMemberJoinCount = 10;
    public const int GuildMemberLeaveCount = 10;

    public static int? PickFor(MessageType type)
    {
        return type switch
        {
            MessageType.GuildMemberJoin => Random.Shared.Next(GuildMemberJoinCount),
            MessageType.GuildMemberLeave => Random.Shared.Next(GuildMemberLeaveCount),
            _ => null,
        };
    }
}