namespace Import.Application.Mapping;

/// <summary>
/// Discord numeric channel type -&gt; Guild.Domain.Enums.ChannelType name (carried as a string
/// across the bus, same convention as other enums mirrored in Guild.Contracts). Inverse of
/// Bots.Application/Gateway/DiscordChannelType.cs's FromEnumName.
/// </summary>
public static class DiscordChannelTypeMapper
{
    public const int GuildText = 0;
    public const int GuildVoice = 2;
    public const int GuildCategory = 4;
    public const int GuildAnnouncement = 5;
    public const int GuildStageVoice = 13;
    public const int GuildPublicThread = 11;
    public const int GuildPrivateThread = 12;
    public const int GuildForum = 15;
    public const int GuildMedia = 16;

    public static bool IsCategory(int discordType) => discordType == GuildCategory;

    /// <summary>Threads are excluded from v1 (no message content to justify them) - callers
    /// should skip channels this returns true for.</summary>
    public static bool IsThread(int discordType) => discordType is GuildPublicThread or GuildPrivateThread;

    public static string ToEchoChannelType(int discordType) => discordType switch
    {
        GuildText => "Text",
        GuildVoice => "Voice",
        GuildAnnouncement => "Announcement",
        GuildStageVoice => "Voice", // lossy - Echo has no stage-channel concept
        GuildForum => "Forum",
        GuildMedia => "Forum", // lossy - closest fit
        _ => "Text",
    };
}
