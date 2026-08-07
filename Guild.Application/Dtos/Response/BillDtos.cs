using Guild.Domain.Enums;

namespace Guild.Application.Dtos.Response;

public class RecurringExpenseShareEntryDto
{
    public required string UserId { get; set; }
    public required decimal ShareValue { get; set; }
}

public class RecurringExpenseDto
{
    public required string Id { get; set; }
    public required string ChannelId { get; set; }
    public required string Description { get; set; }

    /// <summary>Null means the amount varies and each period waits for a figure.</summary>
    public long? AmountMinor { get; set; }

    public required string Currency { get; set; }
    public required string PayerUserId { get; set; }
    public required ExpenseSplitKind SplitKind { get; set; }
    public required ExpenseCategory Category { get; set; }
    public required RecurrenceUnit RecurrenceUnit { get; set; }
    public required int RecurrenceInterval { get; set; }
    public required DateTimeOffset AnchorAt { get; set; }
    public required DateTimeOffset NextDueAt { get; set; }
    public required int LeadDays { get; set; }
    public required bool AutoPost { get; set; }
    public required bool IsPaused { get; set; }
    public required string CreatedByUserId { get; set; }
    public List<RecurringExpenseShareEntryDto> Shares { get; set; } = [];
}

public class BillOccurrenceDto
{
    public required string Id { get; set; }
    public required string RecurringExpenseId { get; set; }
    public required string ChannelId { get; set; }
    public required string Description { get; set; }
    public required DateTimeOffset DueAt { get; set; }
    public long? AmountMinor { get; set; }
    public required string Currency { get; set; }
    public required BillStatus Status { get; set; }
    public string? ExpenseId { get; set; }
    public string? PostedByUserId { get; set; }
    public string? SkippedByUserId { get; set; }
    public string? SkipReason { get; set; }

    /// <summary>Still pending with nobody having said what it cost.</summary>
    public required bool NeedsAmount { get; set; }

    /// <summary>Pending and past its due date.</summary>
    public required bool IsOverdue { get; set; }
}
