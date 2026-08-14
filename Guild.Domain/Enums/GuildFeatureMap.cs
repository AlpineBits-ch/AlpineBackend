namespace Guild.Domain.Enums;

/// <summary>
/// The single source of truth for "which permission bits and channel types belong to which module".
/// </summary>
public static class GuildFeatureMap
{
    private static readonly (GuildFeatures Feature, Permissions Permissions)[] CorePermissionOwners =
    [
        (GuildFeatures.VoiceChannels,
            Permissions.Connect | Permissions.Speak | Permissions.Stream |
            Permissions.MuteMembers | Permissions.DeafenMembers | Permissions.MoveMembers |
            Permissions.PrioritySpeaker | Permissions.RequestToSpeak | Permissions.UseVoiceActivity),

        (GuildFeatures.Threads,
            Permissions.CreateThreads | Permissions.CreatePrivateThreads |
            Permissions.SendMessagesInThreads |
            Permissions.ManageOwnThreads | Permissions.ManageAnyThread),

        (GuildFeatures.Moderation,
            Permissions.KickMembers | Permissions.BanMembers |
            Permissions.ModerateMembers | Permissions.ViewAuditLog),

        (GuildFeatures.Emojis,
            Permissions.ManageEmojis | Permissions.CreateExpressions | Permissions.ManageExpressions),

        (GuildFeatures.Events, Permissions.ManageEvents),
    ];

    private static readonly (GuildFeatures Feature, ModulePermissions Permissions)[] ModulePermissionOwners =
    [
        (GuildFeatures.Wiki,
            ModulePermissions.ViewWiki | ModulePermissions.CreateWikiPages |
            ModulePermissions.EditOwnWikiPages | ModulePermissions.EditAnyWikiPage |
            ModulePermissions.DeleteWikiPages | ModulePermissions.ManageWikiRevisions |
            ModulePermissions.ManageWikiStructure | ModulePermissions.ModerateWikiComments |
            ModulePermissions.PublishWikiPublicly),

        (GuildFeatures.Lists,
            ModulePermissions.ManageLists | ModulePermissions.AddListItems |
            ModulePermissions.CheckOffListItems),

        (GuildFeatures.Chores, ModulePermissions.ManageChores | ModulePermissions.CompleteChores),

        (GuildFeatures.Ledger, ModulePermissions.ManageLedger | ModulePermissions.AddExpenses),

        (GuildFeatures.Pantry, ModulePermissions.ManagePantry),

        (GuildFeatures.Decisions, ModulePermissions.CreateDecisions | ModulePermissions.VoteDecisions),

        (GuildFeatures.GuestAccess, ModulePermissions.ManageGuests),

        (GuildFeatures.Meals, ModulePermissions.PlanMeals | ModulePermissions.ManageMeals),

        (GuildFeatures.Maintenance,
            ModulePermissions.LogMaintenance | ModulePermissions.ManageMaintenance),
    ];

    /// <summary>The capabilities the type comment lists as ungated by design, as data.</summary>
    public const Permissions NeverGated =
        Permissions.ViewChannel | Permissions.ReadMessageHistory |
        Permissions.SendMessages | Permissions.EditOwnMessages | Permissions.EditAnyMessage |
        Permissions.DeleteOwnMessages | Permissions.DeleteAnyMessage | Permissions.PinMessages |
        Permissions.AttachFiles | Permissions.EmbedLinks | Permissions.AddReactions |
        Permissions.CreateInvite | Permissions.ManageChannel | Permissions.ManagePermissions |
        Permissions.ManageGuild | Permissions.UseApplicationCommands;

    /// <summary>Every core permission bit owned by a module that <paramref name="enabled"/> does
    /// not include. A required permission intersecting this mask is unavailable in the guild no
    /// matter who is asking - owner and Superadmin included.</summary>
    public static Permissions DisabledPermissions(GuildFeatures enabled)
    {
        var disabled = Permissions.None;
        foreach (var (feature, permissions) in CorePermissionOwners)
        {
            if (!enabled.HasFlag(feature)) disabled |= permissions;
        }

        return disabled;
    }

    /// <summary>The <see cref="ModulePermissions"/> counterpart of
    /// <see cref="DisabledPermissions(GuildFeatures)"/>. Every bit in the module mask is owned by
    /// some module, so a guild with no optional modules on has this return every bit there is.
    /// </summary>
    public static ModulePermissions DisabledModulePermissions(GuildFeatures enabled)
    {
        var disabled = ModulePermissions.None;
        foreach (var (feature, permissions) in ModulePermissionOwners)
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

    /// <inheritdoc cref="IsPermissionAvailable(GuildFeatures, Permissions)"/>
    public static bool IsPermissionAvailable(GuildFeatures enabled, ModulePermissions required) =>
        (required & DisabledModulePermissions(enabled)) == ModulePermissions.None;

    /// <summary>Clamps a requested bitmask down to what the guild's modules actually allow - used
    /// where an over-broad request should be silently downgraded rather than rejected (bot
    /// installs), mirroring GuildPermissionService.ClampToGrantableAsync.</summary>
    public static Permissions ClampToEnabled(GuildFeatures enabled, Permissions requested) =>
        requested & ~DisabledPermissions(enabled);

    /// <inheritdoc cref="ClampToEnabled(GuildFeatures, Permissions)"/>
    public static ModulePermissions ClampToEnabled(GuildFeatures enabled, ModulePermissions requested) =>
        requested & ~DisabledModulePermissions(enabled);

    /// <summary>Modules no plan may withhold, whatever the plan is configured to cover.</summary>
    public const GuildFeatures PlanIndependentFeatures =
        GuildFeatures.Moderation | GuildFeatures.AutoMod | GuildFeatures.VoiceChannels;

    /// <summary>
    /// The effective module set: what the owner switched on, intersected with what the guild's plan
    /// covers, and never anything else.
    /// </summary>
    public static GuildFeatures ClampToPlan(GuildFeatures enabled, GuildFeatures includedByPlan) =>
        enabled & (includedByPlan | PlanIndependentFeatures);

    /// <inheritdoc cref="GuildFeatureResolution"/>
    public static GuildFeatureResolution ResolveWithPlan(
        GuildFeatures enabled, GuildFeatures includedByPlan) => new(enabled, includedByPlan);

    /// <summary>The modules a mask carries, by name, lowest bit first.</summary>
    public static IReadOnlyList<string> Names(GuildFeatures mask)
    {
        var names = new List<string>();

        foreach (var feature in Enum.GetValues<GuildFeatures>())
        {
            if (feature == GuildFeatures.None) continue;
            if ((mask & feature) == feature) names.Add(feature.ToString());
        }

        return names;
    }

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

/// <summary>
/// A guild's module state as the two separate masks it actually is, plus what they come to when
/// composed.
/// </summary>
/// <param name="Chosen">
/// The guild's own mask - product state, the owner's choice, the thing the module toggles write.
/// </param>
/// <param name="IncludedByPlan">
/// What the guild's plan covers - commercial state, stored by whoever owns billing and never
/// written into the guild row.
/// </param>
public readonly record struct GuildFeatureResolution(
    GuildFeatures Chosen,
    GuildFeatures IncludedByPlan)
{
    /// <summary>What every gate reads.</summary>
    public GuildFeatures Effective => GuildFeatureMap.ClampToPlan(Chosen, IncludedByPlan);

    /// <summary>Modules the owner has switched on that the plan does not cover.</summary>
    public GuildFeatures WithheldByPlan => Chosen & ~Effective;
}
