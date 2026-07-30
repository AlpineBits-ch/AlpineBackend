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
}

/// <summary>Something one housemate paid for that others owe a part of.
///
/// Money is stored as <see cref="AmountMinor"/> - integer minor units (rappen, cents) - never
/// decimal or double. Every split, balance and settlement in this module is integer arithmetic so
/// that balances sum to exactly zero; a rounding drift here is a bug people notice in their
/// actual bank account.
///
/// Shadow-side FK to Channel with no navigation property, as <see cref="ListItem"/>.</summary>
public class Expense : BaseEntity<Expense>, IPrefixedEntity
{
    public static string Prefix { get; } = "expn";

    public string ChannelId { get; set; } = null!;
    public string GuildId { get; set; } = null!;

    /// <summary>Who actually paid the shop. Distinct from CreatedByUserId - one housemate
    /// routinely enters an expense on behalf of another.</summary>
    public string PayerUserId { get; set; } = null!;

    public string Description { get; set; } = null!;

    /// <summary>Total, in minor units of the ledger channel's currency.</summary>
    public long AmountMinor { get; set; }

    public DateTimeOffset OccurredAt { get; set; }
    public ExpenseSplitKind SplitKind { get; set; } = ExpenseSplitKind.Equal;
    public string CreatedByUserId { get; set; } = null!;

    public virtual ICollection<ExpenseShare> Shares { get; set; } = [];

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
        };
    }
}

/// <summary>One participant's slice of an <see cref="Expense"/>.</summary>
public class ExpenseShare
{
    public string ExpenseId { get; set; } = null!;
    public virtual Expense Expense { get; set; } = null!;

    public string UserId { get; set; } = null!;

    /// <summary>Input weight, meaningful only for <see cref="ExpenseSplitKind.Shares"/>. Kept so
    /// an expense can be re-split after editing the total without the caller re-sending weights.</summary>
    public decimal ShareValue { get; set; }

    /// <summary>Resolved amount owed, in minor units. Always computed server-side by
    /// ExpenseSplitter, never taken from the client except as the input to Exact splits.</summary>
    public long AmountMinor { get; set; }
}

/// <summary>A real-world payment between two housemates that clears part of a balance. Recorded,
/// not enforced - nothing here moves money.</summary>
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

/// <summary>Per-ledger-channel settings, upserted like PantryConfig. One currency per ledger -
/// a multi-currency house ledger needs FX rates and a date policy, which is out of scope.</summary>
public class LedgerConfig
{
    public string ChannelId { get; set; } = null!;
    public string GuildId { get; set; } = null!;

    /// <summary>ISO-4217, uppercase.</summary>
    public string Currency { get; set; } = "CHF";

    public DateTimeOffset UpdatedAt { get; set; }
}
