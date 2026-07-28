namespace Guild.Domain.Enums;

public enum AuditActionType
{
    MemberBanned,
    MemberUnbanned,
    MemberKicked,
    MemberMuted,
    MemberUnmuted,
    MemberLeft,
    RoleCreated,
    RoleUpdated,
    RoleDeleted,
    RolePositionsChanged,
    ChannelCreated,
    ChannelDeleted,
    ChannelUpdated,
    ChannelPermissionChanged,
    CategoryCreated,
    CategoryDeleted,
    GuildUpdated,
    GuildDeleted,
    InviteCreated,
    InviteDeleted,
    BotInstalled,
    BotUninstalled,
    GuildImportedFromDiscord,
    GuildSyncedFromDiscord,
}
