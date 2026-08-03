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

    /// <summary>Lower bound on age.</summary>
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
    /// <summary>Writes a batch.</summary>
    Task AddAsync(IReadOnlyCollection<UserMention> mentions);

    Task<IReadOnlyList<UserMention>> GetPageAsync(MentionPageQuery query);

    /// <summary>Removes one row.</summary>
    Task DeleteAsync(string userId, DateTimeOffset createdAt, string messageId);
}
