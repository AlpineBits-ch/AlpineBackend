using Guild.Domain.Aggregates;
using Guild.Domain.Enums;
using Persistence;

namespace Guild.Domain.Entity;

/// <summary>One `@everyone`, `@here` or `@role` ping.</summary>
public class ChannelBroadcastMention : BaseEntity<ChannelBroadcastMention>, IPrefixedEntity
{
    public static string Prefix { get; } = "bcme";

    public string ChannelId { get; set; } = null!;
    public virtual Channel Channel { get; set; } = null!;

    /// <summary>The message that carried the ping.</summary>
    public string MessageId { get; set; } = null!;

    /// <summary>The message's own stored CreatedAt, not when this row was written.</summary>
    public DateTimeOffset MessageCreatedAt { get; set; }

    public string AuthorId { get; set; } = null!;

    public BroadcastMentionKind Kind { get; set; }

    /// <summary>Set only when <see cref="Kind"/> is <see cref="BroadcastMentionKind.Role"/>. One row
    /// per mentioned role, so a message naming three roles writes three.</summary>
    public string? RoleId { get; set; }
}
