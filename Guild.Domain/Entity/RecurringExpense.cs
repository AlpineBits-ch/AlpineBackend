using Guild.Domain.Enums;
using Persistence;

namespace Guild.Domain.Entity;

public class CreateRecurringExpenseParams
{
    public string ChannelId { get; set; } = null!;
    public string GuildId { get; set; } = null!;
    public string Description { get; set; } = null!;

    /// <summary>Null means the amount varies and has to be confirmed each period.</summary>
    public long? AmountMinor { get; set; }

    public string PayerUserId { get; set; } = null!;
    public ExpenseSplitKind SplitKind { get; set; } = ExpenseSplitKind.Equal;
    public ExpenseCategory Category { get; set; } = ExpenseCategory.Uncategorized;
    public RecurrenceUnit RecurrenceUnit { get; set; } = RecurrenceUnit.Month;
    public int RecurrenceInterval { get; set; } = 1;
    public DateTimeOffset AnchorAt { get; set; }
    public int LeadDays { get; set; } = RecurringExpense.DefaultLeadDays;
    public bool AutoPost { get; set; }
    public string CreatedByUserId { get; set; } = null!;
}

/// <summary>
/// A bill the house owes on a cadence: rent on the 1st, internet on the 15th, electricity
/// quarterly.
/// </summary>
public class RecurringExpense : BaseEntity<RecurringExpense>, IPrefixedEntity
{
    public static string Prefix { get; } = "rexp";

    /// <summary>Days ahead a bill is generated and announced.</summary>
    public const int DefaultLeadDays = 5;

    public const int MaxLeadDays = 30;

    /// <summary>Bound on every schedule walk, matching <see cref="Chore"/>'s.</summary>
    private const int MaxSteps = 4096;

    public string ChannelId { get; set; } = null!;
    public string GuildId { get; set; } = null!;

    public string Description { get; set; } = null!;

    /// <summary>The total, in minor units, or null when it varies.</summary>
    public long? AmountMinor { get; set; }

    /// <summary>Who fronts it.</summary>
    public string PayerUserId { get; set; } = null!;

    public ExpenseSplitKind SplitKind { get; set; } = ExpenseSplitKind.Equal;

    public ExpenseCategory Category { get; set; } = ExpenseCategory.Uncategorized;

    /// <summary>The calendar unit the schedule steps in.</summary>
    public RecurrenceUnit RecurrenceUnit { get; set; } = RecurrenceUnit.Month;

    /// <summary>How many <see cref="RecurrenceUnit"/>s between due dates.</summary>
    public int RecurrenceInterval { get; set; } = 1;

    /// <summary>The first due date; every later slot is counted from it.</summary>
    public DateTimeOffset AnchorAt { get; set; }

    /// <summary>When the next occurrence is due.</summary>
    public DateTimeOffset NextDueAt { get; set; }

    /// <summary>How far ahead of <see cref="NextDueAt"/> the occurrence is generated and the house
    /// is told. 0 to <see cref="MaxLeadDays"/>.</summary>
    public int LeadDays { get; set; } = DefaultLeadDays;

    /// <summary>
    /// Post the occurrence to the ledger when it comes due, without anybody confirming.
    /// </summary>
    public bool AutoPost { get; set; }

    public bool IsPaused { get; set; }

    public string CreatedByUserId { get; set; } = null!;

    /// <summary>Who owes a part of it and in what proportion.</summary>
    public virtual ICollection<RecurringExpenseShare> Shares { get; set; } = [];

    public static RecurringExpense Create(CreateRecurringExpenseParams @params)
    {
        var date = DateTimeOffset.UtcNow;
        return new RecurringExpense
        {
            Id = GenerateId(),
            CreatedAt = date,
            UpdatedAt = date,
            ChannelId = @params.ChannelId,
            GuildId = @params.GuildId,
            Description = @params.Description,
            AmountMinor = @params.AmountMinor,
            PayerUserId = @params.PayerUserId,
            SplitKind = @params.SplitKind,
            Category = @params.Category,
            RecurrenceUnit = @params.RecurrenceUnit,
            RecurrenceInterval = @params.RecurrenceInterval,
            AnchorAt = @params.AnchorAt,
            NextDueAt = @params.AnchorAt,
            LeadDays = @params.LeadDays,
            AutoPost = @params.AutoPost,
            CreatedByUserId = @params.CreatedByUserId,
        };
    }

