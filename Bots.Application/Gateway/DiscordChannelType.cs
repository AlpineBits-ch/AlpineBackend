namespace Bots.Application.Gateway;

/// <summary>
/// Maps Guild.Domain.Enums.ChannelType (carried across the bus by name, since Guild.Contracts
/// has no project reference to Guild.Domain) to Discord's numeric channel type constants.
/// </summary>
public static class DiscordChannelType
{
    public const int GuildText = 0;
    public const int GuildVoice = 2;
    public const int GuildCategory = 4;
    public const int GuildAnnouncement = 5;
    public const int PublicThread = 11;
    public const int GuildForum = 15;

    public static int FromEnumName(string channelTypeName) => channelTypeName switch
    {
        "Text" => GuildText,
        "Voice" => GuildVoice,
        "Announcement" => GuildAnnouncement,
        "Thread" => PublicThread,
        "Forum" => GuildForum,
        // Category isn't a Guild.Domain.Enums.ChannelType value (it's a separate Category
        // aggregate) - CategoryEndpoint passes this literal string, matching how the Discord
        // import sync handler already treats categories as type:4 channels.
        "Category" => GuildCategory,
        // Ticket has no Discord equivalent - closest behavior is a private text channel.
        "Ticket" => GuildText,
        _ => GuildText,
    };
}
