using System.Globalization;
using Guild.Application.Dtos.Response;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Services;

/// <summary>
/// What the house spent over a window, broken down by month, by category and by who fronted it.
/// </summary>
public class LedgerSummaryService(MicroserviceContext ctx)
{
    /// <summary>Three years, plus a day for the leap years in it.</summary>
    public const int MaxWindowDays = 1096;

    public const int DefaultWindowMonths = 6;

    public record CategoryTotalRow(ExpenseCategory Category, long TotalMinor, int Count);

    public record CategoryShareRow(ExpenseCategory Category, long ShareMinor);

    public record PeriodTotalRow(int Year, int Month, long TotalMinor, int Count);

    public record PeriodShareRow(int Year, int Month, long ShareMinor);

    public record PayerTotalRow(string UserId, long PaidMinor);

    /// <summary>The window that is actually going to be reported on, as opposed to the one that was
    /// asked for.</summary>
    public record ResolvedWindow(DateTimeOffset From, DateTimeOffset To, bool Clamped);

    /// <summary>Turns an open-ended request into a bounded one.</summary>
    public static ResolvedWindow ResolveWindow(DateTimeOffset? from, DateTimeOffset? to, DateTimeOffset now)
    {
        var end = to ?? now;
        var start = from ?? end.AddMonths(-DefaultWindowMonths);

        if ((end - start).TotalDays <= MaxWindowDays) return new ResolvedWindow(start, end, false);

        return new ResolvedWindow(end.AddDays(-MaxWindowDays), end, true);
    }

    /// <summary>"2026-07".</summary>
    public static string FormatPeriod(int year, int month) =>
        string.Create(CultureInfo.InvariantCulture, $"{year:0000}-{month:00}");

    // ══════════════════════════════════════════════════════════════════════════ Query builders

    public static IQueryable<CategoryTotalRow> BuildCategoryTotalsQuery(
        MicroserviceContext ctx, string channelId, DateTimeOffset from, DateTimeOffset to) =>
        ctx.Expenses.AsNoTracking()
            .Where(e => e.ChannelId == channelId && e.OccurredAt >= from && e.OccurredAt < to)
            .GroupBy(e => e.Category)
            .Select(g => new CategoryTotalRow(g.Key, g.Sum(e => e.AmountMinor), g.Count()));

    /// <summary>The caller's own share per category.</summary>
    public static IQueryable<CategoryShareRow> BuildCategorySharesQuery(
        MicroserviceContext ctx, string channelId, string userId, DateTimeOffset from, DateTimeOffset to) =>
        ctx.ExpenseShares.AsNoTracking()
            .Where(s => s.UserId == userId
                        && s.Expense.ChannelId == channelId
                        && s.Expense.OccurredAt >= from
                        && s.Expense.OccurredAt < to)
            .GroupBy(s => s.Expense.Category)
            .Select(g => new CategoryShareRow(g.Key, g.Sum(s => s.AmountMinor)));

    /// <summary>Bucketed on the UTC calendar rather than on the database session's time zone, so
    /// the month an expense falls in is a property of the data and not of a connection setting.
    /// Which month the boundary cases land in is arguable; which month they land in changing
    /// between two deployments is not.</summary>
    public static IQueryable<PeriodTotalRow> BuildPeriodTotalsQuery(
        MicroserviceContext ctx, string channelId, DateTimeOffset from, DateTimeOffset to) =>
        ctx.Expenses.AsNoTracking()
            .Where(e => e.ChannelId == channelId && e.OccurredAt >= from && e.OccurredAt < to)
            .GroupBy(e => new { e.OccurredAt.UtcDateTime.Year, e.OccurredAt.UtcDateTime.Month })
            .Select(g => new PeriodTotalRow(
                g.Key.Year, g.Key.Month, g.Sum(e => e.AmountMinor), g.Count()));

    public static IQueryable<PeriodShareRow> BuildPeriodSharesQuery(
        MicroserviceContext ctx, string channelId, string userId, DateTimeOffset from, DateTimeOffset to) =>
        ctx.ExpenseShares.AsNoTracking()
            .Where(s => s.UserId == userId
                        && s.Expense.ChannelId == channelId
                        && s.Expense.OccurredAt >= from
                        && s.Expense.OccurredAt < to)
            .GroupBy(s => new { s.Expense.OccurredAt.UtcDateTime.Year, s.Expense.OccurredAt.UtcDateTime.Month })
            .Select(g => new PeriodShareRow(g.Key.Year, g.Key.Month, g.Sum(s => s.AmountMinor)));

