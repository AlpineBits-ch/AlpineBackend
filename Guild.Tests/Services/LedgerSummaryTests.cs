using Guild.Application.Services;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Microsoft.EntityFrameworkCore;

namespace Guild.Tests.Services;

/// <summary>The spending rollup.</summary>
[TestFixture]
public class LedgerSummaryTests
{
    private const string GuildId = "gild-1";
    private const string ChannelId = "chan-ledger";

    private TestGuildContext _context = null!;
    private LedgerSummaryService _summaries = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _summaries = new LedgerSummaryService(_context);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    // ── Seeding ──────────────────────────────────────────────────────────────

    private async Task AddExpenseAsync(string payerUserId, long amountMinor, DateTimeOffset occurredAt,
        ExpenseCategory category, params (string UserId, long AmountMinor)[] shares)
    {
        var expense = Expense.Create(new CreateExpenseParams
        {
            ChannelId = ChannelId, GuildId = GuildId, PayerUserId = payerUserId,
            Description = "Shop", AmountMinor = amountMinor, OccurredAt = occurredAt,
            SplitKind = ExpenseSplitKind.Exact, CreatedByUserId = payerUserId, Category = category,
        });

        foreach (var (userId, share) in shares)
            expense.Shares.Add(new ExpenseShare { ExpenseId = expense.Id, UserId = userId, AmountMinor = share });

        _context.Expenses.Add(expense);
        await _context.SaveChangesAsync();
    }

    private static LedgerSummaryService.ResolvedWindow Window(string from, string to) =>
        new(DateTimeOffset.Parse(from), DateTimeOffset.Parse(to), false);

    private Task<Guild.Application.Dtos.Response.LedgerSummaryDto> SummarizeAsync(
        string userId, LedgerSummaryService.ResolvedWindow window) =>
        _summaries.SummarizeAsync(ChannelId, userId, "CHF", window, includeCategories: true, includePeriods: true);

    // ══════════════════════════════════════════════════════════════════════════ The window
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void ResolveWindow_WithNothingAskedForCoversTheLastSixMonths()
    {
        var now = DateTimeOffset.Parse("2026-08-07T10:00:00Z");

        var window = LedgerSummaryService.ResolveWindow(null, null, now);

        Assert.Multiple(() =>
        {
            Assert.That(window.To, Is.EqualTo(now));
            Assert.That(window.From, Is.EqualTo(now.AddMonths(-6)));
            Assert.That(window.Clamped, Is.False);
        });
    }

    /// <summary>The edge: a request just inside the cap is left exactly as asked.</summary>
    [Test]
    public void ResolveWindow_JustInsideTheCapIsUntouched()
    {
        var to = DateTimeOffset.Parse("2026-08-07T10:00:00Z");
        var from = to.AddDays(-LedgerSummaryService.MaxWindowDays);

        var window = LedgerSummaryService.ResolveWindow(from, to, to);

        Assert.Multiple(() =>
        {
            Assert.That(window.From, Is.EqualTo(from));
            Assert.That(window.Clamped, Is.False);
        });
    }

    /// <summary>Past the cap the start moves forward and the response says so.</summary>
    [Test]
    public void ResolveWindow_BeyondTheCapIsShortenedAndReported()
    {
        var to = DateTimeOffset.Parse("2026-08-07T10:00:00Z");

        var window = LedgerSummaryService.ResolveWindow(to.AddYears(-10), to, to);

        Assert.Multiple(() =>
        {
            Assert.That(window.Clamped, Is.True);
            Assert.That(window.To, Is.EqualTo(to), "the recent end is the half anybody was going to read");
            Assert.That(window.From, Is.EqualTo(to.AddDays(-LedgerSummaryService.MaxWindowDays)));
        });
    }

