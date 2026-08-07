using Guild.Domain.Enums;
using Persistence;

namespace Guild.Domain.Entity;

public class CreateExpenseParams
{
    public string ChannelId { get; set; } = null!;
    public string GuildId { get; set; } = null!;
    public string PayerUserId { get; set; } = null!;
    public string Description { get; set; } = null!;
    public long AmountMinor { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public ExpenseSplitKind SplitKind { get; set; } = ExpenseSplitKind.Equal;
    public string CreatedByUserId { get; set; } = null!;
    public ExpenseCategory Category { get; set; } = ExpenseCategory.Uncategorized;

    /// <summary>Set when this expense was posted from a recurring bill, so the ledger can show
    /// "rent, March" as one of a series rather than as twelve unrelated rows.</summary>
    public string? BillOccurrenceId { get; set; }
}

/// <summary>Something one housemate paid for that others owe a part of.</summary>
public class Expense : BaseEntity<Expense>, IPrefixedEntity
{
    public static string Prefix { get; } = "expn";

    public string ChannelId { get; set; } = null!;
    public string GuildId { get; set; } = null!;

    /// <summary>Who actually paid the shop.</summary>
    public string PayerUserId { get; set; } = null!;

    public string Description { get; set; } = null!;

    /// <summary>Total, in minor units of the ledger channel's currency.</summary>
    public long AmountMinor { get; set; }

    public DateTimeOffset OccurredAt { get; set; }
    public ExpenseSplitKind SplitKind { get; set; } = ExpenseSplitKind.Equal;
    public string CreatedByUserId { get; set; } = null!;

    /// <summary>
    /// Coarse spending bucket, for the "what does this flat cost per month" rollup.
    /// </summary>
    public ExpenseCategory Category { get; set; } = ExpenseCategory.Uncategorized;

    /// <summary>The <c>BillOccurrence</c> this expense was posted from, if any.</summary>
    public string? BillOccurrenceId { get; set; }

    public virtual ICollection<ExpenseShare> Shares { get; set; } = [];

    public virtual ICollection<ExpenseReceipt> Receipts { get; set; } = [];

    public static Expense Create(CreateExpenseParams @params)
    {
        var date = DateTimeOffset.UtcNow;
        return new Expense
        {
            Id = GenerateId(),
            CreatedAt = date,
            UpdatedAt = date,
            ChannelId = @params.ChannelId,
            GuildId = @params.GuildId,
            PayerUserId = @params.PayerUserId,
            Description = @params.Description,
            AmountMinor = @params.AmountMinor,
            OccurredAt = @params.OccurredAt,
            SplitKind = @params.SplitKind,
            CreatedByUserId = @params.CreatedByUserId,
            Category = @params.Category,
            BillOccurrenceId = @params.BillOccurrenceId,
        };
    }
}

/// <summary>One participant's slice of an <see cref="Expense"/>.</summary>
public class ExpenseShare
{
    public string ExpenseId { get; set; } = null!;
    public virtual Expense Expense { get; set; } = null!;

    public string UserId { get; set; } = null!;

    /// <summary>Input weight, meaningful only for <see cref="ExpenseSplitKind.Shares"/>.</summary>
    public decimal ShareValue { get; set; }

    /// <summary>Resolved amount owed, in minor units.</summary>
    public long AmountMinor { get; set; }
}

/// <summary>A real-world payment between two housemates that clears part of a balance.</summary>
public class Settlement : BaseEntity<Settlement>, IPrefixedEntity
{
    public static string Prefix { get; } = "setl";

    public string ChannelId { get; set; } = null!;
    public string GuildId { get; set; } = null!;

    public string FromUserId { get; set; } = null!;
    public string ToUserId { get; set; } = null!;
    public long AmountMinor { get; set; }
    public DateTimeOffset SettledAt { get; set; }
    public string RecordedByUserId { get; set; } = null!;
}

/// <summary>Per-ledger-channel settings, upserted like PantryConfig.</summary>
public class LedgerConfig
{
    public string ChannelId { get; set; } = null!;
    public string GuildId { get; set; } = null!;

    /// <summary>ISO-4217, uppercase.</summary>
    public string Currency { get; set; } = "CHF";

    public DateTimeOffset UpdatedAt { get; set; }
}
