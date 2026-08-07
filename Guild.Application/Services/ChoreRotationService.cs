using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Services;

public record ChoreBalance(string UserId, int CompletedMinutes, int CompletedCount);

/// <summary>
/// One member's row on the balance board, with the presence figure that explains it.
/// </summary>
public record ChoreWorkload(
    string UserId,
    int CompletedMinutes,
    int CompletedCount,
    int PresentDays,
    int ExpectedMinutes,
    int BalanceMinutes);

/// <summary>
/// Decides who gets the next chore, and reports how the workload is actually distributed.
/// </summary>
public class ChoreRotationService(MicroserviceContext ctx, GuildPermissionService? permissions = null)
{
    public const int DefaultBalanceWindowDays = 30;

    /// <summary>
    /// Members eligible for a chore: the rotation role's holders, or the fixed assignee.
    /// </summary>
    public async Task<List<string>> GetRotationPoolAsync(Chore chore, DateTimeOffset? presentAt = null)
    {
        var pool = await GetEligiblePoolAsync(chore);
        if (presentAt is null || pool.Count == 0) return pool;

        var absent = await AbsenceService.AbsentUserIdsAsync(
            ctx, permissions, chore.GuildId, presentAt.Value);

        if (absent.Count == 0) return pool;

        var present = pool.Where(id => !absent.Contains(id)).ToList();

        // Never hand back an empty pool.
        return present.Count > 0 ? present : pool;
    }

    private async Task<List<string>> GetEligiblePoolAsync(Chore chore)
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

    /// <summary>
    /// The balance board: what each member got through, what their share of it actually was, and
    /// the difference.
    /// </summary>
    public async Task<List<ChoreWorkload>> GetWeightedBalancesAsync(
        string guildId, IReadOnlyCollection<string> userIds, int windowDays = DefaultBalanceWindowDays)
    {
        if (userIds.Count == 0) return [];

        var window = Math.Max(1, windowDays);
        var balances = await GetBalancesAsync(guildId, userIds, window);

        var to = DateTimeOffset.UtcNow;
        var from = to.AddDays(-window);

        var absences = await AbsenceService.InWindowAsync(ctx, permissions, guildId, from, to);

        var presentDays = balances.ToDictionary(
            b => b.UserId,
            b => window - MemberAbsence.AbsentDaysWithin(
                absences.Where(a => a.UserId == b.UserId), from, to),
            StringComparer.Ordinal);

        var totalCompleted = balances.Sum(b => b.CompletedMinutes);
        var totalPresent = presentDays.Values.Sum();

        return balances
            .Select(b =>
            {
                var present = presentDays[b.UserId];

                // Everybody away for the entire window: fall back to equal shares rather than
                // dividing by zero.
                var expected = totalPresent <= 0
                    ? (double)totalCompleted / balances.Count
                    : (double)totalCompleted * present / totalPresent;

                return new ChoreWorkload(
                    b.UserId,
                    b.CompletedMinutes,
                    b.CompletedCount,
                    (int)Math.Round(present, MidpointRounding.AwayFromZero),
                    (int)expected,
                    b.CompletedMinutes - (int)expected);
            })
            .ToList();
    }

    /// <summary>
    /// Weighted minutes each member is currently holding but has not finished: assigned, not
    /// completed, not skipped, and not so old that it is never going to be done.
    /// </summary>
    public async Task<Dictionary<string, int>> GetOutstandingLoadAsync(
        string guildId, IReadOnlyCollection<string> userIds, int windowDays = DefaultBalanceWindowDays)
    {
        if (userIds.Count == 0) return [];

        // Same window as the balance.
        var since = DateTimeOffset.UtcNow.AddDays(-Math.Max(1, windowDays));

        var rows = await ctx.ChoreOccurrences
            .AsNoTracking()
            .Where(o => o.GuildId == guildId
                        && o.CompletedAt == null
                        && o.SkippedAt == null
                        && o.DueAt >= since
                        && userIds.Contains(o.AssignedUserId))
            .GroupBy(o => o.AssignedUserId)
            .Select(g => new { UserId = g.Key, Minutes = g.Sum(o => o.EffortMinutes) })
            .ToListAsync();

        return rows.ToDictionary(r => r.UserId, r => r.Minutes, StringComparer.Ordinal);
    }

    /// <summary>Who should take the next occurrence.</summary>
    public async Task<string?> PickNextAssigneeAsync(
        Chore chore, string? excludeUserId = null, DateTimeOffset? dueAt = null)
    {
        var pool = await GetRotationPoolAsync(chore, dueAt);
        if (excludeUserId is not null && pool.Count > 1) pool.Remove(excludeUserId);
        if (pool.Count == 0) return null;
        if (pool.Count == 1) return pool[0];

        var balances = await GetBalancesAsync(chore.GuildId, pool);
        var outstanding = await GetOutstandingLoadAsync(chore.GuildId, pool);

        // Occurrences staged in this unit of work but not yet committed.
        foreach (var entry in ctx.ChangeTracker.Entries<ChoreOccurrence>())
        {
            if (entry.State != EntityState.Added) continue;

            var staged = entry.Entity;
            if (staged.GuildId != chore.GuildId || !pool.Contains(staged.AssignedUserId)) continue;

            outstanding[staged.AssignedUserId] =
                outstanding.GetValueOrDefault(staged.AssignedUserId) + staged.EffortMinutes;
        }

        // Last time each candidate was assigned anything in this guild - the tiebreak that stops
        // two equally-idle members from being picked in id order forever.
        var lastAssigned = await ctx.ChoreOccurrences
            .AsNoTracking()
            .Where(o => o.GuildId == chore.GuildId && pool.Contains(o.AssignedUserId))
            .GroupBy(o => o.AssignedUserId)
            .Select(g => new { UserId = g.Key, Last = g.Max(o => o.DueAt) })
            .ToDictionaryAsync(x => x.UserId, x => x.Last);

        return balances
            .OrderBy(b => b.CompletedMinutes + outstanding.GetValueOrDefault(b.UserId))
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

        // Collapse a backlog before generating anything: a chore anchored months ago, or one whose
        // guild slept through an outage, has slots that will never be done and must not each
        // become a row. See Chore.FastForwardTo.
        chore.FastForwardTo(DateTimeOffset.UtcNow);

        var dueAt = chore.NextDueAt;

        var exists = await ctx.ChoreOccurrences.AnyAsync(o => o.ChoreId == chore.Id && o.DueAt == dueAt);
        if (exists)
        {
            chore.NextDueAt = chore.AdvanceFrom(dueAt);
            return null;
        }

        // Assigned for the date it is due on, not for today: a chore generated on Monday for the
        // following Sunday must miss whoever is away on the Sunday.
        var assignee = await PickNextAssigneeAsync(chore, dueAt: dueAt);
        if (assignee is null) return null;   // empty rotation pool - nothing to assign yet

        var occurrence = ChoreOccurrence.Create(chore, dueAt, assignee);
        ctx.ChoreOccurrences.Add(occurrence);

        chore.NextDueAt = chore.AdvanceFrom(dueAt);

        return occurrence;
    }
}
