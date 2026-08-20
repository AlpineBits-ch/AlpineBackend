namespace Messaging.Domain.Enums;

public enum MessageType
{
    Message,
    Invite,
    GuildMemberJoin,
    GuildMemberLeave,

    /// <summary>A call in this conversation finished.</summary>
    CallEnded,

    /// <summary>A call in this conversation ended with nobody but the caller ever connecting.
    /// Content is empty: there is no duration to show, only that it happened and who placed it
    /// (the message's author).</summary>
    CallMissed,

    /// <summary>
    /// Somebody asked the other party into a voice channel - the durable half of a voice ring
    /// (<c>Guild.Application/Services/VoiceRingService</c>), written into the 1:1 conversation
    /// between the two so the invitation survives the ring itself.
    /// </summary>
    VoiceChannelInvite,

    /// <summary>A group conversation was renamed. Content is the new name, empty when it was cleared.</summary>
    GroupNameChanged,

    /// <summary>A group conversation's icon was replaced. Content is empty, or "removed" when it was cleared.</summary>
    GroupIconChanged,

    /// <summary>A server-rolled dice expression and its result.</summary>
    DiceRoll,

    /// <summary>
    /// A character walked into a scene. The display fields carry the character it walked in as,
    /// denormalised so a rename a year later still reads correctly at this point in the log.
    /// </summary>
    SceneCharacterJoined,

    /// <summary>A character left a scene. Content is empty, or "removed" when a GM took it out
    /// rather than the player leaving, the way GroupIconChanged tells its two cases apart.</summary>
    SceneCharacterLeft,
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