    /// <summary>The widest interval each unit accepts.</summary>
    public static int MaxIntervalFor(RecurrenceUnit unit) => unit switch
    {
        RecurrenceUnit.Day => 365,
        RecurrenceUnit.Week => 52,
        RecurrenceUnit.Month => 24,
        RecurrenceUnit.Year => 5,
        _ => 1,
    };

    public static bool IsIntervalValid(RecurrenceUnit unit, int interval) =>
        interval >= 1 && interval <= MaxIntervalFor(unit);

    /// <summary>
    /// The <paramref name="index"/>th scheduled slot, counted from <see cref="AnchorAt"/>.
    /// </summary>
    public DateTimeOffset SlotAt(int index)
    {
        var step = Math.Max(1, RecurrenceInterval) * Math.Max(0, index);

        return RecurrenceUnit switch
        {
            RecurrenceUnit.Day => AnchorAt.AddDays(step),
            RecurrenceUnit.Week => AnchorAt.AddDays(step * 7L),
            RecurrenceUnit.Month => AnchorAt.AddMonths(step),
            RecurrenceUnit.Year => AnchorAt.AddYears(step),
            _ => AnchorAt.AddMonths(step),
        };
    }

    /// <summary>Advances to the first scheduled slot strictly after <paramref name="after"/>,
    /// staying on the anchor's cadence. Paying a bill four days late must not move the due date for
    /// every month after it, which is the same reason <see cref="Chore.AdvanceFrom"/> steps from the
    /// anchor rather than from when the work happened.</summary>
    public DateTimeOffset AdvanceFrom(DateTimeOffset after) => SlotAt(IndexAtOrBefore(after) + 1);

    /// <summary>
    /// Drops <see cref="NextDueAt"/> forward to the most recent slot at or before <paramref
    /// name="now"/>, and reports how many slots were passed over.
    /// </summary>
    public int FastForwardTo(DateTimeOffset now)
    {
        var index = IndexAtOrBefore(NextDueAt);
        var skipped = 0;

        while (skipped < MaxSteps)
        {
            var next = SlotAt(index + 1);
            if (next > now) break;

            NextDueAt = next;
            index++;
            skipped++;
        }

        return skipped;
    }

    /// <summary>The index of the last slot at or before <paramref name="instant"/>, or -1 when the
    /// whole schedule is still in the future.</summary>
    private int IndexAtOrBefore(DateTimeOffset instant)
    {
        if (instant < AnchorAt) return -1;

        var index = 0;
        while (index < MaxSteps && SlotAt(index + 1) <= instant) index++;

        return index;
    }
}

/// <summary>One participant's standing slice of a <see cref="RecurringExpense"/>.</summary>
public class RecurringExpenseShare
{
    public string RecurringExpenseId { get; set; } = null!;
    public virtual RecurringExpense RecurringExpense { get; set; } = null!;

    public string UserId { get; set; } = null!;

    /// <summary>Input weight, meaningful only for <see cref="ExpenseSplitKind.Shares"/> and
    /// <see cref="ExpenseSplitKind.Exact"/>.</summary>
    public decimal ShareValue { get; set; }
}

/// <summary>
/// One generated instance of a <see cref="RecurringExpense"/> - the row that says "rent is due on
/// Friday" before anybody has paid anything.
/// </summary>
public class BillOccurrence : BaseEntity<BillOccurrence>, IPrefixedEntity
{
    public static string Prefix { get; } = "bill";

    public string RecurringExpenseId { get; set; } = null!;
    public string GuildId { get; set; } = null!;

    /// <summary>Denormalized from the template so the board can be queried per channel without a
    /// join, and so authorization needs none either.</summary>
    public string ChannelId { get; set; } = null!;

    public DateTimeOffset DueAt { get; set; }

    /// <summary>Copied from the template when the amount is fixed; null until somebody confirms it
    /// on a variable bill. Snapshotted rather than read through the template, so correcting next
    /// month's rent does not silently rewrite what was charged in March.</summary>
    public long? AmountMinor { get; set; }

    public BillStatus Status { get; set; } = BillStatus.Pending;

    /// <summary>The <see cref="Expense"/> this became, once posted.</summary>
    public string? ExpenseId { get; set; }

    public string? PostedByUserId { get; set; }
    public string? SkippedByUserId { get; set; }

