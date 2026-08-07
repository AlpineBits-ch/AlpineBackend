namespace Guild.Domain.Enums;

[Flags]
public enum Permissions : ulong
{
    None                    = 0,
 
    // ── Channel visibility ───────────────────────────────────────────────────
    ViewChannel             = 1ul << 0,
 
    // ── Message permissions ──────────────────────────────────────────────────
    SendMessages            = 1ul << 1,
    EditOwnMessages         = 1ul << 2,
    EditAnyMessage          = 1ul << 3,
    DeleteOwnMessages       = 1ul << 4,
    DeleteAnyMessage        = 1ul << 5,
    PinMessages             = 1ul << 6,
 
    // ── Attachment / embed permissions ───────────────────────────────────────
    AttachFiles             = 1ul << 7,
    EmbedLinks              = 1ul << 8,
 
    // ── Reaction permissions ─────────────────────────────────────────────────
    AddReactions            = 1ul << 9,
 
    // ── Voice / media permissions ────────────────────────────────────────────
    Connect                 = 1ul << 10,
    Speak                   = 1ul << 11,
    Stream                  = 1ul << 12,
    MuteMembers             = 1ul << 13,
    DeafenMembers           = 1ul << 14,
    MoveMembers             = 1ul << 15,
 
    // ── Thread permissions ───────────────────────────────────────────────────
    CreateThreads           = 1ul << 16,
    SendMessagesInThreads   = 1ul << 17,
    ManageOwnThreads        = 1ul << 18,
    ManageAnyThread         = 1ul << 19,
 
    // ── Moderation permissions ───────────────────────────────────────────────
    ManageChannel           = 1ul << 20,
    ManagePermissions       = 1ul << 21,
 
    // ── General Stuff that I forgot ──────────────────────────────────────────
    
    CreateInvite            = 1ul << 22,
    
    // -- WIKI
    
    ViewWiki                = 1ul << 23,
    CreateWikiPages             = 1ul << 24,
    EditOwnWikiPages            = 1ul << 25,
    EditAnyWikiPage             = 1ul << 26,
    DeleteWikiPages             = 1ul << 27,
    ManageWikiRevisions         = 1ul << 28,
    ManageWikiStructure         = 1ul << 29,
    ModerateWikiComments        = 1ul << 30,
    PublishWikiPublicly         = 1ul << 31,

    // ── Moderation (members) ─────────────────────────────────────────────────
    KickMembers             = 1ul << 32,
    BanMembers              = 1ul << 33,
    ModerateMembers         = 1ul << 34,
    ManageGuild             = 1ul << 35,
    ViewAuditLog            = 1ul << 36,

    // ── Customization ────────────────────────────────────────────────────────
    ManageEmojis            = 1ul << 37,
    ManageEvents            = 1ul << 38,

    // ── Household modules ────────────────────────────────────────────────────
    // Each belongs to a GuildFeatures module (see GuildFeatureMap.PermissionOwners), so all of
    // these are stripped wholesale in a guild that doesn't have the module switched on.
    ManageLists             = 1ul << 39,
    AddListItems            = 1ul << 40,
    CheckOffListItems       = 1ul << 41,
    ManageChores            = 1ul << 42,
    CompleteChores          = 1ul << 43,
    ManageLedger            = 1ul << 44,
    AddExpenses             = 1ul << 45,
    ManagePantry            = 1ul << 46,
    CreateDecisions         = 1ul << 47,
    VoteDecisions           = 1ul << 48,
    ManageGuests            = 1ul << 49,

    // ── Mentions ─────────────────────────────────────────────────────────────
    /// <summary>Ping the whole channel with @everyone / @here.</summary>
    MentionEveryone         = 1ul << 50,

    // ── Moderation (split out of coarser bits) ───────────────────────────────
    /// <summary>Create, edit, delete and reorder the guild's roles.</summary>
    ManageRoles             = 1ul << 51,

    /// <summary>Create, edit, delete and regenerate the token of a channel webhook.</summary>
    ManageWebhooks          = 1ul << 52,

    // ── Nicknames ────────────────────────────────────────────────────────────
    /// <summary>Change your own nickname in this guild. Part of the @everyone defaults.</summary>
    ChangeNickname          = 1ul << 53,

    /// <summary>Change other members' nicknames.</summary>
    ManageNicknames         = 1ul << 54,

    // ── Household modules (second wave) ──────────────────────────────────────

    /// <summary>Put something on the meal plan, and add a recipe.</summary>
    PlanMeals               = 1ul << 55,

    /// <summary>Edit or delete somebody else's recipe or planned meal, and point the plan at a
    /// different shopping list.</summary>
    ManageMeals             = 1ul << 56,

    /// <summary>
    /// Record that something was serviced or repaired, and flag an appliance as broken.
    /// </summary>
    LogMaintenance          = 1ul << 57,

    /// <summary>Add, edit and delete the assets themselves - service intervals, warranty dates,
    /// the plumber's number - and remove anyone's log entry.</summary>
    ManageMaintenance       = 1ul << 58,

    // ── Catch-all ────────────────────────────────────────────────────────────
    Superadmin              = 1ul << 63,
}
public static class PermissionExtensions
{
    public static bool HasPermission(this Permissions permissions, Permissions requiredPermission)
    {
        if (permissions.HasFlag(Permissions.Superadmin))
        {
            return true;
        }

        return (permissions & requiredPermission) == requiredPermission;
    }
}