    [Test]
    public void FormatPeriod_IsZeroPaddedSoItSortsChronologically()
    {
        var periods = new[]
        {
            LedgerSummaryService.FormatPeriod(2026, 12),
            LedgerSummaryService.FormatPeriod(2026, 2),
            LedgerSummaryService.FormatPeriod(2025, 11),
        };

        Assert.Multiple(() =>
        {
            Assert.That(LedgerSummaryService.FormatPeriod(2026, 7), Is.EqualTo("2026-07"));
            Assert.That(periods.Order(StringComparer.Ordinal),
                Is.EqualTo(new[] { "2025-11", "2026-02", "2026-12" }));
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ Bucketing
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>The normal case, and the one the endpoint exists for: a month boundary falls where
    /// the UTC calendar puts it, including for an expense entered just after local midnight. Which
    /// month those land in is arguable; which month they land in changing with a database session
    /// setting is not, which is why the bucketing does not use one.</summary>
    [Test]
    public async Task Summary_BucketsByMonthAcrossABoundary()
    {
        await AddExpenseAsync("anna", 1000, DateTimeOffset.Parse("2026-07-31T12:00:00Z"),
            ExpenseCategory.Groceries, ("anna", 500), ("ben", 500));

        // 00:30 on the first of August in Zurich is still July in UTC.
        await AddExpenseAsync("ben", 400, DateTimeOffset.Parse("2026-08-01T00:30:00+02:00"),
            ExpenseCategory.Groceries, ("anna", 200), ("ben", 200));

        await AddExpenseAsync("anna", 2000, DateTimeOffset.Parse("2026-08-05T12:00:00Z"),
            ExpenseCategory.Rent, ("anna", 1000), ("ben", 1000));

        var summary = await SummarizeAsync("anna", Window("2026-07-01T00:00:00Z", "2026-09-01T00:00:00Z"));

        Assert.Multiple(() =>
        {
            Assert.That(summary.ByPeriod.Select(p => p.Period), Is.EqualTo(new[] { "2026-07", "2026-08" }));
            Assert.That(summary.ByPeriod[0].TotalMinor, Is.EqualTo(1400));
            Assert.That(summary.ByPeriod[0].Count, Is.EqualTo(2));
            Assert.That(summary.ByPeriod[0].MyShareMinor, Is.EqualTo(700));
            Assert.That(summary.ByPeriod[1].TotalMinor, Is.EqualTo(2000));
            Assert.That(summary.ByPeriod[1].MyShareMinor, Is.EqualTo(1000));
        });
    }

    /// <summary>Uncategorized gets its own line rather than being folded into Other.</summary>
    [Test]
    public async Task Summary_ReportsUncategorizedAsItsOwnBucket()
    {
        await AddExpenseAsync("anna", 3000, DateTimeOffset.Parse("2026-07-05T12:00:00Z"),
            ExpenseCategory.Uncategorized, ("anna", 1500), ("ben", 1500));

        await AddExpenseAsync("anna", 1000, DateTimeOffset.Parse("2026-07-06T12:00:00Z"),
            ExpenseCategory.Other, ("anna", 500), ("ben", 500));

        var summary = await SummarizeAsync("anna", Window("2026-07-01T00:00:00Z", "2026-08-01T00:00:00Z"));

        Assert.Multiple(() =>
        {
            // Biggest first, so the first line answers the question that was asked.
            Assert.That(summary.ByCategory.Select(c => c.Category),
                Is.EqualTo(new[] { ExpenseCategory.Uncategorized, ExpenseCategory.Other }));
            Assert.That(summary.ByCategory[0].TotalMinor, Is.EqualTo(3000));
            Assert.That(summary.ByCategory[0].MyShareMinor, Is.EqualTo(1500));
        });
    }

    /// <summary>The invariant.</summary>
    [Test]
    public async Task Summary_BucketsSumBackToTheTotal()
    {
        await AddExpenseAsync("anna", 1234, DateTimeOffset.Parse("2026-06-10T12:00:00Z"),
            ExpenseCategory.Groceries, ("anna", 617), ("ben", 617));
        await AddExpenseAsync("ben", 999, DateTimeOffset.Parse("2026-07-10T12:00:00Z"),
            ExpenseCategory.Utilities, ("anna", 500), ("ben", 499));
        await AddExpenseAsync("ben", 1, DateTimeOffset.Parse("2026-07-11T12:00:00Z"),
            ExpenseCategory.Uncategorized, ("anna", 1));

        var summary = await SummarizeAsync("anna", Window("2026-06-01T00:00:00Z", "2026-08-01T00:00:00Z"));

        Assert.Multiple(() =>
        {
            Assert.That(summary.TotalMinor, Is.EqualTo(2234));
            Assert.That(summary.ByCategory.Sum(c => c.TotalMinor), Is.EqualTo(summary.TotalMinor));
            Assert.That(summary.ByPeriod.Sum(p => p.TotalMinor), Is.EqualTo(summary.TotalMinor));
            Assert.That(summary.ByPayer.Sum(p => p.PaidMinor), Is.EqualTo(summary.TotalMinor));

            Assert.That(summary.MyShareMinor, Is.EqualTo(617 + 500 + 1));
            Assert.That(summary.ByCategory.Sum(c => c.MyShareMinor), Is.EqualTo(summary.MyShareMinor));
            Assert.That(summary.ByPeriod.Sum(p => p.MyShareMinor), Is.EqualTo(summary.MyShareMinor));
        });
    }

    /// <summary>My share is what I owe of the window, not what I happened to front.</summary>
    [Test]
    public async Task Summary_MyShareIsWhatIOweNotWhatIPaid()
    {
        await AddExpenseAsync("anna", 10_000, DateTimeOffset.Parse("2026-07-10T12:00:00Z"),
            ExpenseCategory.Groceries, ("anna", 2500), ("ben", 2500), ("cara", 2500), ("dan", 2500));

        var summary = await SummarizeAsync("anna", Window("2026-07-01T00:00:00Z", "2026-08-01T00:00:00Z"));

        Assert.Multiple(() =>
        {
            Assert.That(summary.TotalMinor, Is.EqualTo(10_000));
            Assert.That(summary.MyShareMinor, Is.EqualTo(2500));
            Assert.That(summary.ByPayer.Single().PaidMinor, Is.EqualTo(10_000));
        });
    }

    /// <summary>The edge: an expense on the closing instant is outside the window.</summary>
    [Test]
    public async Task Summary_ExcludesExpensesOnTheClosingBoundary()
    {
        await AddExpenseAsync("anna", 500, DateTimeOffset.Parse("2026-07-31T23:59:59Z"),
            ExpenseCategory.Groceries, ("anna", 500));
        await AddExpenseAsync("anna", 700, DateTimeOffset.Parse("2026-08-01T00:00:00Z"),
            ExpenseCategory.Groceries, ("anna", 700));

        var summary = await SummarizeAsync("anna", Window("2026-07-01T00:00:00Z", "2026-08-01T00:00:00Z"));

        Assert.That(summary.TotalMinor, Is.EqualTo(500));
    }

    /// <summary>The negative case: an empty ledger answers with zeroes and empty lists, not with
    /// six months of zero-filled rows that a chart would render as data.</summary>
    [Test]
    public async Task Summary_OfAnEmptyLedgerIsZeroAndEmpty()
    {
        var summary = await SummarizeAsync("anna", Window("2026-01-01T00:00:00Z", "2026-08-01T00:00:00Z"));

        Assert.Multiple(() =>
        {
            Assert.That(summary.TotalMinor, Is.EqualTo(0));
            Assert.That(summary.MyShareMinor, Is.EqualTo(0));
            Assert.That(summary.ByCategory, Is.Empty);
            Assert.That(summary.ByPeriod, Is.Empty);
            Assert.That(summary.ByPayer, Is.Empty);
            Assert.That(summary.Currency, Is.EqualTo("CHF"));
        });
    }

    /// <summary>A summary of somebody else's spending: a member with no shares in the window owes
    /// nothing, and the house total is unaffected by who is asking.</summary>
    [Test]
    public async Task Summary_ForAMemberWithNoSharesReportsZeroForThemOnly()
    {
        await AddExpenseAsync("anna", 1000, DateTimeOffset.Parse("2026-07-10T12:00:00Z"),
            ExpenseCategory.Groceries, ("anna", 1000));

        var summary = await SummarizeAsync("cara", Window("2026-07-01T00:00:00Z", "2026-08-01T00:00:00Z"));

        Assert.Multiple(() =>
        {
            Assert.That(summary.TotalMinor, Is.EqualTo(1000));
            Assert.That(summary.MyShareMinor, Is.EqualTo(0));
            Assert.That(summary.ByCategory.Single().MyShareMinor, Is.EqualTo(0));
        });
    }

    /// <summary>groupBy skips work, not meaning: the totals are identical whichever breakdown was
    /// asked for. If they ever diverge, one of them is being derived from the breakdown rather than
    /// from the rows.</summary>
    [Test]
    public async Task Summary_GroupByOneBreakdownLeavesTheTotalsUnchanged()
    {
        await AddExpenseAsync("anna", 1500, DateTimeOffset.Parse("2026-07-10T12:00:00Z"),
            ExpenseCategory.Rent, ("anna", 750), ("ben", 750));

        var window = Window("2026-07-01T00:00:00Z", "2026-08-01T00:00:00Z");

        var monthsOnly = await _summaries.SummarizeAsync(
            ChannelId, "anna", "CHF", window, includeCategories: false, includePeriods: true);
        var categoriesOnly = await _summaries.SummarizeAsync(
            ChannelId, "anna", "CHF", window, includeCategories: true, includePeriods: false);

        Assert.Multiple(() =>
        {
            Assert.That(monthsOnly.ByCategory, Is.Empty);
            Assert.That(monthsOnly.ByPeriod, Is.Not.Empty);
            Assert.That(categoriesOnly.ByPeriod, Is.Empty);
            Assert.That(categoriesOnly.ByCategory, Is.Not.Empty);

            Assert.That(monthsOnly.TotalMinor, Is.EqualTo(1500));
            Assert.That(categoriesOnly.TotalMinor, Is.EqualTo(1500));
            Assert.That(monthsOnly.MyShareMinor, Is.EqualTo(750));
            Assert.That(categoriesOnly.MyShareMinor, Is.EqualTo(750));
        });
    }

    /// <summary>The category filter the expense list uses, exercised at the source.</summary>
    [Test]
    public async Task CategoryFilter_NarrowsToOneBucket()
    {
        await AddExpenseAsync("anna", 1000, DateTimeOffset.Parse("2026-07-10T12:00:00Z"),
            ExpenseCategory.Groceries, ("anna", 1000));
        await AddExpenseAsync("anna", 2000, DateTimeOffset.Parse("2026-07-11T12:00:00Z"),
            ExpenseCategory.Rent, ("anna", 2000));

        var groceries = await _context.Expenses.AsNoTracking()
            .Where(e => e.ChannelId == ChannelId && e.Category == ExpenseCategory.Groceries)
            .ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(groceries, Has.Count.EqualTo(1));
            Assert.That(groceries[0].AmountMinor, Is.EqualTo(1000));
        });
    }
}

/// <summary>Asserts the rollup's queries compile to SQL against the real Npgsql provider.</summary>
[TestFixture]
public class LedgerSummaryQueryTranslationTests
{
    private PostgresGuildContext _context = null!;