    /// <summary>Free text on a skip - "flat was empty in August", "landlord waived it".</summary>
    public string? SkipReason { get; set; }

    /// <summary>Denormalized from the template so the board renders without loading every schedule,
    /// and so a renamed template does not retitle bills that were already announced under the old
    /// name.</summary>
    public string Description { get; set; } = null!;

    /// <summary>When the house was told this bill is coming, or null if it has not been.</summary>
    public DateTimeOffset? RemindedAt { get; set; }

    public static BillOccurrence Create(RecurringExpense template, DateTimeOffset dueAt)
    {
        var date = DateTimeOffset.UtcNow;
        return new BillOccurrence
        {
            Id = GenerateId(),
            CreatedAt = date,
            UpdatedAt = date,
            RecurringExpenseId = template.Id,
            GuildId = template.GuildId,
            ChannelId = template.ChannelId,
            DueAt = dueAt,
            AmountMinor = template.AmountMinor,
            Status = BillStatus.Pending,
            Description = template.Description,
        };
    }

    /// <summary>Moves the due date and releases the announcement stamp.</summary>
    public void Reschedule(DateTimeOffset dueAt)
    {
        if (dueAt == DueAt) return;

        DueAt = dueAt;
        RemindedAt = null;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>True while nobody has said what this period actually cost.</summary>
    public bool NeedsAmount() => Status == BillStatus.Pending && AmountMinor is null;
}

// ── Integrator: paste into MicroserviceContext.OnModelCreating ───────────────
// modelBuilder.Entity<RecurringExpense>(templateBuilder =>
// {
//     templateBuilder.HasOne<Domain.Aggregates.Channel>()
//         .WithMany()
//         .HasForeignKey(x => x.ChannelId)
//         .OnDelete(DeleteBehavior.Cascade);
//
//     templateBuilder.HasIndex(x => x.ChannelId);
//
//     // The generation sweep's query: unpaused templates whose next slot is inside the widest
//     // lead window. Filtered, because a paused template is not a candidate and a house that
//     // pauses one for the summer should not cost the index anything.
//     templateBuilder.HasIndex(x => x.NextDueAt).HasFilter("is_paused = false");
// });
//
// modelBuilder.Entity<RecurringExpenseShare>(shareBuilder =>
// {
//     shareBuilder.HasKey(x => new { x.RecurringExpenseId, x.UserId });
//
//     shareBuilder.HasOne(x => x.RecurringExpense)
//         .WithMany(x => x.Shares)
//         .HasForeignKey(x => x.RecurringExpenseId)
//         .OnDelete(DeleteBehavior.Cascade);
// });
//
// modelBuilder.Entity<BillOccurrence>(occurrenceBuilder =>
// {
//     occurrenceBuilder.HasOne<RecurringExpense>()
//         .WithMany()
//         .HasForeignKey(x => x.RecurringExpenseId)
//         .OnDelete(DeleteBehavior.Cascade);
//
//     // The board: this channel's bills, soonest first, optionally filtered by status.
//     occurrenceBuilder.HasIndex(x => new { x.ChannelId, x.DueAt });
//
//     // One occurrence per template per due date - the guard that makes generation idempotent
//     // when the sweep and an endpoint-driven generation fire for the same slot.
//     occurrenceBuilder.HasIndex(x => new { x.RecurringExpenseId, x.DueAt }).IsUnique();
//
//     // The alert sweep's query: unannounced bills. Filtered so the index stays proportional to
//     // what is outstanding rather than to all bill history, which is append-only and unbounded.
//     // Deliberately not also filtered on status: BillStatus maps to a Postgres enum, so the
//     // predicate would have to name a label ('pending') whose spelling is Npgsql's translator's
//     // business rather than ours, and a partial index that is merely broader still serves the
//     // sweep's query.
//     occurrenceBuilder.HasIndex(x => x.DueAt).HasFilter("reminded_at IS NULL");
// });
//
// DbSet: public DbSet<RecurringExpense> RecurringExpenses { get; set; }
// DbSet: public DbSet<RecurringExpenseShare> RecurringExpenseShares { get; set; }
// DbSet: public DbSet<BillOccurrence> BillOccurrences { get; set; }
// MapEnum: options.MapEnum<RecurrenceUnit>();
// MapEnum: options.MapEnum<BillStatus>();
// MapEnum: options.MapEnum<ExpenseCategory>();
