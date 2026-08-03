using Guild.Domain.Aggregates;
using Persistence;

namespace Guild.Domain.Entity;

public class ReadState : BaseEntity<ReadState>, IPrefixedEntity
{
    
    public string MemberId { get; set; } = null!;
    public GuildMember GuildMember { get; set; } = null!;
    public string ChannelId { get; set; } = null!;
    public Channel Channel { get; set; } = null!;
    public string? LastReadMessageId { get; set; } = null!;

    /// <summary>The stored CreatedAt of the acked message. The <b>only</b> field the unread
    /// predicate compares.
    ///
    /// Ids cannot serve here. Id generation moved to ULID, which sorts, but everything minted
    /// before that was upper-cased base62 which does not - and there is no backfill, so
    /// <c>Channel.LastMessageId &gt; LastReadMessageId</c> is wrong for any pre-existing account and
    /// stays wrong. <see cref="LastReadMessageId"/> remains an opaque cursor handed to Messaging;
    /// this is what decides whether anything is newer.
    ///
    /// Null means never acked, in which case the member's JoinedAt is the floor.</summary>
    public DateTimeOffset? LastReadAt { get; set; }

    /// <summary>Snapshot of <see cref="Aggregates.Channel.MessageCount"/> at ack time. The unread
    /// badge is the difference between the two, which is why a counter is stored rather than the
    /// count being queried: Messaging owns the messages, and asking it per channel per inbox open
    /// is the cost this whole design exists to avoid.
    ///
    /// Best-effort, exactly as MessageCount is - both drift under message loss - so it is display
    /// only and clamped at zero. The mention count beside it is exact.</summary>
    public int MessageCountAtRead { get; set; }

    // MentionCount used to live here as an incremented counter. It is derived now, from the mention
    // index and the broadcast-mention rows, for two reasons: incrementing was not idempotent (a
    // Wolverine retry doubled it, a permanent failure lost it, and deleting a message left it high),
    // and under the broadcast design there is no per-user write for an @everyone to increment in the
    // first place. The field survives on ReadStateDto so the wire shape did not change.

    public static string Prefix { get; } = "reta";
}