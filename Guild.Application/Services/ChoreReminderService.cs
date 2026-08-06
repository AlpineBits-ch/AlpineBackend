using Guild.Domain.Entity;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Services;

/// <summary>
/// Tells the assignee their chore is due, once, and not in the middle of the night.
/// </summary>
public class ChoreReminderService(
    MicroserviceContext ctx,
    HouseholdNotifier notifier,
    ILogger<ChoreReminderService> logger)
{
    /// <summary>How far past a deferred reminder instant we still bother.</summary>
    private static readonly TimeSpan MaxLateness = TimeSpan.FromHours(12);

    private const int BatchSize = 200;

    public async Task<int> SendDueRemindersAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        var candidates = await ctx.ChoreOccurrences
            .Where(o => o.RemindedAt == null
                        && o.CompletedAt == null
                        && o.SkippedAt == null
                        && o.DueAt <= now)
            // Oldest first, so a backlog of abandoned occurrences is stamped out of the filtered
            // index in batches rather than being re-examined every five minutes forever.
            .OrderBy(o => o.DueAt)
            .Take(BatchSize)
            .ToListAsync(ct);

        if (candidates.Count == 0) return 0;

        var guildIds = candidates.Select(o => o.GuildId).Distinct().ToList();

        var quietHours = await ctx.GuildQuietHoursConfigs
            .AsNoTracking()
            .Where(c => guildIds.Contains(c.GuildId))
            .ToDictionaryAsync(c => c.GuildId, ct);

        var choreIds = candidates.Select(o => o.ChoreId).Distinct().ToList();
        var chores = await ctx.Chores
            .AsNoTracking()
            .Where(c => choreIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Title })
            .ToDictionaryAsync(c => c.Id, c => c.Title, ct);

        var sent = 0;

        foreach (var occurrence in candidates)
        {
            // The whole point of the deferral: a reminder that would land inside the quiet window
            // is moved to the end of it and simply is not sent yet.
            var fireAt = quietHours.TryGetValue(occurrence.GuildId, out var config)
                ? config.DeferPast(occurrence.DueAt)
                : occurrence.DueAt;

            if (fireAt > now) continue;

            if (now - fireAt > MaxLateness)
            {
                // Too stale to be useful, but stamped anyway - otherwise it stays in the candidate
                // set forever and the sweep re-examines it every five minutes for the rest of time.
                occurrence.RemindedAt = now;
                continue;
            }

            var title = chores.GetValueOrDefault(occurrence.ChoreId, "Chore");

            await notifier.AlertAsync(
                occurrence.GuildId,
                occurrence.ChannelId,
                [occurrence.AssignedUserId],
                HouseholdAlertService.KindChoreDue,
                title,
                "Your turn, and it's due now.",
                occurrence.Id,
                new { occurrence.ChoreId, occurrence.DueAt });

            occurrence.RemindedAt = now;
            sent++;
        }

        await ctx.SaveChangesAsync(ct);

        if (sent > 0) logger.LogDebug("Sent {Count} chore reminders", sent);

        return sent;
    }
}
