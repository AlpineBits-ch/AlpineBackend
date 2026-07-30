using Guild.Domain.Entity;
using Guild.Domain.Services;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Services;

/// <summary>Balance computation for a ledger channel.</summary>
public class LedgerService(MicroserviceContext ctx)
{
    public async Task<List<BalanceEntry>> GetBalancesAsync(string channelId)
    {
        var expenses = await ctx.Expenses.AsNoTracking()
            .Include(e => e.Shares)
            .Where(e => e.ChannelId == channelId)
            .ToListAsync();

        var settlements = await ctx.Settlements.AsNoTracking()
            .Where(s => s.ChannelId == channelId)
            .ToListAsync();

        var net = new Dictionary<string, long>(StringComparer.Ordinal);

        void Add(string userId, long delta) =>
            net[userId] = net.TryGetValue(userId, out var current) ? current + delta : delta;

        foreach (var expense in expenses)
        {
            // The payer fronted the whole amount...
            Add(expense.PayerUserId, expense.AmountMinor);
            // ...and each participant owes their slice of it, the payer included.
            foreach (var share in expense.Shares) Add(share.UserId, -share.AmountMinor);
        }

        foreach (var settlement in settlements)
        {
            // Paying someone reduces what you owe and what they're owed.
            Add(settlement.FromUserId, settlement.AmountMinor);
            Add(settlement.ToUserId, -settlement.AmountMinor);
        }

        return net
            .Where(kv => kv.Value != 0)
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new BalanceEntry(kv.Key, kv.Value))
            .ToList();
    }

    public async Task<string> GetCurrencyAsync(string channelId, string guildId)
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
        var participants = requested;

        // An Equal split with no explicit participants means "everyone in the house" - by far the
        // most common case (rent, internet, a shared shop) and not worth making the client
        // enumerate every time.
        if (participants.Count == 0)
        {
            var memberIds = await ctx.GuildMembers.AsNoTracking()
                .Where(m => m.GuildId == guildId)
                .Select(m => m.UserId)
                .ToListAsync();

            if (memberIds.Count == 0) return "This guild has no members to split across";
            participants = memberIds.Select(id => new SplitParticipant(id, 1)).ToList();
        }

        if (participants.Select(p => p.UserId).Distinct(StringComparer.Ordinal).Count() != participants.Count)
            return "A participant may only appear once";

        var memberSet = await ctx.GuildMembers.AsNoTracking()
            .Where(m => m.GuildId == guildId)
            .Select(m => m.UserId)
            .ToListAsync();

        if (participants.Any(p => !memberSet.Contains(p.UserId, StringComparer.Ordinal)))
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
}
