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

    public async Task<IReadOnlyList<UserMention>> GetPageAsync(MentionPageQuery query)
    {
        var limit = Math.Clamp(query.Limit, 1, 100);

        // Not a guard against a silly caller: Scylla rejects LIMIT 0 outright, so a clamp that
        // allowed it would turn an empty page into a driver exception.
        var cql = "WHERE user_id = ?";
        var parameters = new List<object> { query.UserId };

        if (query.Before is not null)
        {
            // The cursor is (created_at, message_id) because two mentions can land in the same
            // millisecond - comparing on the timestamp alone would either skip the second one or
            // return it twice.
            cql += " AND (created_at, message_id) < (?, ?)";
            parameters.Add(query.Before.Value);
            parameters.Add(query.BeforeMessageId ?? string.Empty);
        }

        if (query.Since is not null)
        {
            cql += " AND created_at >= ?";
            parameters.Add(query.Since.Value);
        }

        cql += " LIMIT ?";
        parameters.Add(limit);

        // ToList() immediately: Mapper.FetchAsync returns a lazy projection over the driver's
        // RowSet, whose enumerator dequeues from an internal queue.
        return (await context.Mapper.FetchAsync<UserMention>(cql, parameters.ToArray())).ToList();
    }

    public Task DeleteAsync(string userId, DateTimeOffset createdAt, string messageId) =>
        context.Mapper.DeleteAsync<UserMention>(
            "WHERE user_id = ? AND created_at = ? AND message_id = ?", userId, createdAt, messageId);
}
