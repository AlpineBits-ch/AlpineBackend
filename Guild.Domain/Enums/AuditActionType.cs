namespace Guild.Domain.Enums;

public enum AuditActionType
{
    MemberBanned,
    MemberUnbanned,
    MemberKicked,
    MemberMuted,
    MemberUnmuted,
    MemberLeft,
    MemberNicknameChanged,
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
    CategoryUpdated,
    GuildUpdated,
    GuildDeleted,
    InviteCreated,
    InviteDeleted,
    BotInstalled,
    BotUninstalled,
    GuildImportedFromDiscord,
    GuildSyncedFromDiscord,
    MessagePinned,
    MessageUnpinned,
    EmojiCreated,
    EmojiDeleted,
    AutoModConfigUpdated,
    AutoModMessageBlocked,
    OnboardingConfigUpdated,
    OnboardingPromptCreated,
    OnboardingPromptUpdated,
    OnboardingPromptDeleted,
    WelcomeScreenUpdated,
    ScheduledEventCreated,
    ScheduledEventUpdated,
    ScheduledEventCancelled,
    ScheduledEventDeleted,
    TemplateCreated,
    GuildCreatedFromTemplate,
    ChannelFollowCreated,
    ChannelFollowRemoved,
    ForumTagCreated,
    ForumTagUpdated,
    ForumTagDeleted,
    ForumTagsReordered,
    ForumConfigUpdated,
    ThreadTagsUpdated,
    ThreadPinChanged,
    ThreadLockChanged,

    // ── Household ──────────────────────────────────────────────────────────── The ledger is the
    // one module where a permission lets you rewrite something that is somebody else's money, so
    // every mutation on it is recorded.
    ExpenseCreated,
    ExpenseUpdated,
    ExpenseDeleted,
    SettlementRecorded,
    LedgerConfigUpdated,

    /// <summary>A household removed someone who moved out - the flow that stands in for
    /// MemberKicked in a guild with the Moderation module off.</summary>
    MemberMovedOut,

    // ── Household (second wave) ────────────────────────────────────────────── Recurring expenses
    // are audited for the same reason one-off ones are, and more so: a template quietly charges
    // everybody every month, so who set it up and who changed the amount are the two questions a
    // disputed ledger asks first.
    RecurringExpenseCreated,
    RecurringExpenseUpdated,
    RecurringExpenseDeleted,
    BillPosted,
    BillSkipped,
    MaintenanceAssetCreated,
    MaintenanceAssetUpdated,
    MaintenanceAssetDeleted,
    MaintenanceRecordCreated,
}
