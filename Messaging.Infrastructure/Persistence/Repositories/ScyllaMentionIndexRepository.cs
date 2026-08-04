using Cassandra.Mapping;
using Messaging.Domain.Entities;
using Messaging.Domain.Repositories;

namespace Messaging.Infrastructure.Persistence.Repositories;

/// <summary>The mention index over Scylla.</summary>
public class ScyllaMentionIndexRepository(ScyllaContext context) : IMentionIndexRepository
{
    /// <summary>
    /// Rows expire on their own after 31 days - one past the longest lookback the tab offers - so
    /// there is no reaper to write, schedule or forget to run.
    /// </summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(31);

    private static int RetentionSeconds => (int)Retention.TotalSeconds;

    public async Task AddAsync(IReadOnlyCollection<UserMention> mentions)
    {
        if (mentions.Count == 0) return;

        // Sequential rather than Task.WhenAll: these are the same partition often enough that
        // parallelism buys little, and the caller already chunks the fan-out.
        foreach (var mention in mentions)
        {
            await context.Mapper.InsertAsync(mention, insertNulls: true, ttl: RetentionSeconds);
        }
    }

    /// <summary>
    /// Widest a single same-millisecond group may be before <see cref="ReadInstantAsync"/> stops
    /// trying to read all of it.
    /// </summary>
    private const int MaxSameInstantGroup = 10_000;

    /// <summary>One page of a user's mentions, newest first.</summary>
    public async Task<IReadOnlyList<UserMention>> GetPageAsync(MentionPageQuery query)
    {
        // Not a guard against a silly caller: Scylla rejects LIMIT 0 outright, so a clamp that
        // allowed it would turn an empty page into a driver exception.
        var limit = Math.Clamp(query.Limit, 1, 100);

        var rows = new List<UserMention>();

        if (query.Before is not null)
        {
            // The cursor's own millisecond, read whole and split by message_id - the only part of
            // the range where the tie-break matters relative to the cursor.
            var beforeId = query.BeforeMessageId ?? string.Empty;

            rows.AddRange((await ReadInstantAsync(query.UserId, query.Before.Value))
                .Where(m => string.CompareOrdinal(m.MessageId, beforeId) < 0)
                .Where(m => query.Since is null || m.CreatedAt >= query.Since.Value));
        }

        var remaining = limit - rows.Count;
        if (remaining > 0)
        {
            // Single-column relations only.
            var cql = "WHERE user_id = ?";
            var parameters = new List<object> { query.UserId };

            if (query.Before is not null)
            {
                cql += " AND created_at < ?";
                parameters.Add(query.Before.Value);
            }

            if (query.Since is not null)
            {
                cql += " AND created_at >= ?";
                parameters.Add(query.Since.Value);
            }

            cql += " LIMIT ?";
            parameters.Add(remaining);

            // ToList() immediately: Mapper.FetchAsync returns a lazy projection over the driver's
            // RowSet, whose enumerator dequeues from an internal queue.
            var slice = (await context.Mapper.FetchAsync<UserMention>(cql, parameters.ToArray())).ToList();

            // A short slice exhausted the range, so whatever millisecond it ends on is already
            // whole.
            if (slice.Count >= remaining)
            {
                var boundary = slice[^1].CreatedAt;
                var wholeGroup = await ReadInstantAsync(query.UserId, boundary);
                slice = slice.Where(m => m.CreatedAt != boundary).Concat(wholeGroup).ToList();
            }

            rows.AddRange(slice);
        }

        return rows
            .OrderByDescending(m => m.CreatedAt)
            .ThenByDescending(m => m.MessageId, StringComparer.Ordinal)
            .Take(limit)
            .ToList();
    }

    /// <summary>Every mention of one user sharing one <c>created_at</c>.</summary>
    private async Task<List<UserMention>> ReadInstantAsync(string userId, DateTimeOffset instant) =>
        (await context.Mapper.FetchAsync<UserMention>(
            "WHERE user_id = ? AND created_at = ? LIMIT ?", userId, instant, MaxSameInstantGroup)).ToList();

    public Task DeleteAsync(string userId, DateTimeOffset createdAt, string messageId) =>
        context.Mapper.DeleteAsync<UserMention>(
            "WHERE user_id = ? AND created_at = ? AND message_id = ?", userId, createdAt, messageId);
}
