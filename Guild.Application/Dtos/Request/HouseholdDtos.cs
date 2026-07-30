using Guild.Domain.Enums;

namespace Guild.Application.Dtos.Request;

// ── Lists ────────────────────────────────────────────────────────────────────

public class CreateListItemDto
{
    public string Text { get; set; } = null!;
    public string? Quantity { get; set; }
    public string? Note { get; set; }
    public string? Section { get; set; }
    public string? AssigneeUserId { get; set; }
}

public class UpdateListItemDto
{
    public string? Text { get; set; }
    public string? Quantity { get; set; }
    public string? Note { get; set; }
    public string? Section { get; set; }

    /// <summary>Sent as an explicit flag because null means "leave the assignee alone" while
    /// unassigning has to be expressible too.</summary>
    public bool? ClearAssignee { get; set; }
    public string? AssigneeUserId { get; set; }
}

public class ReorderListItemsDto
{
    /// <summary>Item ids in their new order. Ids not belonging to this list are rejected.</summary>
    public List<string> ItemIds { get; set; } = [];
}

// ── Pantry ───────────────────────────────────────────────────────────────────

public class CreatePantryItemDto
{
    public string Name { get; set; } = null!;
    public decimal Quantity { get; set; }
    public string? Unit { get; set; }
    public decimal? LowThreshold { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}

public class UpdatePantryItemDto
{
    public string? Name { get; set; }
    public decimal? Quantity { get; set; }
    public string? Unit { get; set; }
    public decimal? LowThreshold { get; set; }
    public bool? ClearLowThreshold { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public bool? ClearExpiresAt { get; set; }
}

public class UpdatePantryConfigDto
{
    public string? RestockListChannelId { get; set; }
    public bool? ClearRestockList { get; set; }
    public int? ExpiryWarningDays { get; set; }
}

// ── Chores ───────────────────────────────────────────────────────────────────

public class CreateChoreDto
{
    public string Title { get; set; } = null!;
    public string? Description { get; set; }
    public int IntervalDays { get; set; } = 7;
    public DateTimeOffset? AnchorAt { get; set; }
    public int EffortMinutes { get; set; } = 15;
    public string? RotationRoleId { get; set; }
    public string? FixedAssigneeUserId { get; set; }
    public int GraceHours { get; set; } = 24;
}

public class UpdateChoreDto
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public int? IntervalDays { get; set; }
    public int? EffortMinutes { get; set; }
    public string? RotationRoleId { get; set; }
    public string? FixedAssigneeUserId { get; set; }
    public int? GraceHours { get; set; }
    public bool? IsPaused { get; set; }
}

// ── Ledger ───────────────────────────────────────────────────────────────────

public class ExpenseShareDto
{
    public string UserId { get; set; } = null!;

    /// <summary>Weight for Shares splits, exact minor-unit amount for Exact splits, ignored for
    /// Equal splits.</summary>
    public decimal ShareValue { get; set; }
}

public class CreateExpenseDto
{
    public string Description { get; set; } = null!;

    /// <summary>Minor units (rappen/cents). Never a decimal - see ExpenseSplitter.</summary>
    public long AmountMinor { get; set; }

    public string? PayerUserId { get; set; }
    public DateTimeOffset? OccurredAt { get; set; }
    public ExpenseSplitKind SplitKind { get; set; } = ExpenseSplitKind.Equal;

    /// <summary>Participants. For an Equal split only UserId matters; empty means "everyone in
    /// the guild", resolved server-side.</summary>
    public List<ExpenseShareDto> Shares { get; set; } = [];
}

public class UpdateExpenseDto
{
    public string? Description { get; set; }
    public long? AmountMinor { get; set; }
    public string? PayerUserId { get; set; }
    public DateTimeOffset? OccurredAt { get; set; }
    public ExpenseSplitKind? SplitKind { get; set; }
    public List<ExpenseShareDto>? Shares { get; set; }
}

public class CreateSettlementDto
{
    public string FromUserId { get; set; } = null!;
    public string ToUserId { get; set; } = null!;
    public long AmountMinor { get; set; }
    public DateTimeOffset? SettledAt { get; set; }
}

public class UpdateLedgerConfigDto
{
    public string Currency { get; set; } = "CHF";
}

// ── Decisions ────────────────────────────────────────────────────────────────

public class CreateDecisionDto
{
    public string Title { get; set; } = null!;
    public string? Description { get; set; }
    public DateTimeOffset? ClosesAt { get; set; }
    public int? Quorum { get; set; }
    public List<string> Options { get; set; } = [];
}

public class UpdateDecisionDto
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset? ClosesAt { get; set; }
    public int? Quorum { get; set; }
}

public class CastDecisionVoteDto
{
    public DecisionVoteKind Kind { get; set; }

    /// <summary>Which option. Required for Support; for Block, null objects to the decision as a
    /// whole rather than to one option.</summary>
    public string? OptionId { get; set; }

    /// <summary>Required when Kind is Block.</summary>
    public string? Reason { get; set; }
}

// ── Home status / quiet hours / guests ───────────────────────────────────────

public class SetHomeStatusDto
{
    public HomeStatusKind Kind { get; set; }
    public string? Note { get; set; }

    /// <summary>How long before the status decays back to unset. Defaults to 12 hours; capped at
    /// 7 days so a forgotten "Out" doesn't linger forever.</summary>
    public int? ExpiresInMinutes { get; set; }
}

public class UpdateQuietHoursDto
{
    public bool Enabled { get; set; }
    public int StartMinuteLocal { get; set; } = 22 * 60;
    public int EndMinuteLocal { get; set; } = 7 * 60;
    public string TimeZoneId { get; set; } = "UTC";
}

public class GrantTemporaryRoleDto
{
    public DateTimeOffset ExpiresAt { get; set; }
}
