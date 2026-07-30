namespace Guild.Domain.Enums;

/// <summary>The single source of truth for "which permission bits and channel types belong to
/// which module". GuildPermissionService consults this so that a disabled feature strips its
/// permissions at one choke point instead of every endpoint remembering to check.
///
/// Note what is deliberately NOT gated: ViewChannel, the message/attachment/reaction permissions,
/// CreateInvite, ManageChannel, ManagePermissions and above all ManageGuild - that last one is
/// how an owner turns a feature back on, so gating it would be a one-way door. Features that own
/// no permission bits today (Forums, Announcements, Tickets, AutoMod, Onboarding, Bots, and the
/// household modules) are gated by channel type and by their own endpoints instead.</summary>
public static class GuildFeatureMap
{
    private static readonly (GuildFeatures Feature, Permissions Permissions)[] PermissionOwners =
    [
        (GuildFeatures.VoiceChannels,
            Permissions.Connect | Permissions.Speak | Permissions.Stream |
            Permissions.MuteMembers | Permissions.DeafenMembers | Permissions.MoveMembers),

        (GuildFeatures.Threads,
            Permissions.CreateThreads | Permissions.SendMessagesInThreads |
            Permissions.ManageOwnThreads | Permissions.ManageAnyThread),

        (GuildFeatures.Moderation,
            Permissions.KickMembers | Permissions.BanMembers |
            Permissions.ModerateMembers | Permissions.ViewAuditLog),

        (GuildFeatures.Emojis, Permissions.ManageEmojis),

        (GuildFeatures.Events, Permissions.ManageEvents),

        (GuildFeatures.Wiki,
            Permissions.ViewWiki | Permissions.CreateWikiPages | Permissions.EditOwnWikiPages |
            Permissions.EditAnyWikiPage | Permissions.DeleteWikiPages |
            Permissions.ManageWikiRevisions | Permissions.ManageWikiStructure |
            Permissions.ModerateWikiComments | Permissions.PublishWikiPublicly),
    ];

    /// <summary>Every permission bit owned by a module that <paramref name="enabled"/> does not
    /// include. A required permission intersecting this mask is unavailable in the guild no
    /// matter who is asking - owner and Superadmin included.</summary>
    public static Permissions DisabledPermissions(GuildFeatures enabled)
    {
        var disabled = Permissions.None;
        foreach (var (feature, permissions) in PermissionOwners)
        {
            if (!enabled.HasFlag(feature)) disabled |= permissions;
        }

        return disabled;
    }

    /// <summary>False when <paramref name="required"/> touches any permission belonging to a
    /// disabled module. Intentionally does not consult Superadmin: a feature being off is a
    /// product state, not an authorization level, so no role can escalate past it.</summary>
    public static bool IsPermissionAvailable(GuildFeatures enabled, Permissions required) =>
        (required & DisabledPermissions(enabled)) == Permissions.None;

    /// <summary>Clamps a requested bitmask down to what the guild's modules actually allow - used
    /// where an over-broad request should be silently downgraded rather than rejected (bot
    /// installs), mirroring GuildPermissionService.ClampToGrantableAsync.</summary>
    public static Permissions ClampToEnabled(GuildFeatures enabled, Permissions requested) =>
        requested & ~DisabledPermissions(enabled);

    /// <summary>The module a channel of this type belongs to, or null when the type is part of
    /// the always-on core. Text has no feature flag on purpose - a guild with no text channels
    /// is not a guild.</summary>
    public static GuildFeatures? RequiredFeatureFor(ChannelType type) => type switch
    {
        ChannelType.Voice => GuildFeatures.VoiceChannels,
        ChannelType.Forum or ChannelType.Media => GuildFeatures.Forums,
        ChannelType.Announcement => GuildFeatures.Announcements,
        ChannelType.Ticket => GuildFeatures.Tickets,
        ChannelType.Thread => GuildFeatures.Threads,
        _ => null,
    };
}