    private static readonly DateTimeOffset From = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    private static readonly DateTimeOffset To = DateTimeOffset.Parse("2026-08-01T00:00:00Z");

    [SetUp]
    public void SetUp() => _context = new PostgresGuildContext();

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    [Test]
    public void BuildCategoryTotalsQuery_translates()
    {
        var sql = LedgerSummaryService.BuildCategoryTotalsQuery(_context, "chan-1", From, To).ToQueryString();

        Assert.That(sql, Does.Contain("SELECT"));
    }

    /// <summary>Groups over a navigation into the parent expense, which is a different translation
    /// path from grouping over a column of the table being scanned.</summary>
    [Test]
    public void BuildCategorySharesQuery_translates()
    {
        var sql = LedgerSummaryService
            .BuildCategorySharesQuery(_context, "chan-1", "user-1", From, To).ToQueryString();

        Assert.That(sql, Does.Contain("SELECT"));
    }

    /// <summary>The one most likely to stop translating: a composite key built from date parts of
    /// a timestamptz.</summary>
    [Test]
    public void BuildPeriodTotalsQuery_translates()
    {
        var sql = LedgerSummaryService.BuildPeriodTotalsQuery(_context, "chan-1", From, To).ToQueryString();

        Assert.That(sql, Does.Contain("SELECT"));
    }

