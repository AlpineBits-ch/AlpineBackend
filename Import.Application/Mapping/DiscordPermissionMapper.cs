namespace Import.Application.Mapping;

/// <summary>
/// Discord's real permission bitmask -&gt; Guild.Domain.Enums.Permissions bitmask.
/// </summary>
[Flags]
public enum EchoPermissions : ulong
{
    None = 0,
    ViewChannel = 1ul << 0,
    SendMessages = 1ul << 1,
    EditOwnMessages = 1ul << 2,
    EditAnyMessage = 1ul << 3,
    DeleteOwnMessages = 1ul << 4,
    DeleteAnyMessage = 1ul << 5,
    PinMessages = 1ul << 6,
    AttachFiles = 1ul << 7,
    EmbedLinks = 1ul << 8,
    AddReactions = 1ul << 9,
    Connect = 1ul << 10,
    Speak = 1ul << 11,
    Stream = 1ul << 12,
    MuteMembers = 1ul << 13,
    DeafenMembers = 1ul << 14,
    MoveMembers = 1ul << 15,
    CreateThreads = 1ul << 16,
    SendMessagesInThreads = 1ul << 17,
    ManageOwnThreads = 1ul << 18,
    ManageAnyThread = 1ul << 19,
    ManageChannel = 1ul << 20,
    ManagePermissions = 1ul << 21,
    CreateInvite = 1ul << 22,
    ReadMessageHistory = 1ul << 23,
    SendVoiceMessages = 1ul << 24,
    SendPolls = 1ul << 25,
    UseExternalEmojis = 1ul << 26,
    UseExternalStickers = 1ul << 27,
    CreatePrivateThreads = 1ul << 28,
    UseApplicationCommands = 1ul << 29,
    CreateExpressions = 1ul << 30,
    ManageExpressions = 1ul << 31,
    KickMembers = 1ul << 32,
    BanMembers = 1ul << 33,
    ModerateMembers = 1ul << 34,
    ManageGuild = 1ul << 35,
    ViewAuditLog = 1ul << 36,
    ManageEmojis = 1ul << 37,
    ManageEvents = 1ul << 38,
    PrioritySpeaker = 1ul << 39,
    RequestToSpeak = 1ul << 40,
    UseVoiceActivity = 1ul << 41,
    MentionEveryone = 1ul << 50,
    ManageRoles = 1ul << 51,
    ManageWebhooks = 1ul << 52,
    ChangeNickname = 1ul << 53,
    ManageNicknames = 1ul << 54,
    Superadmin = 1ul << 63,
}

public static class DiscordPermissionMapper
{
    // Real Discord API v10 permission bit constants (the subset with an Echo equivalent).
    private const ulong CreateInstantInvite = 1ul << 0;
    private const ulong KickMembers = 1ul << 1;
    private const ulong BanMembers = 1ul << 2;
    private const ulong Administrator = 1ul << 3;
    private const ulong ManageChannels = 1ul << 4;
    private const ulong ManageGuild = 1ul << 5;
    private const ulong AddReactions = 1ul << 6;
    private const ulong ViewAuditLog = 1ul << 7;
    private const ulong Stream = 1ul << 9;
    private const ulong ViewChannel = 1ul << 10;
    private const ulong SendMessages = 1ul << 11;
    private const ulong ManageMessages = 1ul << 13;
    private const ulong EmbedLinks = 1ul << 14;
    private const ulong AttachFiles = 1ul << 15;
    private const ulong MentionEveryone = 1ul << 17;
    private const ulong Connect = 1ul << 20;
    private const ulong Speak = 1ul << 21;
    private const ulong MuteMembers = 1ul << 22;
    private const ulong DeafenMembers = 1ul << 23;
    private const ulong MoveMembers = 1ul << 24;
    private const ulong ChangeNickname = 1ul << 26;
    private const ulong ManageNicknames = 1ul << 27;
    private const ulong ManageRoles = 1ul << 28;
    private const ulong ManageWebhooks = 1ul << 29;
    private const ulong ManageThreads = 1ul << 34;
    private const ulong CreatePublicThreads = 1ul << 35;
    private const ulong CreatePrivateThreads = 1ul << 36;
    private const ulong SendMessagesInThreads = 1ul << 38;
    private const ulong ModerateMembers = 1ul << 40;
    private const ulong PrioritySpeaker = 1ul << 8;
    private const ulong ReadMessageHistory = 1ul << 16;
    private const ulong UseExternalEmojis = 1ul << 18;
    private const ulong UseVoiceActivity = 1ul << 25;
    private const ulong UseApplicationCommands = 1ul << 31;
    private const ulong RequestToSpeak = 1ul << 32;
    private const ulong ManageEvents = 1ul << 33;
    private const ulong UseExternalStickers = 1ul << 37;
    private const ulong ManageGuildExpressions = 1ul << 30;
    private const ulong CreateExpressions = 1ul << 43;
    private const ulong SendVoiceMessages = 1ul << 46;
    private const ulong SendPolls = 1ul << 49;

