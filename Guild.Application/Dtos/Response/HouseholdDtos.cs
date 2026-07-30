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
