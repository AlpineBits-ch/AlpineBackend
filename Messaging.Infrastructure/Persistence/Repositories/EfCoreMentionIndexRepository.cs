using Messaging.Domain.Entities;
using Messaging.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Messaging.Infrastructure.Persistence.Repositories;

/// <summary>
/// The mention index over Postgres, for self-hosted deployments that run without Scylla.
///
/// <para>Written alongside the Scylla implementation rather than after it. The two have shipped out
/// of step before - a bug that only existed on the Scylla path went green in CI because only the EF
/// path was covered - so both are implemented and tested together against the same assertions.</para>
///
/// <para>Postgres has no per-row TTL, so retention is a sweep instead of an expiry. Until it runs,
/// rows past the window are still filtered out on read: the query clamps <c>Since</c> to the
/// retention floor, so the two backends answer identically even when the sweep is behind.</para>
/// </summary>
public class EfCoreMentionIndexRepository(MicroserviceContext context) : IMentionIndexRepository
{
    public async Task AddAsync(IReadOnlyCollection<UserMention> mentions)
    {
        if (mentions.Count == 0) return;

        // The fan-out is a bus command and Wolverine retries it, so a replay must not double a
        // mention. Scylla gets this free from the primary key; here it has to be checked.
        var messageIds = mentions.Select(m => m.MessageId).Distinct().ToList();

        var existing = await context.UserMentions
            .AsNoTracking()
            .Where(m => messageIds.Contains(m.MessageId))
            .Select(m => new { m.UserId, m.MessageId })
            .ToListAsync();

        var seen = existing.Select(e => (e.UserId, e.MessageId)).ToHashSet();

        foreach (var mention in mentions)
        {
            if (!seen.Add((mention.UserId, mention.MessageId))) continue;
            context.UserMentions.Add(mention);
        }

        await context.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<UserMention>> GetPageAsync(MentionPageQuery query)
    {
        var limit = Math.Clamp(query.Limit, 1, 100);
        var floor = DateTimeOffset.UtcNow - ScyllaMentionIndexRepository.Retention;
        var since = query.Since is null || query.Since < floor ? floor : query.Since.Value;

        var rows = context.UserMentions
            .AsNoTracking()
            .Where(m => m.UserId == query.UserId && m.CreatedAt >= since);

        if (query.Before is not null)
        {
            // Keyset on (CreatedAt, MessageId), matching Scylla's tuple comparison - two mentions
            // can share a millisecond, and comparing the timestamp alone would skip or repeat one.
            var before = query.Before.Value;
            var beforeId = query.BeforeMessageId ?? string.Empty;

            rows = rows.Where(m =>
                m.CreatedAt < before
                || (m.CreatedAt == before && string.Compare(m.MessageId, beforeId) < 0));
        }

        return await rows
            .OrderByDescending(m => m.CreatedAt)
            .ThenByDescending(m => m.MessageId)
            .Take(limit)
            .ToListAsync();
    }

    public async Task DeleteAsync(string userId, DateTimeOffset createdAt, string messageId)
    {
        var row = await context.UserMentions
            .FirstOrDefaultAsync(m => m.UserId == userId && m.MessageId == messageId);

        if (row is null) return;

        context.UserMentions.Remove(row);
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Retention sweep, standing in for Scylla's TTL. Called by the periodic job.
    ///
    /// Set-based on Postgres - the point of a sweep is not to load a million rows to delete them.
    /// The InMemory provider used by the unit suite has no ExecuteDelete, so it falls back to
    /// load-and-remove there; same convention this DbContext already uses for the search index's
    /// tsvector column.
    /// </summary>
    public async Task<int> PurgeExpiredAsync()
    {
        var cutoff = DateTimeOffset.UtcNow - ScyllaMentionIndexRepository.Retention;
        var expired = context.UserMentions.Where(m => m.CreatedAt < cutoff);

        if (context.Database.IsRelational()) return await expired.ExecuteDeleteAsync();

        var rows = await expired.ToListAsync();
        context.UserMentions.RemoveRange(rows);
        await context.SaveChangesAsync();
        return rows.Count;
    }
}
