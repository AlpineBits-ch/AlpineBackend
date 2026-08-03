using Guild.Domain.Aggregates;
using Guild.Domain.Enums;
using Persistence;

namespace Guild.Domain.Entity;

/// <summary>
/// One `@everyone`, `@here` or `@role` ping. A single row per message, whoever it reached.
///
/// <para>These are not fanned out to per-user rows, because they do not have to be: the recipient
/// set is reconstructable from durable state at read time. `@everyone` is the members who joined
/// before it was sent; `@role` is the members who held that role when it was sent, which
/// <see cref="RoleMember.CreatedAt"/> and <see cref="RoleMember.ExpiresAt"/> pin down exactly. Only
/// direct `@user` mentions are indexed per user, and only because a message is otherwise unfindable
/// - Scylla partitions by context, so there is no way to ask "which messages named me".</para>
///
/// <para>Discord works the same way and its API shows it: a message carries `mention_roles` as raw
/// role ids and `mention_everyone` as a bare boolean, neither expanded, because the reader is what
/// evaluates them.</para>
///
/// <para>Lives in Guild's Postgres rather than beside the Scylla mention index: the unread query is
/// already a join over GuildMember, Channel and ReadState, and role membership, join dates and
/// ViewChannel all live here - so this folds into that query instead of costing a cross-service
/// call. Volume is negligible; `@everyone` is permission-gated.</para>
/// </summary>
public class ChannelBroadcastMention : BaseEntity<ChannelBroadcastMention>, IPrefixedEntity
{
    public static string Prefix { get; } = "bcme";

    public string ChannelId { get; set; } = null!;
    public virtual Channel Channel { get; set; } = null!;

    /// <summary>The message that carried the ping. Opaque - handed back to Messaging to resolve
    /// content, never compared.</summary>
    public string MessageId { get; set; } = null!;

    /// <summary>The message's own stored CreatedAt, not when this row was written. Every bound in
    /// the read-time predicate compares against it, so it has to be the message's instant.</summary>
    public DateTimeOffset MessageCreatedAt { get; set; }

    public string AuthorId { get; set; } = null!;

    public BroadcastMentionKind Kind { get; set; }

    /// <summary>Set only when <see cref="Kind"/> is <see cref="BroadcastMentionKind.Role"/>. One row
    /// per mentioned role, so a message naming three roles writes three.</summary>
    public string? RoleId { get; set; }
}
