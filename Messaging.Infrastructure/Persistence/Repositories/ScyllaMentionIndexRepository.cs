using Cassandra.Mapping;
using Messaging.Domain.Entities;
using Messaging.Domain.Repositories;

namespace Messaging.Infrastructure.Persistence.Repositories;

/// <summary>
/// The mention index over Scylla. Partitioned by user, clustered newest-first, so a page is a
/// contiguous slice of one partition - the read this index exists to make possible.
/// </summary>
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
    /// trying to read all of it. A user would need this many mentions in one millisecond to reach
    /// it; the cap exists so a corrupt or adversarial partition cannot turn one page read into an
    /// unbounded one.
    /// </summary>
    private const int MaxSameInstantGroup = 10_000;

    /// <summary>
    /// One page of a user's mentions, newest first.
    ///
    /// <para><b>The cursor is deliberately not a CQL row tuple, for the same reason
    /// <c>ScyllaMessageRepository.FetchRelativeAsync</c>'s is not.</b> <c>user_mentions</c> is
    /// clustered <c>(created_at DESC, message_id ASC)</c> - mixed directions - while a multi-column
    /// relation <c>(created_at, message_id) &lt; (?, ?)</c> is plain ascending-lexicographic on both
    /// components. So inside one millisecond the scan runs message_id <i>ascending</i> while the
    /// cursor compares it <i>descending</i>: a page cap landing in a group keeps the members
    /// furthest from the cursor, and the next page - resuming from one of those - re-serves rows it
    /// has already shown while permanently skipping the ones between. Two mentions in one
    /// millisecond is all it takes, and the cluster reports nothing. Verified against a live node;
    /// see <c>ScyllaMentionIndexPagingTests</c>.</para>
    ///
    /// <para><b>So the tie-break is resolved here rather than by the cluster.</b> The cursor's own
    /// millisecond is read whole and split by message_id; the rest is an ordinary single-column
    /// slice whose boundary millisecond is completed the same way before anything is trimmed. The
    /// page is then ordered <c>(created_at DESC, message_id DESC)</c> - <b>which is what the
    /// relational twin already returned</b> (see <c>EfCoreMentionIndexRepository.GetPageAsync</c>),
    /// and which this backend previously did not: it handed back raw scan order, so the two backends
    /// disagreed about the order of any two mentions sharing a millisecond.</para>
    /// </summary>
    public async Task<IReadOnlyList<UserMention>> GetPageAsync(MentionPageQuery query)
    {
        // Not a guard against a silly caller: Scylla rejects LIMIT 0 outright, so a clamp that
        // allowed it would turn an empty page into a driver exception.
        var limit = Math.Clamp(query.Limit, 1, 100);

        var rows = new List<UserMention>();

        if (query.Before is not null)
        {
            // The cursor's own millisecond, read whole and split by message_id - the only part of
            // the range where the tie-break matters relative to the cursor. Ordinal, because that is
            // the comparison a clustering key on a text column performs; the culture-sensitive
            // default would order ids by rules the cluster has never heard of.
            var beforeId = query.BeforeMessageId ?? string.Empty;

            rows.AddRange((await ReadInstantAsync(query.UserId, query.Before.Value))
                .Where(m => string.CompareOrdinal(m.MessageId, beforeId) < 0)
                .Where(m => query.Since is null || m.CreatedAt >= query.Since.Value));
        }

        var remaining = limit - rows.Count;
        if (remaining > 0)
        {
            // Single-column relations only. No ORDER BY: created_at DESC is the table's own
            // clustering order, and asking for it explicitly would buy nothing while an ASC override
            // would silently reverse the tie-break column too.
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
            // RowSet, whose enumerator dequeues from an internal queue. It is single-pass and
            // self-consuming - enumerating it twice yields nothing the second time.
            var slice = (await context.Mapper.FetchAsync<UserMention>(cql, parameters.ToArray())).ToList();

            // A short slice exhausted the range, so whatever millisecond it ends on is already
            // whole. A full one may have been cut inside a group, and cut in the wrong place - so
            // that group is re-read in full and replaces what was read of it, before the sort below
            // picks which of its members are actually the newest.
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

    /// <summary>Every mention of one user sharing one <c>created_at</c>. Equality on the first
    /// clustering column, so this reads the group in the table's own order and needs no
    /// <c>ORDER BY</c> - which is the point, since an ORDER BY is what the tie-break cannot survive
    /// on this table.</summary>
    private async Task<List<UserMention>> ReadInstantAsync(string userId, DateTimeOffset instant) =>
        (await context.Mapper.FetchAsync<UserMention>(
            "WHERE user_id = ? AND created_at = ? LIMIT ?", userId, instant, MaxSameInstantGroup)).ToList();

    public Task DeleteAsync(string userId, DateTimeOffset createdAt, string messageId) =>
        context.Mapper.DeleteAsync<UserMention>(
            "WHERE user_id = ? AND created_at = ? AND message_id = ?", userId, createdAt, messageId);
}
