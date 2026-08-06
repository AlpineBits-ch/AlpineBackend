using Guild.Domain.Enums;

namespace Guild.Application.Dtos.Response;

public class ListItemDto
{
    public string Id { get; set; } = null!;
    public string ChannelId { get; set; } = null!;
    public string Text { get; set; } = null!;
    public string? Quantity { get; set; }
    public string? Note { get; set; }
    public string? Section { get; set; }
    public string? AssigneeUserId { get; set; }
    public string AddedByUserId { get; set; } = null!;
    public bool IsChecked { get; set; }
    public DateTimeOffset? CheckedAt { get; set; }
    public string? CheckedByUserId { get; set; }
    public int Position { get; set; }
    public string? SourcePantryItemId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class PantryItemDto
{
    public string Id { get; set; } = null!;
    public string ChannelId { get; set; } = null!;
    public string Name { get; set; } = null!;
    public decimal Quantity { get; set; }
    public string? Unit { get; set; }
    public decimal? LowThreshold { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public bool IsLow { get; set; }
    public DateTimeOffset? RestockedAt { get; set; }
    public string AddedByUserId { get; set; } = null!;
}

public class PantryConfigDto
{
    public string ChannelId { get; set; } = null!;
    public string? RestockListChannelId { get; set; }
    public int ExpiryWarningDays { get; set; }
}

public class ChoreDto
{
    public string Id { get; set; } = null!;
    public string ChannelId { get; set; } = null!;
    public string Title { get; set; } = null!;
    public string? Description { get; set; }
    public int IntervalDays { get; set; }
    public DateTimeOffset AnchorAt { get; set; }
    public int EffortMinutes { get; set; }
    public string? RotationRoleId { get; set; }
    public string? FixedAssigneeUserId { get; set; }
    public int GraceHours { get; set; }
    public bool IsPaused { get; set; }
    public DateTimeOffset NextDueAt { get; set; }
}

public class ChoreOccurrenceDto
{
    public string Id { get; set; } = null!;
    public string ChoreId { get; set; } = null!;
    public string ChannelId { get; set; } = null!;
    public string Title { get; set; } = null!;
    public DateTimeOffset DueAt { get; set; }
    public string AssignedUserId { get; set; } = null!;
    public int EffortMinutes { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? CompletedByUserId { get; set; }
    public DateTimeOffset? SkippedAt { get; set; }
    public bool IsOverdue { get; set; }
}

/// <summary>One member's share of the household workload over the balance window.</summary>
public class ChoreBalanceEntryDto
{
    public string UserId { get; set; } = null!;
    public int CompletedMinutes { get; set; }
    public int CompletedCount { get; set; }
    public int BalanceMinutes { get; set; }
}

public class ExpenseDto
{
    public string Id { get; set; } = null!;
    public string ChannelId { get; set; } = null!;
    public string PayerUserId { get; set; } = null!;
    public string Description { get; set; } = null!;
    public long AmountMinor { get; set; }
    public string Currency { get; set; } = null!;
    public DateTimeOffset OccurredAt { get; set; }
    public ExpenseSplitKind SplitKind { get; set; }
    public string CreatedByUserId { get; set; } = null!;
    public List<ExpenseShareEntryDto> Shares { get; set; } = [];
}

public class ExpenseShareEntryDto
{
    public string UserId { get; set; } = null!;
    public decimal ShareValue { get; set; }
    public long AmountMinor { get; set; }
}

public class LedgerBalanceDto
{
    public string UserId { get; set; } = null!;

    /// <summary>Positive = the house owes them, negative = they owe the house.</summary>
    public long NetMinor { get; set; }
}

public class SettlementDto
{
    public string Id { get; set; } = null!;
    public string FromUserId { get; set; } = null!;
    public string ToUserId { get; set; } = null!;
    public long AmountMinor { get; set; }
    public DateTimeOffset SettledAt { get; set; }
    public string RecordedByUserId { get; set; } = null!;
}

public class TransferSuggestionDto
{
    public string FromUserId { get; set; } = null!;
    public string ToUserId { get; set; } = null!;
    public long AmountMinor { get; set; }
}

public class DecisionDto
{
    public string Id { get; set; } = null!;
    public string ChannelId { get; set; } = null!;
    public string Title { get; set; } = null!;
    public string? Description { get; set; }
    public string CreatedByUserId { get; set; } = null!;
    public DateTimeOffset? ClosesAt { get; set; }
    public int? Quorum { get; set; }
    public DecisionStatus Status { get; set; }
    public string? OutcomeOptionId { get; set; }
    public List<DecisionOptionDto> Options { get; set; } = [];

    /// <summary>Every block cast, with its reason.</summary>
    public List<DecisionBlockDto> Blocks { get; set; } = [];

    public string? MyVoteOptionId { get; set; }
    public DecisionVoteKind? MyVoteKind { get; set; }
}

public class DecisionOptionDto
{
    public string Id { get; set; } = null!;
    public string Title { get; set; } = null!;
    public int Position { get; set; }
    public int SupportCount { get; set; }
    public bool IsBlocked { get; set; }
}

public class DecisionBlockDto
{
    public string UserId { get; set; } = null!;

    /// <summary>Null when the block objects to the decision as a whole.</summary>
    public string? OptionId { get; set; }

    public string Reason { get; set; } = null!;
}

public class HomeStatusDto
{
    public string UserId { get; set; } = null!;
    public HomeStatusKind Kind { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public class QuietHoursDto
{
    public bool Enabled { get; set; }
    public int StartMinuteLocal { get; set; }
    public int EndMinuteLocal { get; set; }
    public string TimeZoneId { get; set; } = null!;
}

// ── Home digest ──────────────────────────────────────────────────────────────

/// <summary>
/// Everything a home tab, a lock-screen widget or a watch face needs, in one response.
/// </summary>
public class HouseholdDigestDto
{
    public required string GuildId { get; init; }

    public HouseholdDigestChoresDto? Chores { get; init; }
    public IReadOnlyList<HouseholdDigestListDto>? Lists { get; init; }
    public HouseholdDigestPantryDto? Pantry { get; init; }
    public IReadOnlyList<HouseholdDigestLedgerDto>? Ledger { get; init; }
    public HouseholdDigestDecisionsDto? Decisions { get; init; }
    public IReadOnlyList<HomeStatusDto>? HomeStatus { get; init; }
}

public class HouseholdDigestChoresDto
{
    /// <summary>Yours, due inside the next day or already past due. The one thing a widget shows.</summary>
    public required IReadOnlyList<ChoreOccurrenceDto> Mine { get; init; }

    /// <summary>How many of <see cref="Mine"/> are past their grace period.</summary>
    public required int MineOverdueCount { get; init; }

    /// <summary>Overdue across the whole house, yours included - the "somebody needs to do
    /// something" number.</summary>
    public required int HouseOverdueCount { get; init; }
}

public class HouseholdDigestListDto
{
    public required string ChannelId { get; init; }
    public required string ChannelName { get; init; }
    public required int OpenCount { get; init; }

    /// <summary>The first few unchecked lines, in board order, so a widget can render the list
    /// itself rather than a count.</summary>
    public required IReadOnlyList<ListItemDto> Preview { get; init; }
}

public class HouseholdDigestPantryDto
{
    /// <summary>How many items are inside their own pantry's warning horizon.</summary>
    public required int ExpiringCount { get; init; }

    public required IReadOnlyList<PantryItemDto> Soonest { get; init; }
}

public class HouseholdDigestLedgerDto
{
    public required string ChannelId { get; init; }
    public required string ChannelName { get; init; }
    public required string Currency { get; init; }

    /// <summary>The caller's own net position. Positive means the house owes them.</summary>
    public required long MyNetMinor { get; init; }
}

public class HouseholdDigestDecisionsDto
{
    public required int OpenCount { get; init; }

    /// <summary>Open decisions the caller has not voted on.</summary>
    public required IReadOnlyList<HouseholdDigestDecisionDto> AwaitingMyVote { get; init; }
}

public class HouseholdDigestDecisionDto
{
    public required string Id { get; init; }
    public required string ChannelId { get; init; }
    public required string Title { get; init; }
    public DateTimeOffset? ClosesAt { get; init; }
}
