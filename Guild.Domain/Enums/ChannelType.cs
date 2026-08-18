namespace Guild.Domain.Enums;

public enum ChannelType
{
    Text,
    Voice,
    Forum,
    Ticket,
    Announcement,
    Thread,

    /// <summary>
    /// A forum variant intended to be rendered media-forward (Discord's Media channel).
    /// </summary>
    Media,

    // ── Household modules ──────────────────────────────────────────────────── Channels whose
    // contents are structured item rows rather than messages.

    /// <summary>Shopping / todo list. Items are <c>ListItem</c> rows.</summary>
    List,

    /// <summary>Recurring chore rota.</summary>
    Chores,

    /// <summary>Shared expenses. <c>Expense</c> / <c>ExpenseShare</c> / <c>Settlement</c>.</summary>
    Ledger,

    /// <summary>Stock of a single location (fridge, freezer, cellar) as <c>PantryItem</c>
    /// rows.</summary>
    Pantry,

    /// <summary>Consent-based house decisions.</summary>
    Decisions,

    /// <summary>Recipes and the week's meal plan.</summary>
    Meals,

    /// <summary>The house's appliances and their service history.</summary>
    Maintenance,

    // ── Roleplay ─────────────────────────────────────────────────────────────

    /// <summary>A thread variant carrying a participant list and a turn order in
    /// <c>SceneState</c>. Appended rather than filed next to <see cref="Thread"/> so no existing
    /// type's ordinal moves.</summary>
    Scene
}

public static class ChannelTypeExtensions
{
    /// <summary>Forum and Media are the same channel behaviourally - only the client's intended
    /// rendering differs - so every forum-side check goes through this rather than spelling out
    /// the pair and eventually missing one.</summary>
    public static bool IsForum(this ChannelType type) => type is ChannelType.Forum or ChannelType.Media;

    /// <summary>Channel types whose contents are structured item rows rather than messages. These
    /// carry no message history, so anything message-oriented (posting, pinning, read state
    /// derived from messages, the system-channel picker) must exclude them.</summary>
    public static bool IsHouseholdModule(this ChannelType type) => type is
        ChannelType.List or ChannelType.Chores or ChannelType.Ledger or
        ChannelType.Pantry or ChannelType.Decisions or ChannelType.Meals or
        ChannelType.Maintenance;

    /// <summary>Channel types that behave as a thread: a free-text title, a parent channel, a
    /// creator and an archive flag. A scene the thread code did not recognise would be missing from
    /// thread lists, never auto-archive and escape ManageOwnThreads/ManageAnyThread, so every
    /// thread-side check goes through this rather than naming one member and eventually missing the
    /// other.</summary>
    public static bool IsThreadShaped(this ChannelType type) =>
        type is ChannelType.Thread or ChannelType.Scene;

    /// <summary>The same set as <see cref="IsThreadShaped"/> in the one form EF can translate: an
    /// extension method inside a <c>Where</c> cannot be, and spelling the pair out at each query is
    /// how the second member gets missed.</summary>
    public static readonly ChannelType[] ThreadShaped = [ChannelType.Thread, ChannelType.Scene];
}