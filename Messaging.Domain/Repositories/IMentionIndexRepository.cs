using Messaging.Domain.Entities;

namespace Messaging.Domain.Repositories;

/// <summary>A page of the caller's mention index, newest first.</summary>
public record MentionPageQuery
{
    public required string UserId { get; init; }

    /// <summary>Exclusive upper bound - the clustering key of the last row of the previous page.
    /// Null for the first page. Paired with <see cref="BeforeMessageId"/> because two mentions can
    /// share a millisecond.</summary>
    public DateTimeOffset? Before { get; init; }

    public string? BeforeMessageId { get; init; }

    /// <summary>Lower bound on age. The tab offers 24h / 7d / 30d, and it is clamped to the row TTL -
    /// asking for older than the retention window returns nothing rather than lying.</summary>
    public DateTimeOffset? Since { get; init; }

    public int Limit { get; init; } = 25;
}

/// <summary>
/// The per-user mention index, behind an interface for the same reason
/// <see cref="IMessageRepository"/> is: self-hosted deployments run Postgres instead of Scylla, and
/// a Scylla-only implementation is a Scylla-only bug waiting to ship green.
/// </summary>
public interface IMentionIndexRepository
{
    /// <summary>Writes a batch. Idempotent by primary key - the fan-out is a bus command and
    /// Wolverine retries it, so a replay must not double a mention.</summary>
    Task AddAsync(IReadOnlyCollection<UserMention> mentions);

    Task<IReadOnlyList<UserMention>> GetPageAsync(MentionPageQuery query);

    /// <summary>
    /// Removes one row. Backs the X on a mention, and the lazy reap of rows whose message has since
    /// been deleted.
    ///
    /// <para>Deliberately keyed by the full primary key rather than by message id alone. The index
    /// is partitioned by user, so "delete every row for this message" is not a query Cassandra can
    /// answer without knowing who it mentioned - which is why a deleted message is reaped when a
    /// reader next trips over it rather than swept at delete time.</para>
    /// </summary>
    Task DeleteAsync(string userId, DateTimeOffset createdAt, string messageId);
}
