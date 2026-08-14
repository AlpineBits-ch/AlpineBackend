namespace Guild.Domain.Enums;

/// <summary>The permission bits owned by an optional <see cref="GuildFeatures"/> module: the wiki
/// and the household modules. Carried as a second mask alongside <see cref="Permissions"/>
/// everywhere a permission mask is stored, cached or resolved.
///
/// <para>The split exists because the core mask ran out of room. Before it, twenty-four of the
/// sixty-four core bits were spent on features most guilds never switch on, leaving four free -
/// fewer than the Discord-parity bits that were already known to be missing. Widening to a
/// <c>ulong[]</c> or a decimal-string bitfield would have touched every stored mask, every cached
/// blob and every external contract for the same benefit; a sibling enum touches the same things
/// once and then gives both halves sixty-four bits of headroom independently.</para>
///
/// <para>The dividing line is not "small feature vs. big feature" but "can this bit ever be
/// meaningless?". Everything in here belongs to a module that <see cref="GuildFeatureMap"/> can
/// switch off wholesale, at which point the bit is clamped out of the resolved mask for everyone,
/// owner and Superadmin included. Everything in <see cref="Permissions"/> is always meaningful.
/// That is also why there is no Superadmin bit here: authority is a property of the core mask, and
/// a module being off is a product state rather than an authorization level, so there is nothing
/// for a catch-all bit to override.</para>
///
/// <para><b>Old-to-new bit mapping.</b> These members used to live in <see cref="Permissions"/>.
/// The migration <c>RemapModulePermissionBits</c> moves every stored mask across using exactly
/// this table, and its <c>Down</c> inverts it, so the table is the contract between the two enums
/// and must not be edited after the fact:</para>
///
/// <code>
///   Permissions bit  ->  ModulePermissions bit   member
///   ───────────────────────────────────────────────────────────────
///           23       ->          0               ViewWiki
///           24       ->          1               CreateWikiPages
///           25       ->          2               EditOwnWikiPages
///           26       ->          3               EditAnyWikiPage
///           27       ->          4               DeleteWikiPages
///           28       ->          5               ManageWikiRevisions
///           29       ->          6               ManageWikiStructure
///           30       ->          7               ModerateWikiComments
///           31       ->          8               PublishWikiPublicly
///           39       ->          9               ManageLists
///           40       ->         10               AddListItems
///           41       ->         11               CheckOffListItems
///           42       ->         12               ManageChores
///           43       ->         13               CompleteChores
///           44       ->         14               ManageLedger
///           45       ->         15               AddExpenses
///           46       ->         16               ManagePantry
///           47       ->         17               CreateDecisions
///           48       ->         18               VoteDecisions
///           49       ->         19               ManageGuests
///           55       ->         20               PlanMeals
///           56       ->         21               ManageMeals
///           57       ->         22               LogMaintenance
///           58       ->         23               ManageMaintenance
/// </code>
///
/// <para>Bit positions in here are a storage format for the same reasons the core ones are: they
/// are persisted in the <c>*_module_permissions</c> columns, cached as JSON, and mirrored by name
/// in <c>Guild.Contracts.ExternalModulePermission</c>. Add at bit 24 or above; never move an
/// existing member.</para>
/// </summary>
[Flags]
public enum ModulePermissions : ulong
{
    None                    = 0,

    // ── Wiki ─────────────────────────────────────────────────────────────────
    ViewWiki                = 1ul << 0,
    CreateWikiPages         = 1ul << 1,
    EditOwnWikiPages        = 1ul << 2,
    EditAnyWikiPage         = 1ul << 3,
    DeleteWikiPages         = 1ul << 4,
    ManageWikiRevisions     = 1ul << 5,
    ManageWikiStructure     = 1ul << 6,
    ModerateWikiComments    = 1ul << 7,
    PublishWikiPublicly     = 1ul << 8,

    // ── Household modules ────────────────────────────────────────────────────
    ManageLists             = 1ul << 9,
    AddListItems            = 1ul << 10,
    CheckOffListItems       = 1ul << 11,
    ManageChores            = 1ul << 12,
    CompleteChores          = 1ul << 13,
    ManageLedger            = 1ul << 14,
    AddExpenses             = 1ul << 15,
    ManagePantry            = 1ul << 16,
    CreateDecisions         = 1ul << 17,
    VoteDecisions           = 1ul << 18,
    ManageGuests            = 1ul << 19,

    // ── Household modules (second wave) ──────────────────────────────────────

    /// <summary>Put something on the meal plan, and add a recipe.</summary>
    PlanMeals               = 1ul << 20,

    /// <summary>Edit or delete somebody else's recipe or planned meal, and point the plan at a
    /// different shopping list.</summary>
    ManageMeals             = 1ul << 21,

    /// <summary>
    /// Record that something was serviced or repaired, and flag an appliance as broken.
    /// </summary>
    LogMaintenance          = 1ul << 22,

    /// <summary>Add, edit and delete the assets themselves - service intervals, warranty dates,
    /// the plumber's number - and remove anyone's log entry.</summary>
    ManageMaintenance       = 1ul << 23,
}

public static class ModulePermissionExtensions
{
    /// <summary>
    /// Whether <paramref name="permissions"/> carries every bit in <paramref
    /// name="requiredPermission"/>.
    /// </summary>
    public static bool HasPermission(this ModulePermissions permissions, ModulePermissions requiredPermission) =>
        (permissions & requiredPermission) == requiredPermission;

    /// <summary>The same check with the holder's core mask alongside, so that Superadmin grants
    /// module permissions the way it grants core ones. Note that this still does not survive the
    /// module being disabled: <see cref="GuildFeatureMap"/> clamps the mask before it ever reaches
    /// here.</summary>
    public static bool HasPermission(
        this ModulePermissions permissions,
        ModulePermissions requiredPermission,
        Permissions corePermissions)
    {
        if (corePermissions.HasFlag(Permissions.Superadmin))
        {
            return true;
        }

        return (permissions & requiredPermission) == requiredPermission;
    }
}