    /// <summary>
    /// Parses Discord's decimal-string permission bitmask (kept as a string in JSON to avoid JS
    /// Number precision loss) and remaps it bit-by-bit to <see cref="EchoPermissions"/>.
    /// </summary>
    public static ulong ToEchoPermissions(string discordPermissionsDecimalString)
    {
        if (!ulong.TryParse(discordPermissionsDecimalString, out var discord))
        {
            return 0;
        }

        var echo = EchoPermissions.None;

        if ((discord & Administrator) != 0) echo |= EchoPermissions.Superadmin;
        if ((discord & ViewChannel) != 0) echo |= EchoPermissions.ViewChannel;
        if ((discord & SendMessages) != 0) echo |= EchoPermissions.SendMessages;
        if ((discord & ManageMessages) != 0) echo |= EchoPermissions.DeleteAnyMessage | EchoPermissions.PinMessages;
        if ((discord & AttachFiles) != 0) echo |= EchoPermissions.AttachFiles;
        if ((discord & EmbedLinks) != 0) echo |= EchoPermissions.EmbedLinks;
        if ((discord & AddReactions) != 0) echo |= EchoPermissions.AddReactions;
        if ((discord & Connect) != 0) echo |= EchoPermissions.Connect;
        if ((discord & Speak) != 0) echo |= EchoPermissions.Speak;
        if ((discord & Stream) != 0) echo |= EchoPermissions.Stream;
        if ((discord & MuteMembers) != 0) echo |= EchoPermissions.MuteMembers;
        if ((discord & DeafenMembers) != 0) echo |= EchoPermissions.DeafenMembers;
        if ((discord & MoveMembers) != 0) echo |= EchoPermissions.MoveMembers;
        // Mapped separately now that Echo has a bit for each.
        if ((discord & CreatePublicThreads) != 0) echo |= EchoPermissions.CreateThreads;
        if ((discord & CreatePrivateThreads) != 0) echo |= EchoPermissions.CreatePrivateThreads;
        if ((discord & SendMessagesInThreads) != 0) echo |= EchoPermissions.SendMessagesInThreads;
        if ((discord & ManageThreads) != 0) echo |= EchoPermissions.ManageAnyThread;
        if ((discord & ManageChannels) != 0) echo |= EchoPermissions.ManageChannel;
        // Discord's single MANAGE_ROLES covers both editing roles and setting per-channel
        // overwrites; Echo splits those into ManageRoles and ManagePermissions, so it maps to both.
        if ((discord & ManageRoles) != 0) echo |= EchoPermissions.ManageRoles | EchoPermissions.ManagePermissions;
        if ((discord & ManageWebhooks) != 0) echo |= EchoPermissions.ManageWebhooks;
        if ((discord & MentionEveryone) != 0) echo |= EchoPermissions.MentionEveryone;
        if ((discord & ChangeNickname) != 0) echo |= EchoPermissions.ChangeNickname;
        if ((discord & ManageNicknames) != 0) echo |= EchoPermissions.ManageNicknames;
        if ((discord & CreateInstantInvite) != 0) echo |= EchoPermissions.CreateInvite;
        if ((discord & KickMembers) != 0) echo |= EchoPermissions.KickMembers;
        if ((discord & BanMembers) != 0) echo |= EchoPermissions.BanMembers;
        if ((discord & ModerateMembers) != 0) echo |= EchoPermissions.ModerateMembers;
        if ((discord & ManageGuild) != 0) echo |= EchoPermissions.ManageGuild;
        if ((discord & ViewAuditLog) != 0) echo |= EchoPermissions.ViewAuditLog;
        if ((discord & ReadMessageHistory) != 0) echo |= EchoPermissions.ReadMessageHistory;
        if ((discord & UseApplicationCommands) != 0) echo |= EchoPermissions.UseApplicationCommands;
        if ((discord & UseExternalEmojis) != 0) echo |= EchoPermissions.UseExternalEmojis;
        if ((discord & UseExternalStickers) != 0) echo |= EchoPermissions.UseExternalStickers;
        if ((discord & PrioritySpeaker) != 0) echo |= EchoPermissions.PrioritySpeaker;
        if ((discord & RequestToSpeak) != 0) echo |= EchoPermissions.RequestToSpeak;
        if ((discord & UseVoiceActivity) != 0) echo |= EchoPermissions.UseVoiceActivity;
        if ((discord & SendVoiceMessages) != 0) echo |= EchoPermissions.SendVoiceMessages;
        if ((discord & SendPolls) != 0) echo |= EchoPermissions.SendPolls;
        if ((discord & ManageEvents) != 0) echo |= EchoPermissions.ManageEvents;
        if ((discord & CreateExpressions) != 0) echo |= EchoPermissions.CreateExpressions;
        // Discord retired MANAGE_EMOJIS_AND_STICKERS in favour of MANAGE_GUILD_EXPRESSIONS on the
        // same bit.
        if ((discord & ManageGuildExpressions) != 0)
            echo |= EchoPermissions.ManageExpressions | EchoPermissions.ManageEmojis;

        return (ulong)echo;
    }
}
