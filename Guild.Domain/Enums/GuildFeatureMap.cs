namespace Guild.Domain.Enums;

/// <summary>
/// The single source of truth for "which permission bits and channel types belong to which module".
/// </summary>
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

        (GuildFeatures.Lists,
            Permissions.ManageLists | Permissions.AddListItems | Permissions.CheckOffListItems),

        (GuildFeatures.Chores, Permissions.ManageChores | Permissions.CompleteChores),

        (GuildFeatures.Ledger, Permissions.ManageLedger | Permissions.AddExpenses),

        (GuildFeatures.Pantry, Permissions.ManagePantry),

        (GuildFeatures.Decisions, Permissions.CreateDecisions | Permissions.VoteDecisions),

        (GuildFeatures.GuestAccess, Permissions.ManageGuests),

        (GuildFeatures.Meals, Permissions.PlanMeals | Permissions.ManageMeals),

        (GuildFeatures.Maintenance,
            Permissions.LogMaintenance | Permissions.ManageMaintenance),
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
        ChannelType.List => GuildFeatures.Lists,
        ChannelType.Chores => GuildFeatures.Chores,
        ChannelType.Ledger => GuildFeatures.Ledger,
        ChannelType.Pantry => GuildFeatures.Pantry,
        ChannelType.Decisions => GuildFeatures.Decisions,
        ChannelType.Meals => GuildFeatures.Meals,
        ChannelType.Maintenance => GuildFeatures.Maintenance,
        _ => null,
    };
}
