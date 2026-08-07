using Guild.Domain.Enums;

namespace Guild.Application.Dtos.Request;

// ── Recurring expenses ───────────────────────────────────────────────────────

public class RecurringExpenseShareDto
{
    public string UserId { get; set; } = null!;

    /// <summary>Weight for Shares splits, exact minor-unit amount for Exact splits, ignored for
    /// Equal splits.</summary>
    public decimal ShareValue { get; set; }
}

public class CreateRecurringExpenseDto
{
    public string Description { get; set; } = null!;

    /// <summary>Minor units (rappen/cents).</summary>
    public long? AmountMinor { get; set; }

    /// <summary>Who fronts it. Defaults to the caller.</summary>
    public string? PayerUserId { get; set; }

    public ExpenseSplitKind SplitKind { get; set; } = ExpenseSplitKind.Equal;
    public ExpenseCategory Category { get; set; } = ExpenseCategory.Uncategorized;

    public RecurrenceUnit RecurrenceUnit { get; set; } = RecurrenceUnit.Month;
    public int RecurrenceInterval { get; set; } = 1;

    /// <summary>The first due date. Defaults to now, which makes the first bill due immediately.</summary>
    public DateTimeOffset? AnchorAt { get; set; }

    /// <summary>How many days ahead each bill is generated and announced, 0 to 30.</summary>
    public int LeadDays { get; set; } = Domain.Entity.RecurringExpense.DefaultLeadDays;

    /// <summary>Post to the ledger without asking.</summary>
    public bool AutoPost { get; set; }

    /// <summary>Who owes a part of it.</summary>
    public List<RecurringExpenseShareDto> Shares { get; set; } = [];
}

public class UpdateRecurringExpenseDto
{
    public string? Description { get; set; }

    public long? AmountMinor { get; set; }

    /// <summary>Explicit, because null on <see cref="AmountMinor"/> already means "leave it alone"
    /// and turning a fixed bill into a variable one has to be expressible.</summary>
    public bool? ClearAmount { get; set; }

    public string? PayerUserId { get; set; }
    public ExpenseSplitKind? SplitKind { get; set; }
    public ExpenseCategory? Category { get; set; }
    public RecurrenceUnit? RecurrenceUnit { get; set; }
    public int? RecurrenceInterval { get; set; }
    public DateTimeOffset? AnchorAt { get; set; }
    public int? LeadDays { get; set; }
    public bool? AutoPost { get; set; }
    public bool? IsPaused { get; set; }

    /// <summary>Null leaves the split alone; an empty list resets it to "everyone in the
    /// guild".</summary>
    public List<RecurringExpenseShareDto>? Shares { get; set; }
}

// ── Bills ────────────────────────────────────────────────────────────────────

public class PostBillDto
{
    /// <summary>Required for a variable bill, optional for a fixed one - where sending it overrides
    /// the template's figure for this period only, which is what happens when the landlord puts the
    /// rent up.</summary>
    public long? AmountMinor { get; set; }

    /// <summary>Defaults to the bill's due date rather than to now, so a bill posted three days
    /// late still lands in the month it belongs to.</summary>
    public DateTimeOffset? OccurredAt { get; set; }
}

public class SkipBillDto
{
    /// <summary>Why this period is not being charged.</summary>
    public string? Reason { get; set; }
}
