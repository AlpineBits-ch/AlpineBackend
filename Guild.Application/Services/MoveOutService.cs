using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Domain.Services;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Services;

/// <summary>What a departing member leaves behind, and what happened to it.</summary>
public record MoveOutSummary
{
    public required string UserId { get; init; }

    /// <summary>Unfinished occurrences handed to somebody else.</summary>
    public int ChoresReassigned { get; init; }

    /// <summary>Unfinished occurrences deleted because nobody was left to take them.</summary>
    public int ChoresDropped { get; init; }

    /// <summary>Chores that named them as the fixed assignee and are now paused.</summary>
    public int ChoresPaused { get; init; }

    public int ListItemsUnassigned { get; init; }

    /// <summary>Settlements written to zero their ledger balance.</summary>
    public List<TransferSuggestion> BalancesWrittenOff { get; init; } = [];
}

/// <summary>The balances that stop a move-out, one entry per ledger channel they are not square in.</summary>
public record OutstandingBalance(string ChannelId, string Currency, long NetMinor);

/// <summary>Removing someone who has moved out.</summary>
public class MoveOutService(MicroserviceContext ctx, ChoreRotationService rotation, LedgerService ledger)
{
    /// <summary>Ledger channels where the member is not at zero.</summary>
    public async Task<List<OutstandingBalance>> GetOutstandingBalancesAsync(string guildId, string userId)
    {
        var ledgerChannelIds = await ctx.Channels.AsNoTracking()
            .Where(c => c.GuildId == guildId && c.Type == ChannelType.Ledger)
            .Select(c => c.Id)
            .ToListAsync();

        var outstanding = new List<OutstandingBalance>();

        foreach (var channelId in ledgerChannelIds)
        {
            var balances = await ledger.GetBalancesAsync(channelId);
            var theirs = balances.FirstOrDefault(b => b.UserId == userId);
            if (theirs is null || theirs.NetMinor == 0) continue;

            outstanding.Add(new OutstandingBalance(
                channelId, await ledger.GetCurrencyAsync(channelId), theirs.NetMinor));
        }

        return outstanding;
    }

    /// <summary>Stages every household consequence of <paramref name="userId"/> leaving.</summary>
    public async Task<MoveOutSummary> StageAsync(
        string guildId, string userId, string actorUserId, bool writeOffBalances)
    {
        var writtenOff = writeOffBalances
            ? await StageBalanceWriteOffAsync(guildId, userId, actorUserId)
            : [];

        var (reassigned, dropped) = await StageChoreHandoverAsync(guildId, userId);
        var paused = await StagePauseFixedChoresAsync(guildId, userId);
        var unassigned = await StageUnassignListItemsAsync(guildId, userId);

        return new MoveOutSummary
        {
            UserId = userId,
            ChoresReassigned = reassigned,
            ChoresDropped = dropped,
            ChoresPaused = paused,
            ListItemsUnassigned = unassigned,
            BalancesWrittenOff = writtenOff,
        };
    }

    /// <summary>Records the settlements that bring the leaver to zero.</summary>
    private async Task<List<TransferSuggestion>> StageBalanceWriteOffAsync(
        string guildId, string userId, string actorUserId)
    {
        var ledgerChannelIds = await ctx.Channels.AsNoTracking()
            .Where(c => c.GuildId == guildId && c.Type == ChannelType.Ledger)
            .Select(c => c.Id)
            .ToListAsync();

        var recorded = new List<TransferSuggestion>();
        var now = DateTimeOffset.UtcNow;

        foreach (var channelId in ledgerChannelIds)
        {
            var balances = await ledger.GetBalancesAsync(channelId);
            if (balances.All(b => b.UserId != userId)) continue;

            // Only the transfers this person is a party to.
            var theirTransfers = DebtSimplifier.Simplify(balances)
                .Where(t => t.FromUserId == userId || t.ToUserId == userId)
                .ToList();

            foreach (var transfer in theirTransfers)
            {
                ctx.Settlements.Add(new Settlement
                {
                    Id = Settlement.GenerateId(),
                    CreatedAt = now,
                    UpdatedAt = now,
                    ChannelId = channelId,
                    GuildId = guildId,
                    FromUserId = transfer.FromUserId,
                    ToUserId = transfer.ToUserId,
                    AmountMinor = transfer.AmountMinor,
                    SettledAt = now,
                    RecordedByUserId = actorUserId,
                });
            }

            recorded.AddRange(theirTransfers);
        }

        return recorded;
    }

    /// <summary>
    /// Hands their unfinished occurrences to whoever is next lightest, or deletes them when the
    /// rota has nobody left.
    /// </summary>
    private async Task<(int Reassigned, int Dropped)> StageChoreHandoverAsync(string guildId, string userId)
    {
        var open = await ctx.ChoreOccurrences
            .Where(o => o.GuildId == guildId
                        && o.AssignedUserId == userId
                        && o.CompletedAt == null
                        && o.SkippedAt == null)
            .ToListAsync();

        if (open.Count == 0) return (0, 0);

        var choreIds = open.Select(o => o.ChoreId).Distinct().ToList();
        var chores = await ctx.Chores
            .Where(c => choreIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id);

        int reassigned = 0, dropped = 0;

        foreach (var occurrence in open)
        {
            if (!chores.TryGetValue(occurrence.ChoreId, out var chore))
            {
                ctx.ChoreOccurrences.Remove(occurrence);
                dropped++;
                continue;
            }

            var replacement = await rotation.PickNextAssigneeAsync(chore, excludeUserId: userId);

            // PickNextAssigneeAsync returns the excluded member when they are the only one in the
            // pool, which after a move-out means the pool is empty in every sense that matters.
            if (replacement is null || replacement == userId)
            {
                ctx.ChoreOccurrences.Remove(occurrence);
                dropped++;
                continue;
            }

            occurrence.AssignedUserId = replacement;
            // Cleared so the new assignee is actually told, rather than inheriting a chore the
            // person who left had already been reminded about.
            occurrence.RemindedAt = null;
            reassigned++;
        }

        return (reassigned, dropped);
    }

    /// <summary>Pauses chores that named them personally.</summary>
    private async Task<int> StagePauseFixedChoresAsync(string guildId, string userId)
    {
        var fixedChores = await ctx.Chores
            .Where(c => c.GuildId == guildId && c.FixedAssigneeUserId == userId && !c.IsPaused)
            .ToListAsync();

        foreach (var chore in fixedChores) chore.IsPaused = true;

        return fixedChores.Count;
    }

    private async Task<int> StageUnassignListItemsAsync(string guildId, string userId)
    {
        var items = await ctx.ListItems
            .Where(i => i.GuildId == guildId && i.AssigneeUserId == userId && !i.IsChecked)
            .ToListAsync();

        foreach (var item in items) item.AssigneeUserId = null;

        return items.Count;
    }
}
