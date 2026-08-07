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

    /// <summary>The scanned product code, when this item got here by being scanned.</summary>
    public string? Barcode { get; set; }
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

    /// <summary>When somebody last nudged the assignee about this, or null if nobody has.</summary>
    public DateTimeOffset? NudgedAt { get; set; }
}

/// <summary>One member's share of the household workload over the balance window.</summary>
public class ChoreBalanceEntryDto
{
    public string UserId { get; set; } = null!;
    public int CompletedMinutes { get; set; }
    public int CompletedCount { get; set; }
    public int BalanceMinutes { get; set; }

    /// <summary>How many days of the balance window this member was actually here for, which is
    /// what <see cref="BalanceMinutes"/> is measured against. Surfaced so a client can explain the
    /// number instead of only showing it: "behind by 40 minutes" reads as an accusation, and
    /// "behind by 40 minutes over the 16 days you were here" reads as arithmetic.</summary>
    public int PresentDays { get; set; }
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

    /// <summary>What the house owes and when, from the ledger channels the caller can see.</summary>
    public HouseholdDigestBillsDto? Bills { get; init; }

    public HouseholdDigestMealsDto? Meals { get; init; }
    public HouseholdDigestMaintenanceDto? Maintenance { get; init; }

    /// <summary>Who is away right now, and until when.</summary>
    public IReadOnlyList<HouseholdDigestAbsenceDto>? Away { get; init; }
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

public class HouseholdDigestBillsDto
{
    /// <summary>Pending bills due inside the next fortnight, soonest first, anything already late
    /// included at the top. The same shape <see cref="HouseholdDigestChoresDto.Mine"/> takes: an
    /// overdue thing is still a thing that is due, and hiding it from the list to count it
    /// separately would leave the most urgent row off the widget.</summary>
    public required IReadOnlyList<HouseholdDigestBillDto> DueSoon { get; init; }

    public required int OverdueCount { get; init; }

    /// <summary>Variable bills that have come due with nobody having said what they cost.</summary>
    public required int NeedsAmountCount { get; init; }
}

public class HouseholdDigestBillDto
{
    public required string Id { get; init; }
    public required string ChannelId { get; init; }
    public required string Description { get; init; }
    public required DateTimeOffset DueAt { get; init; }

    /// <summary>Null while the amount is still unknown - see
    /// <see cref="HouseholdDigestBillsDto.NeedsAmountCount"/>.</summary>
    public long? AmountMinor { get; init; }

    public required string Currency { get; init; }

    /// <summary>What this period will cost the caller specifically, in minor units.</summary>
    public long? MyShareMinor { get; init; }

    public required BillStatus Status { get; init; }
}

public class HouseholdDigestMealsDto
{
    /// <summary>What is planned for today, in board order across every meals channel the caller can
    /// see.</summary>
    public required IReadOnlyList<HouseholdDigestMealDto> Today { get; init; }

    /// <summary>Whether the caller is down to cook any of today's entries.</summary>
    public required bool ImCookingToday { get; init; }
}

public class HouseholdDigestMealDto
{
    public required string Id { get; init; }
    public required string ChannelId { get; init; }
    public required MealSlot Slot { get; init; }

    /// <summary>The recipe's title, or the entry's free text.</summary>
    public required string Title { get; init; }

    public string? CookUserId { get; init; }
}

public class HouseholdDigestMaintenanceDto
{
    public required int BrokenCount { get; init; }
    public required int ServiceOverdueCount { get; init; }

    /// <summary>Warranties lapsing inside <see cref="Domain.Entity.MaintenanceAsset.WarrantyWarningDays"/>.
    /// Already-lapsed ones are not counted: there is nothing left to do about them.</summary>
    public required int WarrantyExpiringCount { get; init; }

    /// <summary>The few assets worth showing, most urgent first.</summary>
    public required IReadOnlyList<HouseholdDigestAssetDto> Attention { get; init; }
}

public class HouseholdDigestAssetDto
{
    public required string Id { get; init; }
    public required string ChannelId { get; init; }
    public required string Name { get; init; }
    public required AssetStatus Status { get; init; }

    /// <summary>
    /// The single most urgent of the attention board's tokens - <c>broken</c>,
    /// <c>service_overdue</c>, <c>needs_attention</c>, <c>warranty_expiring</c>.
    /// </summary>
    public required string Reason { get; init; }
}

public class HouseholdDigestAbsenceDto
{
    public required string UserId { get; init; }
    public required DateTimeOffset StartAt { get; init; }

    /// <summary>Exclusive, as <see cref="Domain.Entity.MemberAbsence.EndAt"/> is.</summary>
    public required DateTimeOffset EndAt { get; init; }

    public string? Note { get; init; }
}
