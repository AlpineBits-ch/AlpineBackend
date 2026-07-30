using Guild.Domain.Entity;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Services;

public record ChoreBalance(string UserId, int CompletedMinutes, int CompletedCount);

/// <summary>
/// Decides who gets the next chore, and reports how the workload is actually distributed.
/// </summary>
public class ChoreRotationService(MicroserviceContext ctx)
{
    public const int DefaultBalanceWindowDays = 30;

    /// <summary>Members eligible for a chore: the rotation role's holders, or the fixed assignee.
    /// Expired (guest) role memberships are excluded - a house sitter shouldn't inherit the bins.</summary>
    public async Task<List<string>> GetRotationPoolAsync(Chore chore)
    {
        if (chore.RotationRoleId is null)
            return chore.FixedAssigneeUserId is null ? [] : [chore.FixedAssigneeUserId];

        var now = DateTimeOffset.UtcNow;

        return await ctx.RoleMembers
            .AsNoTracking()
            .Where(rm => rm.RoleId == chore.RotationRoleId && (rm.ExpiresAt == null || rm.ExpiresAt > now))
            .Join(ctx.GuildMembers.AsNoTracking(), rm => rm.MemberId, m => m.Id, (rm, m) => m.UserId)
            .Distinct()
            .ToListAsync();
    }

    /// <summary>Completed weighted minutes per member over the window, for everyone in
    /// <paramref name="userIds"/> (members with nothing completed appear with zero rather than
    /// being missing, which is what makes them sort first).</summary>
    public async Task<List<ChoreBalance>> GetBalancesAsync(
        string guildId, IReadOnlyCollection<string> userIds, int windowDays = DefaultBalanceWindowDays)
    {
        if (userIds.Count == 0) return [];

        var since = DateTimeOffset.UtcNow.AddDays(-Math.Max(1, windowDays));

        var completed = await ctx.ChoreOccurrences
            .AsNoTracking()
            .Where(o => o.GuildId == guildId
                        && o.CompletedAt != null
                        && o.CompletedAt >= since
                        && userIds.Contains(o.AssignedUserId))
            .GroupBy(o => o.AssignedUserId)
            .Select(g => new { UserId = g.Key, Minutes = g.Sum(o => o.EffortMinutes), Count = g.Count() })
            .ToListAsync();

        var byUser = completed.ToDictionary(c => c.UserId);

        return userIds
            .Select(id => byUser.TryGetValue(id, out var c)
                ? new ChoreBalance(id, c.Minutes, c.Count)
                : new ChoreBalance(id, 0, 0))
            .ToList();
    }

    /// <summary>Who should take the next occurrence.</summary>
    public async Task<string?> PickNextAssigneeAsync(Chore chore, string? excludeUserId = null)
    {
        var pool = await GetRotationPoolAsync(chore);
        if (excludeUserId is not null && pool.Count > 1) pool.Remove(excludeUserId);
        if (pool.Count == 0) return null;
        if (pool.Count == 1) return pool[0];

        var balances = await GetBalancesAsync(chore.GuildId, pool);

        // Last time each candidate was assigned anything in this guild - the tiebreak that stops
        // two equally-idle members from being picked in id order forever.
        var lastAssigned = await ctx.ChoreOccurrences
            .AsNoTracking()
            .Where(o => o.GuildId == chore.GuildId && pool.Contains(o.AssignedUserId))
            .GroupBy(o => o.AssignedUserId)
            .Select(g => new { UserId = g.Key, Last = g.Max(o => o.DueAt) })
            .ToDictionaryAsync(x => x.UserId, x => x.Last);

        return balances
            .OrderBy(b => b.CompletedMinutes)
            .ThenBy(b => lastAssigned.TryGetValue(b.UserId, out var last) ? last : DateTimeOffset.MinValue)
            .ThenBy(b => b.UserId, StringComparer.Ordinal)
            .First()
            .UserId;
    }

    /// <summary>
    /// Creates the occurrence for a chore's current NextDueAt and advances the schedule.
    /// </summary>
    public async Task<ChoreOccurrence?> StageNextOccurrenceAsync(Chore chore)
    {
        if (chore.IsPaused) return null;

        var dueAt = chore.NextDueAt;

        var exists = await ctx.ChoreOccurrences.AnyAsync(o => o.ChoreId == chore.Id && o.DueAt == dueAt);
        if (exists)
        {
            chore.NextDueAt = chore.AdvanceFrom(dueAt);
            return null;
        }

        var assignee = await PickNextAssigneeAsync(chore);
        if (assignee is null) return null;   // empty rotation pool - nothing to assign yet

        var occurrence = ChoreOccurrence.Create(chore, dueAt, assignee);
        ctx.ChoreOccurrences.Add(occurrence);

        chore.NextDueAt = chore.AdvanceFrom(dueAt);

        return occurrence;
    }
}