    public static IQueryable<PayerTotalRow> BuildPayerTotalsQuery(
        MicroserviceContext ctx, string channelId, DateTimeOffset from, DateTimeOffset to) =>
        ctx.Expenses.AsNoTracking()
            .Where(e => e.ChannelId == channelId && e.OccurredAt >= from && e.OccurredAt < to)
            .GroupBy(e => e.PayerUserId)
            .Select(g => new PayerTotalRow(g.Key, g.Sum(e => e.AmountMinor)));

    /// <summary>The caller's total share over the window.</summary>
    public static IQueryable<long> BuildMyShareQuery(
        MicroserviceContext ctx, string channelId, string userId, DateTimeOffset from, DateTimeOffset to) =>
        ctx.ExpenseShares.AsNoTracking()
            .Where(s => s.UserId == userId
                        && s.Expense.ChannelId == channelId
                        && s.Expense.OccurredAt >= from
                        && s.Expense.OccurredAt < to)
            .Select(s => s.AmountMinor);

    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>The rollup.</summary>
    public async Task<LedgerSummaryDto> SummarizeAsync(
        string channelId, string userId, string currency, ResolvedWindow window,
        bool includeCategories, bool includePeriods)
    {
        var payers = await BuildPayerTotalsQuery(ctx, channelId, window.From, window.To).ToListAsync();

        // Totalled off the payer rows rather than with a sixth aggregate: every expense has exactly
        // one payer, so those rows already partition the window.
        var summary = new LedgerSummaryDto
        {
            ChannelId = channelId,
            Currency = currency,
            From = window.From,
            To = window.To,
            TotalMinor = payers.Sum(p => p.PaidMinor),
            MyShareMinor = await BuildMyShareQuery(ctx, channelId, userId, window.From, window.To)
                .SumAsync(),
            Clamped = window.Clamped,

            ByPayer = payers
                .OrderByDescending(p => p.PaidMinor)
                .ThenBy(p => p.UserId, StringComparer.Ordinal)
                .Select(p => new LedgerPayerTotalDto { UserId = p.UserId, PaidMinor = p.PaidMinor })
                .ToList(),
        };

        if (includeCategories)
        {
            var totals = await BuildCategoryTotalsQuery(ctx, channelId, window.From, window.To).ToListAsync();
            var shares = (await BuildCategorySharesQuery(ctx, channelId, userId, window.From, window.To)
                    .ToListAsync())
                .ToDictionary(s => s.Category, s => s.ShareMinor);

            // Biggest bucket first: the first line of this list is the answer to the question that
            // was asked. Ties break on the category so two renders of an unchanged ledger agree.
            summary.ByCategory = totals
                .OrderByDescending(c => c.TotalMinor)
                .ThenBy(c => c.Category)
                .Select(c => new LedgerCategoryTotalDto
                {
                    Category = c.Category,
                    TotalMinor = c.TotalMinor,
                    MyShareMinor = shares.GetValueOrDefault(c.Category),
                    Count = c.Count,
                })
                .ToList();
        }

        if (includePeriods)
        {
            var totals = await BuildPeriodTotalsQuery(ctx, channelId, window.From, window.To).ToListAsync();
            var shares = (await BuildPeriodSharesQuery(ctx, channelId, userId, window.From, window.To)
                    .ToListAsync())
                .ToDictionary(s => (s.Year, s.Month), s => s.ShareMinor);

            // Chronological, and months the house spent nothing in are simply absent rather than
            // zero-filled.
            summary.ByPeriod = totals
                .OrderBy(p => p.Year).ThenBy(p => p.Month)
                .Select(p => new LedgerPeriodTotalDto
                {
                    Period = FormatPeriod(p.Year, p.Month),
                    TotalMinor = p.TotalMinor,
                    MyShareMinor = shares.GetValueOrDefault((p.Year, p.Month)),
                    Count = p.Count,
                })
                .ToList();
        }

        return summary;
    }
}
