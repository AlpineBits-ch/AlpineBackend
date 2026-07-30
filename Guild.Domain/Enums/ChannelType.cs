namespace Guild.Domain.Enums;

public enum ChannelType
{
    Text,
    Voice,
    Forum,
    Ticket,
    Announcement,
    Thread,

    /// <summary>A forum variant intended to be rendered media-forward (Discord's Media channel).
    /// Behaviourally identical to Forum everywhere in this service - same posts, same tags, same
    /// config - so every forum check is written as "Forum or Media".
    /// Appended last deliberately: Npgsql maps this enum by name, and appending is the only
    /// addition Postgres can make to an existing enum type without a rewrite.</summary>
    Media,

    // ── Household modules ────────────────────────────────────────────────────
    // Channels whose contents are structured item rows rather than messages. Modelled as channel
    // types (not a separate aggregate hanging off Guild) so they inherit permission resolution,
    // category placement, ordering, ReadState unread badges and sidebar navigation unchanged.
    // Each is gated on its GuildFeatures module - see GuildFeatureMap.RequiredFeatureFor.
    // Appended, for the same Postgres reason as Media above.

    /// <summary>Shopping / todo list. Items are <c>ListItem</c> rows.</summary>
    List,

    /// <summary>Recurring chore rota. <c>Chore</c> definitions plus generated
    /// <c>ChoreOccurrence</c> rows.</summary>
    Chores,

    /// <summary>Shared expenses. <c>Expense</c> / <c>ExpenseShare</c> / <c>Settlement</c>.</summary>
    Ledger,

    /// <summary>Stock of a single location (fridge, freezer, cellar) as <c>PantryItem</c>
    /// rows.</summary>
    Pantry,

    /// <summary>Consent-based house decisions. <c>Decision</c> / <c>DecisionOption</c> /
    /// <c>DecisionVote</c>.</summary>
    Decisions
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
        ChannelType.Pantry or ChannelType.Decisions;
}