using Guild.Domain.Entity;
using Guild.Domain.Services;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Services;

/// <summary>Balance computation for a ledger channel.</summary>
public class LedgerService(MicroserviceContext ctx)
{
    /// <summary>Net position per member, aggregated in the database.</summary>
    public async Task<List<BalanceEntry>> GetBalancesAsync(string channelId)
    {
        // What each member fronted...
        var paid = await ctx.Expenses.AsNoTracking()
            .Where(e => e.ChannelId == channelId)
            .GroupBy(e => e.PayerUserId)
            .Select(g => new { UserId = g.Key, Total = g.Sum(e => e.AmountMinor) })
            .ToListAsync();

        // ...and what each member owes of it.
        var owed = await ctx.ExpenseShares.AsNoTracking()
            .Where(s => s.Expense.ChannelId == channelId)
            .GroupBy(s => s.UserId)
            .Select(g => new { UserId = g.Key, Total = g.Sum(s => s.AmountMinor) })
            .ToListAsync();

        var settlementsOut = await ctx.Settlements.AsNoTracking()
            .Where(s => s.ChannelId == channelId)
            .GroupBy(s => s.FromUserId)
            .Select(g => new { UserId = g.Key, Total = g.Sum(s => s.AmountMinor) })
            .ToListAsync();

        var settlementsIn = await ctx.Settlements.AsNoTracking()
            .Where(s => s.ChannelId == channelId)
            .GroupBy(s => s.ToUserId)
            .Select(g => new { UserId = g.Key, Total = g.Sum(s => s.AmountMinor) })
            .ToListAsync();

        var net = new Dictionary<string, long>(StringComparer.Ordinal);

        void Add(string userId, long delta) =>
            net[userId] = net.TryGetValue(userId, out var current) ? current + delta : delta;

        foreach (var row in paid) Add(row.UserId, row.Total);
        foreach (var row in owed) Add(row.UserId, -row.Total);

        // Paying someone reduces what you owe and what they're owed.
        foreach (var row in settlementsOut) Add(row.UserId, row.Total);
        foreach (var row in settlementsIn) Add(row.UserId, -row.Total);

        return net
            .Where(kv => kv.Value != 0)
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new BalanceEntry(kv.Key, kv.Value))
            .ToList();
    }

    public async Task<string> GetCurrencyAsync(string channelId)
    {
        var config = await ctx.LedgerConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.ChannelId == channelId);
        return config?.Currency ?? "CHF";
    }

    /// <summary>Replaces an expense's shares from a client payload.</summary>
    public async Task<string?> ApplySharesAsync(
        Expense expense,
        Domain.Enums.ExpenseSplitKind splitKind,
        IReadOnlyList<SplitParticipant> requested,
        string guildId)
    {
        var memberSet = await GetMemberUserIdsAsync(guildId);

        var participants = requested;

        // An Equal split with no explicit participants means "everyone in the house" - by far the
        // most common case (rent, internet, a shared shop) and not worth making the client
        // enumerate every time.
        if (participants.Count == 0)
        {
            if (memberSet.Count == 0) return "This guild has no members to split across";
            participants = memberSet.Order(StringComparer.Ordinal)
                .Select(id => new SplitParticipant(id, 1))
                .ToList();
        }

        if (participants.Select(p => p.UserId).Distinct(StringComparer.Ordinal).Count() != participants.Count)
            return "A participant may only appear once";

        if (participants.Any(p => !memberSet.Contains(p.UserId)))
            return "Every participant must be a member of this guild";

        IReadOnlyList<SplitResult> split;
        try
        {
            split = ExpenseSplitter.Split(expense.AmountMinor, splitKind, participants);
        }
        catch (ArgumentException e)
        {
            return e.Message;
        }

        var byUser = participants.ToDictionary(p => p.UserId, p => p.ShareValue, StringComparer.Ordinal);

        expense.Shares.Clear();
        foreach (var result in split)
        {
            expense.Shares.Add(new ExpenseShare
            {
                ExpenseId = expense.Id,
                UserId = result.UserId,
                ShareValue = byUser[result.UserId],
                AmountMinor = result.AmountMinor,
            });
        }

        return null;
    }

    /// <summary>Whether every id names a current member of the guild.</summary>
    public async Task<bool> AreMembersAsync(string guildId, params string[] userIds)
    {
        var distinct = userIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count == 0) return true;

        var memberSet = await GetMemberUserIdsAsync(guildId);
        return distinct.All(memberSet.Contains);
    }

    private async Task<HashSet<string>> GetMemberUserIdsAsync(string guildId)
    {
        var ids = await ctx.GuildMembers.AsNoTracking()
            .Where(m => m.GuildId == guildId)
            .Select(m => m.UserId)
            .ToListAsync();

        return ids.ToHashSet(StringComparer.Ordinal);
    }
}