    [Test]
    public void BuildPeriodSharesQuery_translates()
    {
        var sql = LedgerSummaryService
            .BuildPeriodSharesQuery(_context, "chan-1", "user-1", From, To).ToQueryString();

        Assert.That(sql, Does.Contain("SELECT"));
    }

    [Test]
    public void BuildPayerTotalsQuery_translates()
    {
        var sql = LedgerSummaryService.BuildPayerTotalsQuery(_context, "chan-1", From, To).ToQueryString();

        Assert.That(sql, Does.Contain("SELECT"));
    }

    [Test]
    public void BuildMyShareQuery_translates()
    {
        var sql = LedgerSummaryService
            .BuildMyShareQuery(_context, "chan-1", "user-1", From, To).ToQueryString();

        Assert.That(sql, Does.Contain("SELECT"));
    }

    /// <summary>The expense list's category filter, which ships alongside the rollup and is the
    /// only other place the new column is queried.</summary>
    [Test]
    public void CategoryFilteredExpensePage_translates()
    {
        var sql = _context.Expenses.AsNoTracking()
            .Where(e => e.ChannelId == "chan-1" && e.Category == ExpenseCategory.Groceries)
            .OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id)
            .Take(51)
            .ToQueryString();

        Assert.That(sql, Does.Contain("SELECT"));
    }
}
